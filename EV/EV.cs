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

using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.ISO15118.Discovery;

using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.ISO15118;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.ISO15118.Security;

#endregion

namespace cloud.charging.open.EV
{

    /// <summary>
    /// One electric vehicle: the battery it says it has, the wire below the
    /// charging cable it finds a station on, the HTTP server in front of both,
    /// the JSON API at "/api" and the web interface at "/".
    /// </summary>
    /// <remarks>
    /// The web interface is a bundle of HTML, CSS and JavaScript built by
    /// webpack from Frontend/ and embedded into this assembly, so that the
    /// vehicle is one file to deploy and needs nothing installed beside it. The
    /// browser and the vehicle talk over the JSON API and one Server-Sent
    /// Events stream; nothing is rendered on the server.
    ///
    /// A vehicle is not a station turned around. A station runs a loop: it
    /// listens, and answers whoever plugs in. A vehicle's flow has a beginning
    /// and an end - arrive, discover, charge, leave - and nothing on the wire
    /// happens until somebody says so. That is why this class has a web
    /// interface that runs for as long as the process does, and an ISO 15118
    /// side that is a set of things it can be asked to do.
    /// </remarks>
    public partial class EV : WWCPNode
    {

        #region Data

        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target of EV.csproj).
        /// </summary>
        public const            String  HTTPRoot          = "cloud.charging.open.EV.HTTPRoot.";

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// One below the charging station's 2348, so that a vehicle and a
        /// station started on the same bench do not fight over a port - which
        /// is the normal way of running both of these. The node below has a
        /// port of its own for a node of no particular kind, and this one is
        /// handed to it rather than left to it.
        /// </remarks>
        public static new readonly  IPPort  DefaultHTTPPort   = IPPort.Parse(2347);

        /// <summary>
        /// Lets one SDP discovery run at a time.
        /// </summary>
        /// <remarks>
        /// Not for the sake of the configuration but for the sake of the
        /// socket: two discoveries at once would multicast two questions onto
        /// one link and sort the answers between them by arrival order, which
        /// is not sorting them at all. A second request is refused while one is
        /// running rather than queued, because by the time a queued one ran the
        /// person who asked would have the first one's answer in front of them.
        /// </remarks>
        private readonly  SemaphoreSlim                   discoveryLock   = new (1, 1);

        /// <summary>
        /// How the last SDP discovery went, as the web interface reads it, or
        /// null while none has been asked for.
        /// </summary>
        private           JObject?                        lastDiscovery;

        /// <summary>
        /// Where the last discovery found a station, or null when it found
        /// none.
        /// </summary>
        /// <remarks>
        /// The address here carries the scope id of the interface it was heard
        /// on, which the JSON beside it does not have to and a socket cannot do
        /// without.
        /// </remarks>
        private           SeccEndpoint?                   lastDiscoveryEndpoint;

        #endregion

        #region Properties

        /// <summary>
        /// The JSON API the browser talks to.
        /// </summary>
        public EVHTTPAPI              API                          { get; }

        /// <summary>
        /// What to call this vehicle.
        /// </summary>
        public String                 VehicleName                  { get; private set; } = VehicleConfiguration.DefaultName;

        /// <summary>
        /// Its vehicle identification number, where somebody gave it one.
        /// </summary>
        public String?                VIN                          { get; private set; }

        /// <summary>
        /// The usable capacity of the pack, in kWh.
        /// </summary>
        public Double                 BatteryCapacity_kWh          { get; private set; } = VehicleConfiguration.DefaultCapacity_kWh;

        /// <summary>
        /// How full it is at plug-in, in percent.
        /// </summary>
        public Double                 StateOfCharge_percent        { get; private set; } = VehicleConfiguration.DefaultSoC_percent;

        /// <summary>
        /// How full it wants to be when it leaves, in percent.
        /// </summary>
        public Double                 TargetStateOfCharge_percent  { get; private set; } = 100;

        /// <summary>
        /// What this vehicle asks a station for, in kW.
        /// </summary>
        public Double                 MaxChargingPower_kW          { get; private set; } = 11;

        /// <summary>
        /// Where it starts asking for less, in percent.
        /// </summary>
        public Double                 TaperFrom_percent            { get; private set; } = VehicleConfiguration.DefaultTaperFrom_percent;

