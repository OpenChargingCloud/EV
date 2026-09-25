/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EV <https://github.com/OpenChargingCloud/EV>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Diagnostics.CodeAnalysis;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node;

#endregion

namespace cloud.charging.open.EV
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": the sign-in,
    /// the configuration of this vehicle, its log, and one
    /// Server-Sent Events stream that carries everything that happens.
    /// </summary>
    /// <remarks>
    /// It lives in its own HTTPAPI so that unknown API paths never reach the
    /// single-page-application fallback of the web interface at "/": Hermod
    /// dispatches a request to the most specific HTTPAPI first.
    ///
    /// Everything below /api/v1 except the sign-in itself needs the session
    /// cookie - the event stream included, which is why the stream is opened
    /// here by hand rather than through Hermod's MapEventSource.
    /// </remarks>
    public class EVHTTPAPI : HTTPAPI
    {

        #region Data

        /// <summary>
        /// The default root path of this API.
        /// </summary>
        public static readonly HTTPPath  DefaultAPIPath      = WWCPNode.DefaultAPIPath;

        /// <summary>
        /// The identification of the Server-Sent Events source.
        /// </summary>
        public const           String    EventSourceName     = "events";

        /// <summary>
        /// The sub-event every log entry travels as.
        /// </summary>
        public const           String    LogEventName        = "log";

        /// <summary>
        /// How long an event stream stays silent before a comment is sent down
        /// it instead.
        /// </summary>
        /// <remarks>
        /// Silence is how an event stream waits, and a proxy in front of the
        /// vehicle cannot tell it from a vehicle that has gone: nginx gives up
        /// on an upstream that has sent nothing for 60 seconds. Fifteen seconds
        /// is what the HTML standard suggests for exactly this, and a browser
        /// skips a comment.
        /// </remarks>
        public static readonly TimeSpan  DefaultEventStreamHeartbeat = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long a failed sign-in waits before it answers. Not a lock-out,
        /// just enough to make guessing a slow business.
        /// </summary>
        public static readonly TimeSpan  FailedLoginDelay    = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// The most log entries one request may ask for.
        /// </summary>
        public const           Int32     MaxLogPageSize      = 2_000;

        /// <summary>
        /// How many log entries a request brings back when it does not say.
        /// </summary>
        public const           Int32     DefaultLogPageSize  = 500;

        private readonly DateTimeOffset  startedAt;

        /// <summary>
        /// Cancelled when this vehicle is shutting down, so that the event
        /// streams end.
        /// </summary>
        /// <remarks>
        /// A browser on the Logs page holds a request open that is not waiting
        /// on its socket but on the next log entry, so closing the socket under
        /// it does not end it - and an HTTP server that waits for every request
        /// it started would then never finish stopping. This is what ends them
        /// instead; see <see cref="CloseEventStreams"/>.
        /// </remarks>
        private readonly CancellationTokenSource  shutdown = new ();

        #endregion

        #region Properties

        /// <summary>
        /// The vehicle this API speaks for.
        /// </summary>
        public EV                    Vehicle   { get; }

        /// <summary>
        /// Everything that happens inside this vehicle.
        /// </summary>
        public EventLog                  Log       { get; }

        /// <summary>
        /// The signed-in browsers.
        /// </summary>
        /// <summary>
        /// Who may open the web interface: the accounts, and the groups whose
        /// membership carries this vehicle's roles.
        /// </summary>
        public HTTPExtAPI                ExtAPI    { get; }

        /// <summary>
        /// The version reported by the status resource.
        /// </summary>
        public String                    Version   { get; }

        /// <summary>
        /// The Server-Sent Events source every browser hangs on (/api/v1/events).
        /// </summary>
        public HTTPEventSource<JObject>  Events    { get; }

        /// <summary>
        /// How long an event stream stays silent before a comment is sent down
        /// it; <see cref="DefaultEventStreamHeartbeat"/> unless set, and never
        /// when set to zero.
        /// </summary>
        public TimeSpan                  EventStreamHeartbeat { get; set; } = DefaultEventStreamHeartbeat;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Vehicle">The vehicle this API speaks for.</param>
        /// <param name="ExtAPI">The accounts and the groups they are in.</param>
        /// <param name="Log">Everything that happens inside this vehicle.</param>
        /// <param name="APIPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        public EVHTTPAPI(HTTPServer  HTTPServer,
                         EV          Vehicle,
                         HTTPExtAPI  ExtAPI,
                         EventLog    Log,
                         HTTPPath?   APIPath   = null,
                         String?     Version   = null)

            : base(HTTPServer,
                   RootPath:     APIPath ?? DefaultAPIPath,
                   Description:  I18NString.Create("The JSON API of this vehicle"))

        {

            this.Vehicle   = Vehicle;
            this.ExtAPI    = ExtAPI;
            this.Log       = Log;
            this.startedAt = Vehicle.TimeProvider.GetUtcNow();

            this.Version   = Version
                                 ?? typeof(EVHTTPAPI).Assembly.GetName().Version?.ToString(3)
                                 ?? "0.0.0";

            // Hermod caches the last events and replays them to a new client.
            // The browser ignores everything older than the snapshot it loaded,
            // so a replay costs nothing but bytes; what it buys is that a
            // browser which reconnects after a hiccup gets what it missed.
            this.Events    = this.AddJSONEventSource(
                                 HTTPEventSource_Id.Parse(EventSourceName),
                                 MaxNumberOfCachedEvents:  500,
                                 RetryInterval:            TimeSpan.FromSeconds(2),
                                 EnableLogging:            false
                             );

            this.Log.OnLogged += entry => Publish(LogEventName, entry.ToJSON());

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        private void RegisterURLTemplates()
        {

            // No sign-in route here. Signing in happens at the HTTPExt API's
            // own "/ext/login", which is the only place that can check a
            // password: the check reads a store this API has no access to, and
            // a second door onto the same credentials is a second door to get
            // wrong. What this API does is read the cookie that door sets.
            AddHandler(HTTPPath.Root + "v1/auth/logout",   Logout,            HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/auth/me",       Me,                HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/status",        GetStatus,         HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/clock",         GetClock,          HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration", GetConfiguration,  HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/configuration/dns",        GetDNSConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/dns",        PutDNSConfiguration,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/dns/query",  PostDNSQuery,          HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/nts",        GetNTSConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/nts",        PutNTSConfiguration,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/nts/sync",   PostNTSSync,           HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/nts/test",   PostNTSTest,           HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/vehicle",    GetVehicleConfiguration,  HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/vehicle",    PutVehicleConfiguration,  HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        GetV2GConfiguration,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        PutV2GConfiguration,      HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g/discover", PostV2GDiscover,        HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g/pair",     PostSLACPair,           HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/session",    GetSessionConfiguration,  HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/session",    PutSessionConfiguration,  HTTPMethod.PUT);

            // The store is a collection and is addressed like one, which is why
            // it is not under "configuration/": what is in it is not a setting
            // that is read and written whole, it is a set of things that are
            // added, switched and removed one at a time.
            AddHandler(HTTPPath.Root + "v1/certificates",             GetCertificates,          HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/certificates",             PostCertificate,          HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/certificates/reload",      PostCertificateReload,    HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/certificates/{id}",        GetCertificate,           HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/certificates/{id}",        PatchCertificate,         HTTPMethod.PATCH);
            AddHandler(HTTPPath.Root + "v1/certificates/{id}",        DeleteCertificate,        HTTPMethod.DELETE);

            AddHandler(HTTPPath.Root + "v1/session",                  GetSessionConfiguration,  HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/session",                  PostSessionStart,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/session/stop",             PostSessionStop,          HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/logs",          GetLogs,           HTTPMethod.GET);

            AddHandler(HTTPMethod.GET,
                       HTTPPath.Root + "v1/events",
                       HTTPContentType.Text.EVENTSTREAM,
                       StreamEvents);

            // Everything else below /api answers with a JSON 404 instead of
            // the single-page-application stub of the web interface.
            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.POST, HTTPMethod.PUT,
                                           HTTPMethod.PATCH, HTTPMethod.DELETE })
                AddHandler(HTTPPath.Root + "{path..}", UnknownPath, method);

        }

        #endregion


        #region (private) Logout          (Request)

        /// <summary>
        /// POST /api/v1/auth/logout: ends the session and expires the cookie.
        /// </summary>
        private Task<HTTPResponse> Logout(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            // The session is ended where it lives, and not only forgotten by
            // this browser: a cookie that is merely expired is still a valid
            // token to whoever copied it.
            if (Request.Cookies is not null                                                      &&
                Request.Cookies.TryGet(ExtAPI.SessionCookieName, out var cookie)                 &&
                cookie is not null                                                               &&
                SecurityToken_Id.TryParse(cookie.FirstOrDefault().Key, out var securityTokenId))
            {

                ExtAPI.Sessions.Remove(securityTokenId);

                Log.Notice($"A session was ended from {Request.RemoteSocket}.", "web", "auth");

            }

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.NoContent,
                           CacheControl    = "no-store",
                           SetCookie       = ExpiredSessionCookie()
                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        #endregion

        #region (private) Me              (Request)

        /// <summary>
        /// GET /api/v1/auth/me: who is signed in, or 401.
        /// </summary>
        private Task<HTTPResponse> Me(HTTPRequest Request)

            => Task.FromResult(
                   TryGetUser(Request, out var user, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, MeJSON(user))
                       : unauthorized
               );

        #endregion


        #region (private) GetStatus       (Request)

        /// <summary>
        /// GET /api/v1/status: how this vehicle is doing right now.
        /// </summary>
        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var now = Vehicle.TimeProvider.GetUtcNow();

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("service",    "EV"),
                               new JProperty("version",    Version),
                               new JProperty("hermod",     typeof(HTTPServer).Assembly.GetName().Version?.ToString(3)),
                               new JProperty("timestamp",  now.ToString("o")),
                               new JProperty("startedAt",  startedAt.ToString("o")),
                               new JProperty("uptime",     (now - startedAt).ToString(@"d\.hh\:mm\:ss")),
                               new JProperty("sessions",   ExtAPI.Sessions.Count()),
                               new JProperty("log",        new JObject(
                                                               new JProperty("entries",   Log.Count),
                                                               new JProperty("capacity",  Log.Capacity),
                                                               new JProperty("lastId",    Log.LastId),
                                                               new JProperty("tags",      new JArray(Log.KnownTags))
                                                           ))
                           )
                       )
                   );

        }

        #endregion

        #region (private) GetClock        (Request)

        /// <summary>
        /// GET /api/v1/clock: what time it is here, whether it has been checked,
        /// against whom and how long ago, and whether all of that adds up to
        /// legal time.
        /// </summary>
        /// <remarks>
        /// For anybody signed in, as the status is: a screen that shows the time
        /// has to be able to say what it is worth, and that is no secret of the
        /// configuration. Nothing here changes anything - the servers and the
        /// rules are the NTS page's, and "legal" is decided by the vehicle and
        /// sent as a fact, never worked out by whoever reads it.
        ///
        /// The JSON was there before the route: written with the web interface
        /// and never served, so a screen could not ask it and a wrong name in it
        /// went unnoticed until it was read for another reason.
        /// </remarks>
        private Task<HTTPResponse> GetClock(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.ClockJSON())
                   );

        }

        #endregion

        #region (private) GetConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration: what this vehicle is made of.
        /// </summary>
        private Task<HTTPResponse> GetConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.ConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetDNSConfiguration(Request) / PutDNSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/dns: how this vehicle resolves names.
        /// </summary>
        private Task<HTTPResponse> GetDNSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.DNSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/dns: change what may be changed about it.
        /// Answers with the whole configuration as it now stands, so that the
        /// page does not have to ask again to find out what it got.
        /// </summary>
        private Task<HTTPResponse> PutDNSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Vehicle.TryUpdateDNSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.DNSConfigurationJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/dns/query with {"name", "recordTypes"}:
        /// make this vehicle look a name up and say what came back.
        /// </summary>
        /// <remarks>
        /// A POST although it changes nothing here, because it makes this
        /// vehicle send traffic to a host somebody named - which is not
        /// something to leave sitting in a URL that a browser may repeat,
        /// prefetch or put in a history.
        /// </remarks>
        private async Task<HTTPResponse> PostDNSQuery(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var name = json.Value<String>("name")?.Trim();

            if (String.IsNullOrEmpty(name))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "A 'name' to look up is required.");

            if (!EV.TryParseRecordTypes(json["recordTypes"], out var recordTypes, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            Log.Info($"'{user.Id}' asked this vehicle to resolve '{name}'.", "dns", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Vehicle.ResolveAsync(name,
                                                  recordTypes,
                                                  json.Value<Int32?>("server"),
                                                  Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetNTSConfiguration(Request) / PutNTSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/nts: where this vehicle gets the time from.
        /// </summary>
        private Task<HTTPResponse> GetNTSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/nts: change what may be changed about it.
        /// </summary>
        private Task<HTTPResponse> PutNTSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Vehicle.TryUpdateNTSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/nts/sync: one key exchange and one
        /// authenticated NTP request, with every step in the log.
        /// </summary>
        /// <remarks>
        /// Answers with the whole NTS configuration and not only with the
        /// result, because an exchange moves the cookie pool, the key material
        /// and the record of the last exchange - all of which the page is
        /// showing while it waits.
        /// </remarks>
        private async Task<HTTPResponse> PostNTSSync(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            Log.Info($"'{user.Id}' asked this vehicle to synchronise its time.", "nts", "test", "web");

            var result = await Vehicle.SyncTimeAsync(Request.CancellationToken);

            var json   = Vehicle.NTSConfigurationJSON();

            json["result"] = result;

            return JSONResponse(Request, HTTPStatusCode.OK, json);

        }

        #endregion

        #region (private) PostNTSTest(Request)

        /// <summary>
        /// POST /api/v1/configuration/nts/test with an optional {"host"}: ask
        /// one time server everything there is to ask, and say where it got
        /// to.
        /// </summary>
        /// <remarks>
        /// The host is optional and names the server to ask; left out, it is
        /// the configured one. The key exchange may name NTP servers other
        /// than itself, and the page offers one of these per name - which is
        /// the whole reason this takes a host at all.
        ///
        /// At the diagnostics permission, with the other tests. Like "Sync
        /// now", it does not step the clock.
        /// </remarks>
        private async Task<HTTPResponse> PostNTSTest(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var host = json.Value<String>("host")?.Trim();

            Log.Info($"'{user.Id}' asked this vehicle to test {(host is null ? "its time server" : $"the time server '{host}'")}.",
                     "nts", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Vehicle.TestTimeServerAsync(host, Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetVehicleConfiguration(Request) / PutVehicleConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/vehicle: what this vehicle is, and what
        /// its battery wants.
        /// </summary>
        private Task<HTTPResponse> GetVehicleConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.VehicleConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/vehicle: change what may be changed about
        /// it. Answers with the whole configuration as it now stands, so that
        /// the page does not have to ask again to find out what it got.
        /// </summary>
        private Task<HTTPResponse> PutVehicleConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeChargingSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Vehicle.TryUpdateVehicleConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.VehicleConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetV2GConfiguration(Request) / PutV2GConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/v2g: the wire below the charging cable,
        /// from this vehicle's side.
        /// </summary>
        private Task<HTTPResponse> GetV2GConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.V2GConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/v2g: change what the next discovery does.
        /// </summary>
        /// <remarks>
        /// At the network permission and not at the charging one: which
        /// interface this vehicle broadcasts on is a statement about the
        /// machine it runs on, and getting it wrong sends multicast traffic
        /// onto somebody's office LAN rather than onto the powerline.
        /// </remarks>
        private Task<HTTPResponse> PutV2GConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Vehicle.TryUpdateV2GConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.V2GConfigurationJSON())
                   );

        }

        #endregion

        #region (private) PostV2GDiscover(Request)

        /// <summary>
        /// POST /api/v1/configuration/v2g/discover with an optional
        /// {"interface"}: multicast an SDP request onto the link and say what
        /// answered.
        /// </summary>
        /// <remarks>
        /// A POST although it changes nothing here, and for a stronger reason
        /// than the DNS query beside it: this one puts packets on a link every
        /// machine on that link receives. That is not something to leave
        /// sitting in a URL a browser may repeat, prefetch or put in a history.
        ///
        /// The interface is optional and names the one to broadcast on; left
        /// out, it is the configured one, and without that the first candidate.
        ///
        /// At the diagnostics permission, with the other tests: it asks a
        /// question and starts no session.
        ///
        /// Answers with the whole V2G configuration and not only with the
        /// result, because a discovery moves the record of the last one - which
        /// the page is showing while it waits.
        /// </remarks>
        private async Task<HTTPResponse> PostV2GDiscover(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var interfaceName = json.Value<String>("interface")?.Trim();

            if (interfaceName?.Length == 0)
                interfaceName = null;

            Log.Info(
                $"'{user.Id}' asked this vehicle to look for a station " +
                $"{(interfaceName is null ? "on its configured interface" : $"on '{interfaceName}'")}.",
                "15118", "sdp", "test", "web"
            );

            var result = await Vehicle.DiscoverAsync(interfaceName, Request.CancellationToken);

            var answer = Vehicle.V2GConfigurationJSON();

            answer["result"] = result;

            return JSONResponse(Request, HTTPStatusCode.OK, answer);

        }

        #endregion

        #region (private) GetSessionConfiguration(Request) / PutSessionConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/session: what this vehicle does once it
        /// has found a station, and how the last session went.
        /// </summary>
        private Task<HTTPResponse> GetSessionConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.SessionConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/session: change what the next session
        /// does.
        /// </summary>
        /// <remarks>
        /// Which permission this needs depends on what the request actually
        /// mentions - see <see cref="PermissionsForSession"/>. Three different
        /// kinds of statement live in one section, and asking for the union of
        /// them would mean a driver could not say how full they want the
        /// battery without also being trusted with the vehicle's identity.
        /// </remarks>
        private Task<HTTPResponse> PutSessionConfiguration(HTTPRequest Request)
        {

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!TryAuthorize(Request, PermissionsForSession(json), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!Vehicle.TryUpdateSessionConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.SessionConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetCertificates(Request) / PostCertificate(Request)

        /// <summary>
        /// GET /api/v1/certificates: everything in this vehicle's store.
        /// </summary>
        /// <remarks>
        /// Grouped by kind rather than returned as one list, because the page
        /// that reads it shows the roots this vehicle believes and the
        /// credentials it presents as two different things - and because a flat
        /// list would put an OEM root next to an OEM provisioning certificate
        /// with one word between them.
        ///
        /// At the reading permission: what certificates a vehicle holds is not
        /// a secret from anybody who may look at it at all, and the private
        /// keys are not in the answer. Changing any of it needs
        /// <see cref="Permissions.ManageCredentials"/>.
        /// </remarks>
        private Task<HTTPResponse> GetCertificates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.CertificatesJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/certificates with {"kind", "content", "password", "label"}:
        /// put a certificate into the store.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The file arrives as base64 in <c>content</c>, which is what an
        /// upload from the browser turns into. The password is what opens it if
        /// it is a protected PKCS#12, is used once here, and is not kept: the
        /// store writes what it holds without one.
        /// </para>
        /// <para>
        /// Answered with 200 rather than 201 when the certificate was already
        /// there. Importing the same file twice is the same entry - the id is
        /// its fingerprint - so the second import created nothing.
        /// </para>
        /// </remarks>
        private Task<HTTPResponse> PostCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCredentials, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!CertificateKindExtensions.TryParseKind(json.Value<String>("kind"), out var kind))
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                     "'kind' has to be one of " +
                                     String.Join(", ", CertificateKindExtensions.All.Select(one => one.AsText())) + ".")
                       );

            var content = json.Value<String>("content")?.Trim();

            if (content is null or { Length: 0 })
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                     "'content' has to be the certificate file, base64-encoded.")
                       );

            Byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(content);
            }
            catch (FormatException)
            {
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest, "'content' is not valid base64.")
                       );
            }

            var existed = Vehicle.Certificates.Entries.Count;

            if (!Vehicle.Certificates.Import(bytes,
                                             kind,
                                             json.Value<String>("password"),
                                             json.Value<String>("label"),
                                             out var entry,
                                             out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           Vehicle.Certificates.Entries.Count > existed
                               ? HTTPStatusCode.Created
                               : HTTPStatusCode.OK,
                           entry.ToJSON(WithDiagnostics: true)
                       )
                   );

        }

        #endregion

        #region (private) GetCertificate(Request) / PatchCertificate(Request) / DeleteCertificate(Request)

        /// <summary>
        /// GET /api/v1/certificates/{id}: one certificate.
        /// </summary>
        private Task<HTTPResponse> GetCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            var entry = Vehicle.Certificates.Get(HandleOf(Request));

            return Task.FromResult(
                       entry is null
                           ? ErrorJSON(Request, HTTPStatusCode.NotFound, "There is no such certificate in this store.")
                           : JSONResponse(Request, HTTPStatusCode.OK, entry.ToJSON(WithDiagnostics: true))
                   );

        }

        /// <summary>
        /// PATCH /api/v1/certificates/{id} with {"active"} and/or {"label"}:
        /// switch a certificate on or off, or rename it.
        /// </summary>
        /// <remarks>
        /// Two things in one request because they are the only two things about
        /// a stored certificate that can be changed at all - everything else
        /// about it is read out of the file and is not somebody's to edit.
        /// </remarks>
        private Task<HTTPResponse> PatchCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCredentials, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var handle = HandleOf(Request);

            if (Vehicle.Certificates.Get(handle) is null)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.NotFound, "There is no such certificate in this store.")
                       );

            if (json.TryGetValue("label", out var label) && label.Type != JTokenType.Undefined)
            {
                if (!Vehicle.Certificates.Relabel(handle, label.Value<String>(), out _, out var relabelError))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, relabelError));
            }

            if (json.TryGetValue("active", out var active))
            {

                if (active.Type != JTokenType.Boolean)
                    return Task.FromResult(
                               ErrorJSON(Request, HTTPStatusCode.BadRequest, "'active' has to be true or false.")
                           );

                if (!Vehicle.Certificates.SetActive(handle, active.Value<Boolean>(), out _, out var activeError))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, activeError));

            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK,
                                    Vehicle.Certificates.Get(handle)!.ToJSON(WithDiagnostics: true))
                   );

        }

        /// <summary>
        /// DELETE /api/v1/certificates/{id}: take a certificate out of the
        /// store and delete its file.
        /// </summary>
        /// <remarks>
        /// Refused while a session setting still names it, and named in the
        /// refusal. Deleting it anyway would leave a vehicle configured to
        /// present something that is not there, which is discovered at the next
        /// session rather than here - and switching it off is what somebody
        /// taking a certificate out of service usually meant.
        /// </remarks>
        private Task<HTTPResponse> DeleteCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCredentials, true, out _, out var refused))
                return Task.FromResult(refused);

            var handle = HandleOf(Request);

            if (Vehicle.UsedBySession(handle) is { } field)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.Conflict,
                                     $"That certificate is what 'session.{field}' names. Choose another one there " +
                                      "first, or switch this one off instead of deleting it.")
                       );

            if (!Vehicle.Certificates.Remove(handle, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.CertificatesJSON())
                   );

        }

        #endregion

        #region (private) PostCertificateReload(Request)

        /// <summary>
        /// POST /api/v1/certificates/reload: read the store directory again.
        /// </summary>
        /// <remarks>
        /// What the store does at every start, on demand: certificates somebody
        /// copied into the directory are adopted, and entries whose files are
        /// gone are dropped. It exists because putting a file in a directory is
        /// a perfectly good way to install a certificate on a machine somebody
        /// already has a shell on, and having to restart the vehicle to be
        /// noticed would make it a worse one.
        /// </remarks>
        private Task<HTTPResponse> PostCertificateReload(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCredentials, true, out _, out var refused))
                return Task.FromResult(refused);

            Vehicle.Certificates.Reload();

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.CertificatesJSON())
                   );

        }

        #endregion

        #region (private static) HandleOf(Request)

        /// <summary>
        /// The certificate handle out of the request's path.
        /// </summary>
        private static String HandleOf(HTTPRequest Request)

            => Request.ParsedURLParameters.Length > 0
                   ? Request.ParsedURLParameters[0].Trim()
                   : "";

        #endregion

        #region (private static) PermissionsForSession(JSON)

        /// <summary>
        /// What a change to the session settings needs, worked out from what it
        /// changes.
        /// </summary>
        /// <remarks>
        /// The three kinds are different in kind and not only in degree. Saying
        /// how much energy the driver wants is a daily decision anybody who may
        /// drive the vehicle makes. Saying which interface or which protocol is
        /// a statement about the machine and the counterparty. Naming the
        /// certificate the vehicle is known by is a statement about who this
        /// vehicle <i>is</i>, which is why it sits behind the one permission
        /// not even the owner gets by default.
        ///
        /// Every field the request does not mention costs nothing, so a page
        /// that offers one card asks for one permission.
        /// </remarks>
        private static Permissions PermissionsForSession(JObject JSON)
        {

            var required = Permissions.None;

            foreach (var field in new[] { "pkiDirectory", "vehicleCertificate",
                                          "contractCertificate", "oemCertificate", "tariffCertificate" })
                if (JSON.ContainsKey(field))
                    required |= Permissions.ManageCredentials;

            foreach (var field in new[] { "connect", "protocol", "mode", "tls", "slacPeer",
                                          "t1sBus", "t1sTransport", "t1sInterface", "t1sWeight", "renegotiate" })
                if (JSON.ContainsKey(field))
                    required |= Permissions.ChangeNetworkSettings;

            foreach (var field in new[] { "targetEnergyKWh", "maxChargingTimeSeconds",
                                          "departureInSeconds", "minimumStateOfChargePercent" })
                if (JSON.ContainsKey(field))
                    required |= Permissions.ChangeChargingSettings;

            // An empty request changes nothing and is answered as a read, which
            // is what it is.
            return required == Permissions.None
                       ? Permissions.ReadConfiguration
                       : required;

        }

        #endregion

        #region (private) PostSessionStart(Request) / PostSessionStop(Request)

        /// <summary>
        /// POST /api/v1/session with an optional {"connect", "pause",
        /// "resume", "pauseResume"}: drive up to a station and charge.
        /// </summary>
        /// <remarks>
        /// <b>This answers before the session is over, and on purpose.</b> A
        /// session is hundreds of exchanges and a full charge is minutes, which
        /// is longer than any browser will hold a request open and much longer
        /// than it should have to. So this says the session has started and
        /// then gets out of the way: the exchange itself arrives on the event
        /// stream while it happens, and the sum lands in the session resource
        /// when it ends.
        ///
        /// Which is also why the session does not run on this request's
        /// cancellation token. A request that has already been answered has a
        /// cancelled token, and a session tied to it would end the moment it
        /// was reported as started. <see cref="EV.CancelSession"/> is what ends
        /// one, and POST .../stop is how somebody asks for that.
        /// </remarks>
        private async Task<HTTPResponse> PostSessionStart(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunSessions, true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var connect      = json.Value<String>("connect")?.Trim();
            var resume       = json.Value<String>("resume")?.Trim();
            var pause        = json.Value<Boolean?>("pause")       ?? false;
            var pauseResume  = json.Value<Boolean?>("pauseResume") ?? false;

            if (connect?.Length == 0)  connect = null;
            if (resume?.Length  == 0)  resume  = null;

            if (resume is not null && !IsHexadecimal(resume))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                 "'resume' is a paused session's identification in hexadecimal.");

            if (Vehicle.SessionRunning)
                return ErrorJSON(Request, HTTPStatusCode.Conflict,
                                 "A session is already running on this vehicle. Stop it first, or wait for it to end.");

            Log.Info($"'{user.Id}' asked this vehicle to charge " +
                     $"{(connect is null ? "at whichever station it finds" : $"at {connect}")}.",
                     "15118", "session", "web");

            // Started here and not awaited: see the remarks above.
            var run = Vehicle.RunSessionAsync(connect, pause, resume, pauseResume);

            // RunSessionAsync takes the vehicle's session lock and marks itself
            // running before its first real await, so by this line the vehicle
            // already says so. Waited for all the same rather than reasoned
            // about, because the cost of being wrong is not a slow page: a
            // browser that reloads on this answer and finds "not running" draws
            // the *previous* session's result as though it were this one's, and
            // then stops asking.
            while (!Vehicle.SessionRunning && !run.IsCompleted)
                await Task.Delay(5, Request.CancellationToken);

            // Observed rather than abandoned. RunSessionAsync answers instead of
            // throwing, so this is only the unforeseen half - but an exception
            // nobody ever looks at is one that disappears.
            _ = run.ContinueWith(
                    finished => Log.Error($"Session: {finished.Exception?.GetBaseException().Message}", "15118", "session"),
                    TaskContinuationOptions.OnlyOnFaulted
                );

            return JSONResponse(
                       Request,
                       HTTPStatusCode.Accepted,
                       new JObject(
                           new JProperty("outcome",  "started"),
                           new JProperty("watch",    "The exchange is on the event stream while it happens; " +
                                                     "this resource carries the result when it ends.")
                       )
                   );

        }

        /// <summary>
        /// POST /api/v1/session/stop: end the session that is running.
        /// </summary>
        /// <remarks>
        /// What has already been metered stays metered - this ends the
        /// exchange, it does not undo it.
        /// </remarks>
        private Task<HTTPResponse> PostSessionStop(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunSessions, true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!Vehicle.SessionRunning)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.Conflict, "No session is running on this vehicle.")
                       );

            Log.Info($"'{user.Id}' asked this vehicle to stop charging.", "15118", "session", "web");

            Vehicle.CancelSession();

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, new JObject(new JProperty("outcome", "stopping")))
                   );

        }

        #endregion

        #region (private) PostSLACPair(Request)

        /// <summary>
        /// POST /api/v1/configuration/v2g/pair: run the SLAC pairing stage on
        /// its own.
        /// </summary>
        /// <remarks>
        /// A session runs this by itself where a peer is configured. It is also
        /// here on its own because it is the half that fails first and fails
        /// differently: SLAC agreeing and SDP then finding nothing is a very
        /// different link from SLAC never agreeing at all, and being able to
        /// ask the two questions separately is what tells them apart.
        ///
        /// Seconds rather than minutes, so unlike a session this one is awaited.
        /// </remarks>
        private async Task<HTTPResponse> PostSLACPair(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            if (Vehicle.SessionSettings.SLACPeer is null)
                return ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                 "No SLAC peer is configured. Real SLAC needs a powerline modem and AF_PACKET; " +
                                 "this runs the same state machine over a simulated medium against a station that agreed to do the same.");

            Log.Info($"'{user.Id}' asked this vehicle to pair over SLAC.", "15118", "slac", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Vehicle.PairAsync(Request.CancellationToken)
                   );

        }

        #endregion

        #region (private static) IsHexadecimal(Text)

        /// <summary>
        /// Whether this is a session identification as one is written down: an
        /// even number of hexadecimal digits, and nothing else.
        /// </summary>
        /// <remarks>
        /// Checked here rather than left to Convert.FromHexString, whose
        /// refusal is a FormatException with no idea which field it was about.
        /// </remarks>
        private static Boolean IsHexadecimal(String Text)

            => Text.Length > 0 &&
               Text.Length % 2 == 0 &&
               Text.All(Uri.IsHexDigit);

        #endregion

        #region (private) GetLogs         (Request)

        /// <summary>
        /// GET /api/v1/logs?limit=&amp;after=&amp;tag=: what happened, oldest
        /// of the returned entries first.
        /// </summary>
        /// <remarks>
        /// This is the snapshot a browser loads before it starts following the
        /// event stream; "lastId" says how far it reaches, and everything the
        /// stream delivers with a greater id is new.
        /// </remarks>
        private Task<HTTPResponse> GetLogs(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var limit    = Request.QueryString.GetInt32 ("limit") ?? DefaultLogPageSize;
            var after    = Request.QueryString.GetUInt64("after");
            var tag      = Request.QueryString.GetString("tag");

            if (limit < 1 || limit > MaxLogPageSize)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'limit' must be between 1 and {MaxLogPageSize}.")
                       );

            var entries  = Log.Recent(limit, after, tag).ToArray();

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               // The whole log's last id and not the last of
                               // this page: a page filtered by a tag would
                               // otherwise make the browser ask again for
                               // everything between the two.
                               new JProperty("lastId",   Log.LastId),
                               new JProperty("capacity", Log.Capacity),
                               new JProperty("tags",     new JArray(Log.KnownTags)),
                               new JProperty("entries",  new JArray(entries.Select(entry => entry.ToJSON())))
                           )
                       )
                   );

        }

        #endregion


        #region (private) StreamEvents    (Request)

        /// <summary>
        /// GET /api/v1/events: the Server-Sent Events stream every browser
        /// hangs on. Modelled on Hermod's MapEventSource, with the session
        /// checked first and without opening the stream to other origins.
        /// </summary>
        /// <remarks>
        /// Two things in here are for a proxy in front of the vehicle, and both
        /// were learned from nginx as it comes.
        ///
        /// "X-Accel-Buffering: no", because nginx buffers what it passes on,
        /// and a buffered event stream reaches the browser as nothing at all -
        /// not even its header - until a buffer is full or the vehicle has been
        /// silent long enough for nginx to give up on it. The browser never saw
        /// the stream open, so the Logs page said "reconnecting ..." and did not
        /// ask for its snapshot either: behind nginx the header came after 72
        /// seconds, and the stream ended 98 ms later.
        ///
        /// And a comment whenever the stream has been silent for
        /// <see cref="EventStreamHeartbeat"/>, because the 60 seconds after
        /// which nginx gives up are an ordinary pause for a vehicle nobody is
        /// using.
        /// </remarks>
        private Task<HTTPResponse> StreamEvents(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out var reader, out var unauthorized))
                return Task.FromResult(unauthorized);

            var clientId    = Request.RemoteSocket.ToString();

            // Asked before every event and at every heartbeat - see StillLetIn().
            var stillLetIn  = StillLetIn(Request, reader);

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {

                           HTTPStatusCode  = HTTPStatusCode.OK,
                           Server          = HTTPServer.HTTPServerName,
                           ContentType     = HTTPContentType.Text.EVENTSTREAM,
                           CacheControl    = "no-cache",
                           Connection      = ConnectionType.KeepAlive,

                           HTTPSSEWorker   = async (response, stream) => {

                               // Either the browser going away or this vehicle
                               // shutting down ends the stream. The second one
                               // is not something the request's own token knows
                               // about - see CloseEventStreams().
                               using var ending = CancellationTokenSource.CreateLinkedTokenSource(
                                                      Request.CancellationToken,
                                                      shutdown.Token
                                                  );

                               try
                               {

                                   await stream.WriteAsync("retry: ");
                                   await stream.WriteAsync(((UInt32) Events.RetryInterval.TotalMilliseconds).ToString());
                                   await stream.WriteAsync("\n\n");

                                   // The preamble has to leave the buffer now,
                                   // not with the first event: on a quiet
                                   // vehicle the browser would otherwise wait
                                   // for its first byte until its own read
                                   // timeout expired.
                                   await stream.FlushAsync(ending.Token);

                                   var heartbeat  = EventStreamHeartbeat > TimeSpan.Zero
                                                        ? EventStreamHeartbeat
                                                        : Timeout.InfiniteTimeSpan;

                                   await using var events = Events.GetAllEventsGreater(
                                                                clientId,
                                                                Request.GetHeaderField(HTTPRequestHeaderField.LastEventId),
                                                                ending.Token
                                                            ).GetAsyncEnumerator(ending.Token);

                                   // The next event is waited for across heartbeats
                                   // rather than asked for again: an enumerator
                                   // takes one question at a time.
                                   var next = events.MoveNextAsync().AsTask();

                                   // Set when the stream ends because its session did.
                                   var signedOut = false;

                                   try
                                   {

                                       while (true)
                                       {

                                           try
                                           {
                                               if (!await next.WaitAsync(heartbeat, ending.Token))
                                                   break;
                                           }
                                           catch (TimeoutException)
                                           {

                                               // A quiet stream is asked as well, or one
                                               // whose session ended would go on for as
                                               // long as nothing was logged.
                                               if (!stillLetIn())
                                               {
                                                   signedOut = true;
                                                   break;
                                               }

                                               await stream.WriteHeartbeat(CancellationToken: ending.Token);
                                               continue;

                                           }

                                           // Asked before the event is written, not after:
                                           // what was logged after the sign-out is not sent
                                           // to the session that signed out.
                                           if (!stillLetIn())
                                           {
                                               signedOut = true;
                                               break;
                                           }

                                           var httpEvent = events.Current;

                                           await stream.WriteAsync(httpEvent.SerializedHeader);
                                           await stream.WriteAsync(httpEvent.SerializedData);
                                           await stream.WriteAsync("\n\n");
                                           await stream.FlushAsync(ending.Token);

                                           next = events.MoveNextAsync().AsTask();

                                       }

                                   }
                                   finally
                                   {
                                       // However the loop ended, the enumerator may
                                       // still be waiting for the next event - a
                                       // heartbeat that could not be written leaves
                                       // it so - and it cannot be disposed before
                                       // it has stopped. Cancelling stops it.
                                       ending.Cancel();

                                       try
                                       {
                                           await next;
                                       }
                                       catch
                                       { }
                                   }

                                   // Its session over, the reader is told the one way
                                   // a stream can tell anybody anything: it ends, and
                                   // the browser's retry is answered with a 401.
                                   if (signedOut)
                                       await Events.Unsubscribe(clientId);

                               }
                               catch (OperationCanceledException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (ObjectDisposedException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (Exception e)
                               {
                                   await Events.Unsubscribe(clientId);

                                   // Not through the event log: an event stream
                                   // that ends because the browser went away is
                                   // the normal end of one, and logging it here
                                   // would publish an event to the very streams
                                   // that are closing.
                                   System.Diagnostics.Debug.WriteLine($"The event stream of {clientId} ended: {e.Message}");
                               }

                           }

                       }.Set("X-Accel-Buffering", "no").
                         WithCommonSecurityHeaders().
                         AsImmutable
                   );

        }

        #endregion

        #region (private) StillLetIn(Request, Reader)

        /// <summary>
        /// Whether whoever opened an event stream would still be let in -
        /// asked before every event the stream is sent, and at every heartbeat.
        /// </summary>
        /// <remarks>
        /// A stream is one request that is answered for hours, and it used to
        /// be asked about its session once, when it opened. Measured on a
        /// local controller, whose stream is this one: signed out, the Logs page
        /// went on saying "live" and showing every line it wrote, for as long as
        /// it was watched.
        ///
        /// A stream opened with a session is asked whether that session is
        /// still there and its account still one that may sign in - what a new
        /// request with the same cookie is asked. The session is looked at and
        /// not taken through Sessions.TryGet, which counts as a use: with an
        /// idle timeout, a Logs page left open would keep its session alive for
        /// ever, one line of the log at a time. Among the few sessions a vehicle has,
        /// looking costs nothing.
        ///
        /// One opened with a password or an API key has no session that could
        /// end. Its account is asked about instead, and the password is not
        /// checked again: that would be 600 000 rounds of PBKDF2 and a turn of
        /// the sign-in's rate limit, for every line of the log.
        /// </remarks>
        /// <param name="Request">The request that opened the stream.</param>
        /// <param name="Reader">Who it was let in as.</param>
        private Func<Boolean> StillLetIn(HTTPRequest Request, IUser Reader)
        {

            if (Request.Cookies is not null                                                      &&
                Request.Cookies.TryGet(ExtAPI.SessionCookieName, out var cookie)                 &&
                cookie is not null                                                               &&
                SecurityToken_Id.TryParse(cookie.FirstOrDefault().Key, out var securityTokenId) &&
                LiveSession(securityTokenId) is not null)
            {
                return () => LiveSession(securityTokenId) is Session session  &&
                             ExtAPI.TryGetUser(session.UserId, out var user)   &&
                             HTTPExtAPI.CanAuthenticate(user);
            }

            var readerId = Reader.Id;

            return () => ExtAPI.TryGetUser(readerId, out var user) &&
                         HTTPExtAPI.CanAuthenticate(user);


            Session? LiveSession(SecurityToken_Id Token)
            {

                var now = ExtAPI.Sessions.TimeProvider.GetUtcNow();

                return ExtAPI.Sessions.FirstOrDefault(session => session.Token == Token &&
                                                                 !session.IsExpired(now));

            }

        }

        #endregion

        #region CloseEventStreams()

        /// <summary>
        /// End every open event stream, so that the HTTP server can stop.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="EV.Stop"/> before the servers are
        /// stopped, and not by the server itself: an event stream is a request
        /// that has been answered and is still being written to, and Hermod
        /// waits for every request it started before it reports itself stopped.
        /// Closing the socket underneath one does not wake it, because it is
        /// waiting for the next log entry and not for the network - so without
        /// this, a vehicle with one browser on its Logs page never finishes
        /// shutting down.
        ///
        /// The browsers see the connection end and reconnect by themselves;
        /// that is what the retry interval of the stream is for.
        /// </remarks>
        public void CloseEventStreams()
        {

            if (!shutdown.IsCancellationRequested)
                shutdown.Cancel();

        }

        #endregion

        #region (private) UnknownPath     (Request)

        private Task<HTTPResponse> UnknownPath(HTTPRequest Request)

            => Task.FromResult(
                   JSONResponse(
                       Request,
                       HTTPStatusCode.NotFound,
                       new JObject(
                           new JProperty("error",  "Unknown API path"),
                           new JProperty("path",   Request.Path.ToString())
                       )
                   )
               );

        #endregion


        #region (private) Publish(SubEvent, JSON)

        /// <summary>
        /// Hands an event to every browser. Fire-and-forget on purpose: this is
        /// called from inside whatever wrote the log entry, and none of those
        /// should wait for a slow browser.
        /// </summary>
        private void Publish(String   SubEvent,
                             JObject  JSON)
        {

            Events.SubmitEvent(SubEvent, JSON).
                   ContinueWith(task => System.Diagnostics.Debug.WriteLine($"Publishing a '{SubEvent}' event failed: {task.Exception?.GetBaseException().Message}"),
                                TaskContinuationOptions.OnlyOnFaulted);

        }

        #endregion

        #region (private) TryGetUser(Request, out Session, out Unauthorized)

        /// <summary>
        /// The live session behind the request, or the 401 response - which
        /// also expires a stale cookie, so that the browser stops sending it.
        /// </summary>
        private Boolean TryGetUser(HTTPRequest                             Request,
                                   [NotNullWhen(true)]  out IUser?          User,
                                   [NotNullWhen(false)] out HTTPResponse?   Unauthorized)
        {

            // Cookie, HTTP Basic auth or an API key - whichever of the three
            // the caller used. Which one it was does not change what they may
            // do: the groups do that, and they hang off the account rather than
            // off the door it came through.
            if (ExtAPI.TryGetHTTPUser(Request, out User) && User is not null)
            {
                Unauthorized = null;
                return true;
            }

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.Unauthorized,
                              ContentType     = HTTPContentType.Application.JSON_UTF8,
                              Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in required.")).ToString(Formatting.None)),
                              CacheControl    = "no-store"
                          };

            if (Request.Cookies is not null &&
                Request.Cookies.TryGet(ExtAPI.SessionCookieName, out _))
            {
                builder.SetCookie = ExpiredSessionCookie();
            }

            Unauthorized = builder.WithCommonSecurityHeaders().AsImmutable;
            return false;

        }

        #endregion

        #region (private) TryAuthorize(Request, Required, StateChanging, out Session, out Refused)

        /// <summary>
        /// The live session behind the request, when it is allowed to do this -
        /// or the response that says why not.
        /// </summary>
        /// <remarks>
        /// Three refusals, in the order they have to happen: a request from
        /// another site is turned away before it is read at all, a request
        /// without a session is a 401 that also expires a stale cookie, and a
        /// request from somebody signed in who may not do this is a 403 naming
        /// the permission they are short of and the roles that carry it. The
        /// difference between the last two matters to a browser: 401 means sign
        /// in again, 403 means signing in again will not help.
        /// </remarks>
        /// <param name="Request">The request.</param>
        /// <param name="Required">What this request needs permission to do.</param>
        /// <param name="StateChanging">Whether it changes something, and is therefore also checked for being cross-site.</param>
        /// <param name="Session">The session behind it.</param>
        /// <param name="Refused">The response to send instead.</param>
        private Boolean TryAuthorize(HTTPRequest                             Request,
                                     Permissions                             Required,
                                     Boolean                                 StateChanging,
                                     [NotNullWhen(true)]  out IUser?         User,
                                     [NotNullWhen(false)] out HTTPResponse?  Refused)
        {

            User = null;

            if (StateChanging && RefuseCrossSite(Request) is HTTPResponse crossSite)
            {
                Refused = crossSite;
                return false;
            }

            if (!TryGetUser(Request, out User, out Refused))
                return false;

            var permissions = PermissionsOf(User);

            if (!permissions.HasFlag(Required))
            {
                Refused  = RefusePermission(Request, User, Required, null);
                User     = null;
                return false;
            }

            Refused = null;
            return true;

        }

        #endregion

        #region (private) RefusePermission(Request, Session, Required, Because)

        /// <summary>
        /// The 403 for somebody signed in who may not do this, naming the roles
        /// that carry the permission they are short of.
        /// </summary>
        /// <remarks>
        /// Its own method because it is needed twice: once before a request is
        /// read, and once after - a change to the EVSEs cannot be judged until
        /// it has been compared with what the vehicle has, so that refusal
        /// happens with the body already parsed. Both say the same sentence,
        /// and both leave the same line in the log.
        /// </remarks>
        /// <param name="Because">What it was about this particular request, when the route alone does not say.</param>
        private HTTPResponse RefusePermission(HTTPRequest  Request,
                                              IUser        User,
                                              Permissions  Required,
                                              String?      Because)
        {

            // HasFlag with more than one flag asks for all of them, which is
            // what a role has to carry to do a change that was several kinds at
            // once. Nobody is named who could only do half of it.
            var allowed = UserRole.All.Where(role => role.Permissions.HasFlag(Required)).
                                       Select(role => role.Name);

            Log.Warning(
                $"'{User.Id}' was refused {Required} on {Request.HTTPMethod} {Request.Path}; " +
                $"signed in as {String.Join(", ", RolesOf(User).Select(role => role.Name))}." +
                (Because is null ? "" : $" {Because}"),
                "web", "auth"
            );

            return ErrorJSON(
                       Request,
                       HTTPStatusCode.Forbidden,
                       (Because is null ? "" : Because + " ") +
                       $"This needs the {String.Join(" or ", allowed)} role."
                   );

        }

        #endregion

        #region (private static) RefuseCrossSite(Request)

        /// <summary>
        /// The 403 for a request that another site made the browser send, or
        /// null when the request is our own page's.
        /// </summary>
        /// <remarks>
        /// The cookie is SameSite=strict, so a cross-site request would arrive
        /// without a session anyway. This is the second lock on the same door:
        /// browsers say where a request came from (Sec-Fetch-Site, Origin), and
        /// a state-changing request from anywhere but this origin is refused
        /// before it is even read.
        /// </remarks>
        private static HTTPResponse? RefuseCrossSite(HTTPRequest Request)
        {

            var site = Request.GetHeaderField("Sec-Fetch-Site");

            if (site is not null && site is not ("same-origin" or "none"))
                return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");

            var origin = Request.GetHeaderField("Origin");

            if (origin is not null && origin != "null")
            {

                var host = Request.GetHeaderField("Host") ?? "";

                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    !uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");
                }

            }

            return null;

        }

        #endregion

        #region (private static) TryParseJSONObject(Request, out JSON, out ErrorResponse)

        /// <summary>
        /// The request body as a JSON object, or the 400 response describing
        /// what is wrong with it.
        /// </summary>
        private static Boolean TryParseJSONObject(HTTPRequest                             Request,
                                                  [NotNullWhen(true)]  out JObject?       JSON,
                                                  [NotNullWhen(false)] out HTTPResponse?  ErrorResponse)
        {

            JSON           = null;
            ErrorResponse  = null;

            var text = Request.HTTPBodyAsUTF8String;

            if (String.IsNullOrWhiteSpace(text))
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "The request body must be a JSON object!");
                return false;
            }

            try
            {
                JSON = JObject.Parse(text);
                return true;
            }
            catch (JsonException e)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, $"Invalid JSON: {e.Message}");
                return false;
            }

        }

        #endregion

        #region (private) MeJSON(Session)

        /// <summary>
        /// Who is signed in, and what they may do.
        /// </summary>
        /// <remarks>
        /// The permissions travel to the browser so that a page can grey out
        /// what this person may not do, rather than offering it and letting
        /// them find out by being refused. They are a copy of what the vehicle
        /// enforces and not the enforcement: every request is checked again on
        /// arrival, so a browser that edits this list gains nothing but a
        /// button that answers 403.
        /// </remarks>
        private JObject MeJSON(IUser User)

            => new (
                   new JProperty("username",     User.Id.ToString()),
                   new JProperty("roles",        new JArray(RolesOf(User).Select(role => role.Name))),
                   new JProperty("permissions",  new JArray(PermissionsOf(User).Names()))
               );

        #endregion

        #region (private) ExpiredSessionCookie()

        /// <summary>
        /// The Set-Cookie of a sign-out: the HTTPExt API's session cookie,
        /// expired in 1970, so that the browser drops it.
        /// </summary>
        /// <remarks>
        /// Written here rather than asked of the HTTPExt API, which sets its
        /// cookies inside its own handlers and has nothing to hand one out.
        /// One HTTPCookie parsed as one: HTTPCookies.Parse(String) is made for
        /// the Cookie header of a request, where a semicolon separates cookies,
        /// and would turn "Path=/" and "HttpOnly" into cookies of their own.
        /// </remarks>
        private HTTPCookies ExpiredSessionCookie()

            => new (HTTPCookie.Parse(
                        String.Concat(ExtAPI.SessionCookieName, "=",
                                      "; Expires=", DateTimeOffset.UnixEpoch.ToRFC1123(),
                                      "; Path=/",
                                      "; SameSite=strict",
                                      "; HttpOnly")
                    ));

        #endregion

        #region (private) RolesOf(User) / PermissionsOf(User)

        /// <summary>
        /// The roles this account holds: one per group of that name it is in.
        /// </summary>
        /// <remarks>
        /// Asked of the groups on every request rather than remembered at
        /// sign-in, so that taking somebody out of a group takes effect on
        /// their next request instead of at their next sign-in. A role revoked
        /// that still works until a browser is closed is not revoked.
        /// </remarks>
        private IEnumerable<UserRole> RolesOf(IUser User)

              // IsMember compares the account by identification, which is what
              // makes this safe to ask with whatever instance authenticated the
              // request: a cookie brings one rebuilt from what the cookie holds
              // rather than the one the membership was made with. It compared by
              // reference until 2026-09-18, and the same account then came out as
              // systemadmin through Basic auth and as nobody through a cookie.
            => UserRole.All.Where(role => ExtAPI.IsMember(User, role.GroupId));

        /// <summary>
        /// Everything those roles add up to, or nothing at all when the account
        /// is in none of the groups.
        /// </summary>
        private Permissions PermissionsOf(IUser User)

            => RolesOf(User).PermissionsOf();

        #endregion

        #region (private static) ErrorJSON(...) / JSONResponse(...)

        private static HTTPResponse ErrorJSON(HTTPRequest     Request,
                                              HTTPStatusCode  StatusCode,
                                              String          Message)

            => JSONResponse(
                   Request,
                   StatusCode,
                   new JObject(new JProperty("error", Message))
               );


        private static HTTPResponse JSONResponse(HTTPRequest     Request,
                                                 HTTPStatusCode  StatusCode,
                                                 JToken          JSON)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = StatusCode,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(JSON.ToString(Formatting.None)),
                   CacheControl    = "no-store"
               }.WithCommonSecurityHeaders().AsImmutable;

        #endregion

    }

}
