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
    /// The JSON API the browser talks to, registered at "/api": what every
    /// node has - see <see cref="NodeHTTPAPI"/> - and what only a vehicle
    /// has, its own settings, V2G and its charging session.
    /// </summary>
    /// <remarks>
    /// The sign-in, the configuration, name resolution and the time servers,
    /// the certificate store, the log and the event stream are the node's,
    /// the same on every kind of node; this class used to have its own copy
    /// of all of them.
    /// </remarks>
    public class EVHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The vehicle this API speaks for.
        /// </summary>
        public EV  Vehicle  { get; }

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
                   Vehicle,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(EVHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.Vehicle = Vehicle;

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        /// <summary>
        /// What only a vehicle has.
        /// </summary>
        private void RegisterURLTemplates()
        {

            AddHandler(HTTPPath.Root + "v1/configuration/vehicle",    GetVehicleConfiguration,  HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/vehicle",    PutVehicleConfiguration,  HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        GetV2GConfiguration,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        PutV2GConfiguration,      HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g/discover", PostV2GDiscover,        HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g/pair",     PostSLACPair,           HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/session",    GetSessionConfiguration,  HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/session",    PutSessionConfiguration,  HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/session",                  GetSessionConfiguration,  HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/session",                  PostSessionStart,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/session/stop",             PostSessionStop,          HTTPMethod.POST);

        }

        #endregion


        #region (private) GetVehicleConfiguration(Request) / PutVehicleConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/vehicle: what this vehicle is, and what
        /// its battery wants.
        /// </summary>
        private Task<HTTPResponse> GetVehicleConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(VehicleAccess.Vehicle), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(VehicleAccess.Vehicle), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Vehicle.TryUpdateVehicleConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

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

            if (!TryAuthorize(Request, Permission.Read(VehicleAccess.V2G), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(VehicleAccess.V2G), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Vehicle.TryUpdateV2GConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

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

            if (!TryAuthorize(Request, Permission.Run (VehicleAccess.V2G), true, out var user, out var refused))
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

            if (!TryAuthorize(Request, Permission.Read(VehicleAccess.Session), false, out _, out var refused))
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

            if (!Vehicle.TryUpdateSessionConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Vehicle.SessionConfigurationJSON())
                   );

        }

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
        private static IReadOnlyList<Permission> PermissionsForSession(JObject JSON)
        {

            var required = new List<Permission>();

            // Which certificate the vehicle is known by is a decision about
            // who it is, and asked as one: the certificates' own.
            if (new[] { "pkiDirectory", "vehicleCertificate", "contractCertificate",
                        "oemCertificate", "tariffCertificate" }.Any(JSON.ContainsKey))
                required.Add(Permission.Edit(NodeResources.Certificates));

            // Which interface and which protocol is a statement about the
            // link, and asked as one.
            if (new[] { "connect", "protocol", "mode", "tls", "slacPeer",
                        "t1sBus", "t1sTransport", "t1sInterface", "t1sWeight", "renegotiate" }.Any(JSON.ContainsKey))
                required.Add(Permission.Edit(VehicleAccess.V2G));

            if (new[] { "targetEnergyKWh", "maxChargingTimeSeconds",
                        "departureInSeconds", "minimumStateOfChargePercent" }.Any(JSON.ContainsKey))
                required.Add(Permission.Edit(VehicleAccess.Session));

            // An empty request changes nothing and is answered as a read, which
            // is what it is.
            if (required.Count == 0)
                required.Add(Permission.Read(VehicleAccess.Session));

            return required;

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

            if (!TryAuthorize(Request, Permission.Run (VehicleAccess.Session), true, out var user, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (VehicleAccess.Session), true, out var user, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (VehicleAccess.V2G), true, out var user, out var refused))
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

    }

}