        /// <summary>
        /// What the next SDP discovery runs under.
        /// </summary>
        public V2GConfiguration       V2GSettings                  { get; private set; } = new ();

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One electric vehicle, with its web interface.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; DefaultHTTPPort by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this vehicle sits below; the root by default. Something else only where several of these programs share one HTTP server.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this vehicle's own. Handing one in is what makes one sign-in open several of these programs at once.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts: one file, whose sections the node below and the vehicle each read for themselves.</param>
        /// <param name="DNSClient">How to resolve names, or null to make a client.</param>
        /// <param name="NTSClient">Where to read the time, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="CertificatesPath">The directory the certificate store lives in between starts.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="TimeProvider">The clock, or null for the system one.</param>
        public EV(IIPAddress?            HTTPHostname       = null,
                  IPPort?                HTTPPort           = null,
                  HTTPServer?            HTTPServer         = null,
                  HTTPPath?              BasePath           = null,
                  HTTPPath?              HTTPRootPath       = null,
                  HTTPExtAPI?            ExtAPI             = null,
                  String?                AccountsPath       = null,
                  WWCPConfigFile?        ConfigFile         = null,
                  DNSClient?             DNSClient          = null,
                  NTSClient?             NTSClient          = null,
                  IStaticContentSource?  Frontend           = null,
                  String?                CertificatesPath   = null,
                  EventLog?              Log                = null,
                  Boolean                LogToConsole       = true,
                  LogLevel               ConsoleLogLevel    = LogLevel.Info,
                  String?                LogPath            = null,
                  Boolean                BridgeDebugLog     = true,
                  TimeProvider?          TimeProvider       = null)

            : base(Kind:               new NodeKind(
                                           Name:           "electric vehicle",
                                           Tag:            "vehicle",
                                           Product:        "EV",
                                           Organization:   "Vehicle",
                                           LogFilePrefix:  "ev"
                                       ),
                   Version:            typeof(EV).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:           HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:       HTTPHostname,
                   HTTPServer:         HTTPServer,
                   BasePath:           BasePath,
                   HTTPRootPath:       HTTPRootPath,
                   ExtAPI:             ExtAPI,
                   AccountsPath:       AccountsPath,
                   ConfigFile:         ConfigFile,
                   DNSClient:          DNSClient,
                   NTSClient:          NTSClient,
                   Frontend:           Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(EV).Assembly),
                   CertificatesPath:   CertificatesPath,
                   Log:                Log,
                   LogToConsole:       LogToConsole,
                   ConsoleLogLevel:    ConsoleLogLevel,
                   LogPath:            LogPath,
                   BridgeDebugLog:     BridgeDebugLog,
                   TimeProvider:       TimeProvider)

        {

            #region What the configuration file says about a vehicle

            // Its own sections of the document the node below has already
            // read: the ones that reading passed over are the ones this is
            // for.
            if (!EVConfiguration.TryParse(ConfigurationDocument, out var configuration, out var problem))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {problem} Repair or remove '{this.ConfigFile.Path}' and start again.");

            if (!configuration.IsEmpty)
                this.Log.Info($"Vehicle configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            #endregion

            #region What this vehicle is, and what it does with a station

            // After the certificate store, which the node below made before
            // handing over, because the session settings name certificates
            // in it.
            if (configuration.Vehicle is not null)
                ApplyVehicleConfiguration(configuration.Vehicle);

            if (configuration.V2G is not null)
                ApplyV2GConfiguration(configuration.V2G);

            if (configuration.Session is not null)
                ApplySessionConfiguration(configuration.Session);

            this.Log.Info($"This vehicle is {VehicleName}, {StateOfCharge_percent:F0} % of {BatteryCapacity_kWh:F0} kWh.", "vehicle", "config");

            #endregion

            #region The JSON API

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this vehicle are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This vehicle signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // The JSON API at "/api", beside the web interface the node below
            // has already put at "/". The more specific of the two, so that an
            // unknown /api path never reaches the single-page-application
            // stub.
            this.API           = new EVHTTPAPI(
                                     HTTPServer:  this.HTTPServer,
                                     Vehicle:     this,
                                     ExtAPI:      this.ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     this.HTTPRootPath,
                                     Version:     Version
                                 );

            #endregion

        }

        #endregion


        #region (protected override) OnStarted()

        /// <summary>
        /// What a vehicle says once it is up: where its API is, and that
        /// nothing goes out on the link by itself.
        /// </summary>
        protected override Task OnStarted()
        {

            Log.Info($"The JSON API is at {APIURL}v1/status", "web", "http");

            // Said at every start, because it is the one thing about this
            // vehicle that surprises people: nothing goes out on the link until
            // somebody asks for it.
            var candidates = V2GLink.Candidates();

            Log.Info(
                candidates.Count > 0
                    ? $"ISO 15118: {candidates.Count} interface(s) could carry V2G traffic ({String.Join(", ", candidates.Select(candidate => candidate.Name))}). " +
                       "Nothing is sent until a discovery is asked for."
                    : "ISO 15118: no interface of this machine could carry V2G traffic - one needs to be up, have a MAC address and an IPv6 link-local address.",
                "15118"
            );

            return Task.CompletedTask;

        }

        #endregion

        #region (protected override) OnStopping()

        /// <summary>
        /// End the event streams before the server stops.
        /// </summary>
        /// <remarks>
        /// Every browser with the Logs page open holds a request that is
        /// waiting for the next log entry rather than for its socket, and the
        /// HTTP server waits for every request it started. Closing the sockets
        /// does not wake those, so they are ended here first - whoever owns the
        /// server, because the streams are this vehicle's.
        /// </remarks>
        protected override Task OnStopping()
        {

            API.CloseEventStreams();

            return Task.CompletedTask;

        }

        #endregion


        #region DiscoverAsync(InterfaceName = null, CancellationToken = default)

        /// <summary>
        /// Ask the link below the charging cable whether there is a station on
        /// it, and say what came back.
        /// </summary>
        /// <remarks>
        /// One at a time - see <see cref="discoveryLock"/>. A second request
        /// while one is running is answered with what is happening rather than
        /// queued behind it, because the person asking has the first one's
        /// answer arriving in front of them either way.
        /// </remarks>
        /// <param name="InterfaceName">The interface to broadcast on; without one, the configured interface, and without that the first candidate.</param>
        /// <param name="CancellationToken">Abort the discovery.</param>
        public async Task<JObject> DiscoverAsync(String?            InterfaceName       = null,
                                                 CancellationToken  CancellationToken   = default)
        {

            var wanted = InterfaceName ?? V2GSettings.InterfaceName;

            if (!await discoveryLock.WaitAsync(0, CancellationToken))
                return new JObject(
                           new JProperty("outcome",  "busy"),
                           new JProperty("error",    "A discovery is already running on this vehicle. Its result appears in the log and on this page.")
                       );

            try
            {

                var link = V2GLink.FindInterface(wanted);

                if (link is null)
                {

                    var candidates = V2GLink.Candidates();

                    var error      = wanted is not null
                                         ? $"'{wanted}' is not an interface of this machine that could carry V2G traffic."
                                         : "No interface of this machine could carry V2G traffic - one needs to be up, have a MAC address and an IPv6 link-local address.";

                    if (candidates.Count > 0)
                        error += $" Candidates: {String.Join(", ", candidates.Select(candidate => candidate.Name))}.";

                    Log.Warning($"SDP: no discovery was run. {error}", "15118", "sdp");

                    lastDiscovery          = new JObject(
                                                 new JProperty("outcome",    "noInterface"),
                                                 new JProperty("startedAt",  TimeProvider.GetUtcNow().ToString("o")),
                                                 new JProperty("error",      error)
                                             );

                    lastDiscoveryEndpoint  = null;

                    return lastDiscovery;

                }

                var found = await V2GLink.DiscoverAsync(
                                      link,
                                      V2GSettings,
                                      Log,
                                      CancellationToken
                                  );

                found.JSON["interface"] = link.Name;

                lastDiscovery           = found.JSON;

                // Kept beside the JSON rather than dug back out of it: this is
                // where a link-local address still carries the scope id of the
                // interface it was heard on, and a session started right after
                // a discovery is the one caller that needs it.
                lastDiscoveryEndpoint   = found.Found;

                return lastDiscovery;

            }
            finally
            {
                discoveryLock.Release();
            }

        }

        #endregion


        #region ConfigurationJSON()

        /// <summary>
        /// What this vehicle is, as the Configuration page of the web
        /// interface reads it: what the node below says of itself, and on top
        /// the vehicle, its battery, its link and the assemblies it was built
        /// from.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            // First, because it is the card the page leads with.
            json.AddFirst(new JProperty("vehicle",    new JObject(
                              new JProperty("name",           VehicleName),
                              new JProperty("vin",            VIN),
                              new JProperty("version",        Version),
                              new JProperty("createdAt",      CreatedAt.ToString("o")),
                              new JProperty("machine",        Environment.MachineName),
                              new JProperty("runtime",        Environment.Version.ToString()),
                              new JProperty("os",             Environment.OSVersion.ToString())
                          )));

            json.Property("vehicle")!.AddAfterSelf(new JProperty("battery",    new JObject(
                              new JProperty("capacityKWh",                BatteryCapacity_kWh),
                              new JProperty("stateOfChargePercent",       StateOfCharge_percent),
                              new JProperty("targetStateOfChargePercent", TargetStateOfCharge_percent),
                              new JProperty("maxChargingPowerKW",         MaxChargingPower_kW),
                              new JProperty("taperFromPercent",           TaperFrom_percent)
                          )));

            json.Add(new JProperty("v2g",        new JObject(
                         new JProperty("interface",      V2GSettings.InterfaceName),
                         new JProperty("candidates",     new JArray(V2GLink.Candidates().Select(candidate => candidate.Name))),
                         new JProperty("lastDiscovery",  lastDiscovery)
                     )));

            json.Add(new JProperty("assemblies", new JArray(
                         BuiltFrom.Assemblies.Select(AssemblyJSON)
                     )));

            return json;

        }

        #endregion

        #region (private static) AssemblyJSON(Assembly)

        /// <summary>
        /// One library of this vehicle, as the Configuration page reads it.
        /// </summary>
        /// <remarks>
        /// Nothing is named here any more. What used to be four hand-written
        /// lines is whatever BuiltFrom finds loaded, so a library that joins
        /// this vehicle appears by itself and one that leaves stops being
        /// claimed - which a hand-written list never manages for long.
        ///
        /// The label is the repository where there is one, because that is what
        /// somebody looking at a bug report can check out; the assembly's own
        /// name stays beside it for the libraries that carry no stamp yet.
        /// </remarks>
        private static JObject AssemblyJSON(LoadedAssembly Assembly)
        {

            var json = new JObject(
                           new JProperty("name",      Assembly.Repository ?? Assembly.Name),
                           new JProperty("assembly",  Assembly.Name),
                           new JProperty("version",   Assembly.Version)
                       );

            // Only when it is known: an empty commit in a bug report reads like
            // an answer, and it is not one.
            if (Assembly.Commit is not null)
                json.Add(new JProperty("commit", Assembly.Commit));

            return json;

        }

        #endregion

        #region ValidatorFor(Store, Kind, Log)

        /// <summary>
        /// A chain validator over the usable roots of one kind, or nothing
        /// where there are none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing rather than a validator that trusts nobody, and the
        /// difference is the same one <c>TrustRoots.Load</c> makes for the same
        /// reason: "no roots configured" and "roots that vouch for nobody" fail
        /// identically at a handshake and mean entirely different things about
        /// the run. A caller that gets nothing back can say so; one handed an
        /// empty validator can only report a rejection.
        /// </para>
        /// <para>
        /// Expired roots are left out here rather than refused at import. A
        /// root that expires overnight should stop being a trust anchor
        /// overnight, without anybody having to notice.
        /// </para>
        /// </remarks>
        public static V2GChainValidator? ValidatorFor(CertificateStore  Store,
                                                      CertificateKind   Kind,
                                                      EventLog          Log)
        {

            if (!Kind.IsTrustAnchor())
                throw new ArgumentException($"{Kind.AsText()} is not a trust anchor.", nameof(Kind));

            var roots = new X509Certificate2Collection();

            foreach (var entry in Store.UsableByKind(Kind))
            {

                var fullPath = Store.FullPath(entry);

                try
                {
                    roots.Add(X509CertificateLoader.LoadCertificateFromFile(fullPath));
                }
                catch (Exception exception)
                {
                    Log.Warning($"Certificates: the {Kind.AsText()} '{entry.Label}' could not be read and is not " +
                                $"vouching for anything - {exception.Message}",
                                "certificates");
                }

            }

            return roots.Count > 0
                       ? new V2GChainValidator(roots)
                       : null;

        }

        #endregion


        #region DisposeAsync()

        /// <summary>
        /// Stop listening, let go of what is this vehicle's own, and then of
        /// the rest.
        /// </summary>
        public override async ValueTask DisposeAsync()
        {

            // Stopped here as well as below: the locks may not go before the
            // session and the discovery that might be holding them have.
            await Stop();

            sessionLock.  Dispose();
            discoveryLock.Dispose();

            await base.DisposeAsync();

        }

        #endregion

    }

}
