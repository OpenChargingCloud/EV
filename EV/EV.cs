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

using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using NullMailer = org.GraphDefined.Vanaheimr.Hermod.SMTP.NullMailer;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.ISO15118.Discovery;

using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.EV.Logging;
using cloud.charging.open.EV.Web;

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
    public partial class EV : IAsyncDisposable
    {

        #region Data

        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target of EV.csproj).
        /// </summary>
        public const String  HTTPRoot            = "cloud.charging.open.EV.HTTPRoot.";

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// One below the charging station's 2348, so that a vehicle and a
        /// station started on the same bench do not fight over a port - which
        /// is the normal way of running both of these.
        /// </remarks>
        public static readonly IPPort DefaultHTTPPort = IPPort.Parse(2347);

        /// <summary>
        /// Where the accounts live, unless another directory is given.
        /// </summary>
        public const String  DefaultAccountsPath          = "EV-accounts";

        /// <summary>
        /// The accounts themselves, inside that directory.
        /// </summary>
        public const String  DefaultAccountsDatabaseFile  = "users.db";

        /// <summary>
        /// Where the HTTPExt API answers: accounts, groups and API keys.
        /// </summary>
        /// <remarks>
        /// Beside "/api" rather than under it, because it is not this
        /// vehicle's API: it is Hermod's, with its own routes and its own
        /// vocabulary, and putting it under /api/v1 would promise that this
        /// vehicle versions it.
        /// </remarks>
        public static readonly HTTPPath  ExtAPIPath        = HTTPPath.Parse("/ext");

        /// <summary>
        /// The account made at a first start.
        /// </summary>
        public const String  DefaultAdminUser             = "root";

        /// <summary>
        /// The organization that account belongs to.
        /// </summary>
        /// <remarks>
        /// A vehicle has no organizations to speak of, and this one exists
        /// because the HTTPExt API's sign-in refuses an account that is in
        /// none - "You do not have access to any organization!" - however
        /// right its password is. So there is exactly one, named after the
        /// thing it stands for.
        /// </remarks>
        public const String  DefaultOrganization          = "Vehicle";

        /// <summary>
        /// The file of the bundle that is the web interface; its presence is
        /// what says there is one to serve at all.
        /// </summary>
        public const String  IndexFile           = "index.html";

        /// <summary>
        /// The icon of the bundle, which /favicon.ico is pointed at.
        /// </summary>
        public const String  FaviconSVG          = "favicon.svg";

        private readonly  DNSClient                       dnsClient;
        private           NTSClient                       ntsClient;

        /// <summary>
        /// The name servers this vehicle would ask, whether or not name
        /// resolution is switched on at the moment.
        /// </summary>
        /// <remarks>
        /// Kept beside the DNS client because switching name resolution off is
        /// done by taking its servers away - which is what being switched off
        /// actually means, for everything holding that client and not only for
        /// the parts of this vehicle that remember to ask first. Switching it
        /// back on needs the list back, and this is where it waited.
        /// </remarks>
        private           IReadOnlyList<DNSServerConfig>  configuredDNSServers;

        /// <summary>
        /// Serialises changes to what this vehicle is, so that two browsers
        /// saving at the same moment do not build half a vehicle each.
        /// </summary>
        private readonly  SemaphoreSlim                   reconfigureLock = new (1, 1);

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

        private readonly  HTTPServer                      httpServer;
        private readonly  HTTPPath                        httpRootPath;

        /// <summary>
        /// When this vehicle last managed to check its clock, what it found,
        /// and against whom.
        /// </summary>
        /// <remarks>
        /// Three fields rather than one object because they are written from
        /// one place and read from another, and the alternative - digging them
        /// back out of the JSON of the last check - would make the page depend
        /// on the shape of a diagnostic.
        /// </remarks>
        private           DateTimeOffset?                 lastTimeCheck;
        private           TimeSpan?                       lastTimeCheckOffset;
        private           String?                         lastTimeCheckServer;

        /// <summary>
        /// The clock that makes this vehicle check its own, when NTS is on.
        /// </summary>
        private           ITimer?                         timeCheckTimer;

        /// <summary>
        /// What the file said about the time client, kept because the parts of
        /// it that are not the client itself - how often to check, and what the
        /// operator claims about the server - are read long afterwards.
        /// </summary>
        private           NTSConfiguration?               ntsSettings;

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

        private readonly  ConsoleLog?                     consoleLog;
        private readonly  TraceBridge?                    traceBridge;

        private           Boolean                         started;

        #endregion

        #region Properties

        /// <summary>
        /// Everything that happens inside this vehicle.
        /// </summary>
        public EventLog               Log                          { get; }

        /// <summary>
        /// Who may open the web interface: the accounts, the groups they are
        /// in, and the sessions and API keys they hold.
        /// </summary>
        public HTTPExtAPI             ExtAPI                       { get; }

        /// <summary>
        /// The directory the accounts live in between starts.
        /// </summary>
        public String                 AccountsPath                 { get; }

        /// <summary>
        /// Where everything this vehicle can be told in writing lives between
        /// starts: its name resolution, its time source, its battery, its link.
        /// </summary>
        public EVConfigFile           ConfigFile                   { get; }

        /// <summary>
        /// The password made up at a first start and shown once, or null when
        /// accounts were already there.
        /// </summary>
        /// <remarks>
        /// Set by <see cref="Start"/> rather than by the constructor, because
        /// creating the account is asynchronous and a constructor that waited
        /// on it would be a constructor that can deadlock.
        /// </remarks>
        public String?                GeneratedPassword            { get; private set; }

        /// <summary>
        /// The clock this vehicle reads. Its own, not the one NTS reports.
        /// </summary>
        public TimeProvider           TimeProvider                 { get; }

        /// <summary>
        /// When this vehicle was made.
        /// </summary>
        public DateTimeOffset         CreatedAt                    { get; }

        /// <summary>
        /// The version of this assembly.
        /// </summary>
        public String                 Version                      { get; }

        /// <summary>
        /// How this vehicle resolves names.
        /// </summary>
        public DNSClient              DNSClient                    => dnsClient;

        /// <summary>
        /// Where this vehicle reads the time.
        /// </summary>
        public NTSClient              NTSClient                    => ntsClient;

        /// <summary>
        /// Whether this vehicle resolves names at all.
        /// </summary>
        public Boolean                DNSEnabled                   { get; private set; } = true;

        /// <summary>
        /// Whether this vehicle asks a time server at all.
        /// </summary>
        public Boolean                NTSEnabled                   { get; private set; } = true;

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

        /// <summary>
        /// Where the web interface comes from: the bundle embedded in this
        /// assembly, or a directory somebody pointed this vehicle at.
        /// </summary>
        public IStaticContentSource   Frontend                     { get; }

        /// <summary>
        /// The JSON API the browser talks to.
        /// </summary>
        public EVHTTPAPI              API                          { get; }

        /// <summary>
        /// The web interface at "/", or null when there is no bundle to serve.
        /// </summary>
        public HTTPAPI?               WebInterface                 { get; }

        /// <summary>
        /// The TCP port the web interface listens on.
        /// </summary>
        public IPPort                 HTTPPort                     { get; }

        /// <summary>
        /// Where to point a browser.
        /// </summary>
        public URL                    WebInterfaceURL              { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One electric vehicle, with its web interface.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; DefaultHTTPPort by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" by default.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts.</param>
        /// <param name="DNSClient">How to resolve names, or null to make a client.</param>
        /// <param name="NTSClient">Where to read the time, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="Passwords">What opens the PKCS#12 files the session settings name; the environment fills in whatever is not given here.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="TimeProvider">The clock, or null for the system one.</param>
        public EV(IIPAddress?            HTTPHostname      = null,
                  IPPort?                HTTPPort          = null,
                  HTTPServer?            HTTPServer        = null,
                  HTTPPath?              HTTPRootPath      = null,
                  String?                AccountsPath      = null,
                  EVConfigFile?          ConfigFile        = null,
                  DNSClient?             DNSClient         = null,
                  NTSClient?             NTSClient         = null,
                  IStaticContentSource?  Frontend          = null,
                  CertificatePasswords?  Passwords         = null,
                  EventLog?              Log               = null,
                  Boolean                LogToConsole      = true,
                  LogLevel               ConsoleLogLevel   = LogLevel.Info,
                  Boolean                BridgeDebugLog    = true,
                  TimeProvider?          TimeProvider      = null)
        {

            #region The clock, before anything that wants to know the time

            // First of all, and not for tidiness: the event log below stamps
            // every entry with this, so a clock set afterwards would leave the
            // log reading the system one - and a log on a different clock than
            // the vehicle it belongs to cannot be held against anything.
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;
            this.CreatedAt     = this.TimeProvider.GetUtcNow();

            #endregion

            #region The log, next - everything below it may want to say something

            this.Version      = typeof(EV).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            this.Log          = Log ?? new EventLog(TimeProvider: this.TimeProvider);

            this.consoleLog   = LogToConsole
                                    ? new ConsoleLog(this.Log, ConsoleLogLevel)
                                    : null;

            // Attached before anything else is built, so that what the DNS
            // client and the HTTP server say while they are being made is
            // already in the log a browser will see later.
            this.traceBridge  = BridgeDebugLog
                                    ? TraceBridge.Attach(this.Log)
                                    : null;

            this.Log.Notice($"Electric vehicle v{this.Version} starting up.", "vehicle");

            #endregion

            #region Where the accounts live

            // Ending in a separator, because the HTTPExt API builds the paths
            // of its files by putting strings together rather than with
            // Path.Combine: a directory that does not end in one would give it
            // "...accountsUsersAPI" and not "...accounts/UsersAPI".
            this.AccountsPath = AccountsPath ?? DefaultAccountsPath;

            if (!this.AccountsPath.EndsWith(Path.DirectorySeparatorChar))
                this.AccountsPath += Path.DirectorySeparatorChar;

            #endregion

            #region What the configuration file says

            this.ConfigFile = ConfigFile ?? new EVConfigFile(EVConfigFile.DefaultFileName);

            EVConfiguration? configuration = null;

            if (this.ConfigFile.Exists)
            {

                // A file that is there but cannot be read is not something to
                // paper over with defaults: somebody wrote down what their
                // vehicle is and got it wrong, and quietly running as something
                // else instead would be worse than stopping.
                if (!this.ConfigFile.TryLoad(out configuration, out var configError))
                    throw new InvalidOperationException($"{configError} Repair or remove '{this.ConfigFile.Path}' and start again.");

                this.Log.Info($"Configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            }

            #endregion

            #region The clients everything below shares

            this.dnsClient             = DNSClient ?? new DNSClient();
            this.configuredDNSServers  = [.. dnsClient.DNSServers];

            // The clock goes to the time client too: a vehicle that reads one
            // clock itself and disciplines another would have two, which is one
            // more than a vehicle may have.
            this.ntsClient     = NTSClient ?? new NTSClient(
                                                  DomainName.Parse(NTSConfiguration.DefaultHostname),
                                                  Timeout:       TimeSpan.FromSeconds(10),
                                                  DNSClient:     dnsClient,
                                                  TimeProvider:  this.TimeProvider
                                              );

            // Last, and that is the whole precedence rule: what this
            // constructor was handed holds until the file says otherwise, and
            // what the file does not mention is left exactly as it was.
            if (configuration?.DNS is not null)
                ApplyDNSConfiguration(configuration.DNS);

            if (configuration?.NTS is not null)
                ApplyNTSConfiguration(configuration.NTS);

            this.ntsSettings = configuration?.NTS;

            #endregion

            #region What this vehicle is, and what it does with a station

            if (configuration?.Vehicle is not null)
                ApplyVehicleConfiguration(configuration.Vehicle);

            if (configuration?.V2G is not null)
                ApplyV2GConfiguration(configuration.V2G);

            if (configuration?.Session is not null)
                ApplySessionConfiguration(configuration.Session);

            // What was handed in wins, and the environment fills in the rest -
            // the precedence a command line expects. Neither is ever written to
            // the configuration file, which is the whole reason these do not
            // travel with the settings that name the files they open.
            this.Passwords = (Passwords ?? CertificatePasswords.None).Or(CertificatePasswords.FromEnvironment());

            this.Log.Info($"This vehicle is {VehicleName}, {StateOfCharge_percent:F0} % of {BatteryCapacity_kWh:F0} kWh.", "vehicle", "config");

            if (this.Passwords != CertificatePasswords.None)
                this.Log.Info($"Certificates: {this.Passwords}.", "15118", "config");

            #endregion

            #region The HTTP server, the JSON API and the web interface

            var address        = HTTPHostname ?? IPv4Address.Localhost;
            var port           = HTTPPort     ?? DefaultHTTPPort;

            this.httpServer    = HTTPServer   ?? new HTTPServer(
                                                     IPAddress:       address,
                                                     TCPPort:         port,
                                                     HTTPServerName:  $"OpenChargingCloud EV v{Version}",
                                                     DNSClient:       dnsClient
                                                 );

            this.httpRootPath  = HTTPRootPath ?? EVHTTPAPI.DefaultAPIPath;

            this.HTTPPort        = port;
            this.WebInterfaceURL = URL.Parse($"http://{address}:{port}/");

            // 1) The HTTPExt API at "/ext". First of the three, because it is
            //    the one with a database behind it: whatever it finds wrong
            //    with its files, it should say so before a port is opened and
            //    before anybody is let in against accounts that were not read.
            this.ExtAPI        = new HTTPExtAPI(
                                     HTTPServer:             httpServer,
                                     RootPath:               ExtAPIPath,
                                     HTTPServerName:         $"OpenChargingCloud EV v{Version}",
                                     HTTPServiceName:        $"OpenChargingCloud EV v{Version}",
                                     APIRobotEMailAddress:   EMailAddress.Parse("OpenChargingCloud EV Robot <robot@charging.cloud>"),
                                     APIRobotGPGPassphrase:  "",

                                     // Nothing here sends mail. A vehicle that
                                     // notifies by e-mail is told so by whoever
                                     // runs it, with a submission client of
                                     // their own; until then a mailer that
                                     // swallows what it is given is better than
                                     // one that quietly retries against a host
                                     // nobody configured.
                                     SMTPSubmissionClient:   new NullMailer(),
                                     DisableNotifications:   true,

                                     // The cookie has to reach "/api", and its
                                     // path would otherwise be the root path of
                                     // this API - "/ext" - so a browser signed
                                     // in at /ext/login would send nothing to
                                     // the API and look signed out everywhere
                                     // else.
                                     HTTPCookiePath:         "/",

                                     // A secure cookie is dropped by a browser
                                     // over plain HTTP, and a vehicle on a
                                     // bench is reached over plain HTTP. Tied
                                     // to the TLS the server is actually using
                                     // rather than switched off: on a vehicle
                                     // with a certificate this stays on.
                                     UseSecureCookies:       false,

                                     LoggingPath:            AccountsPath,
                                     DatabaseFileName:       DefaultAccountsDatabaseFile,

                                     // Left on, and that is what makes the
                                     // directory above: switching it off skips
                                     // the CreateDirectory that the accounts
                                     // file is written into, and the first
                                     // account created would fail on a path
                                     // that was never made.
                                     DisableLogging:         false
                                 );

            this.Log.Info($"The accounts of this vehicle are in '{ExtAPI.DatabaseFileName}', its HTTPExt API at '{ExtAPIPath}'.", "web", "http");

            // 2) The JSON API at "/api". Before the web interface, so that it
            //    is the more specific API and an unknown /api path never
            //    reaches the single-page-application stub below.
            this.API           = new EVHTTPAPI(
                                     HTTPServer:  httpServer,
                                     Vehicle:     this,
                                     ExtAPI:      ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     httpRootPath,
                                     Version:     Version
                                 );

            // 3) The web interface at "/": the files of the bundle, and the
            //    single-page-application stub for every other page URL, so
            //    that a reload on /logs and a bookmark to it both work.
            this.Frontend      = Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(EV).Assembly);

            if (this.Frontend.TryGet(IndexFile, out _))
            {

                this.WebInterface = httpServer.AddHTTPAPI();

                this.WebInterface.MapSinglePageApplication(
                    this.Frontend,
                    new SinglePageAppOptions {
                        IndexTransform = html => html.Replace("{{ServerVersion}}", $"v{Version}", StringComparison.Ordinal)
                    }
                );

                // Browsers ask for /favicon.ico whatever the page says, and a
                // bundle built by webpack carries an SVG. A literal route wins
                // over the catch-all, so this answers before the stub would -
                // and beats a 404 on every visit, which is a line in the log
                // and a broken icon in the tab.
                if (this.Frontend.TryGet(FaviconSVG, out _))
                    this.WebInterface.AddHandler(
                        HTTPPath.Parse("/favicon.ico"),
                        request => Task.FromResult(
                                       new HTTPResponse.Builder(request) {
                                           HTTPStatusCode  = HTTPStatusCode.TemporaryRedirect,
                                           Location        = Location.From(HTTPPath.Parse("/" + FaviconSVG)),
                                           CacheControl    = "public, max-age=3600"
                                       }.AsImmutable
                                   ),
                        HTTPMethod.GET
                    );

            }

            else
                this.Log.Error(
                    $"No web interface to serve ({this.Frontend.Description}): the JSON API answers, the browser gets nothing. " +
                    "Build the frontend (npm run build in Frontend/) or point the vehicle at a directory with --frontend.",
                    "web"
                );

            #region Every request, into the log

            httpServer.OnHTTPRequest  += (server, request, cancellationToken) => {

                // The event stream is one request that stays open for as long
                // as a browser has the page open; logging it would say nothing
                // and logging its response would say it at the wrong moment.
                if (!IsEventStream(request))
                    this.Log.Debug($"{request.HTTPMethod} {request.Path} from {request.RemoteSocket}", "http");

                return Task.CompletedTask;

            };

            // Only OnHTTPResponse, and not OnHTTPError beside it: Hermod raises
            // both for the same response, and one line per request is what a
            // log is for.
            httpServer.OnHTTPResponse += (server, request, response, cancellationToken) => {

                if (IsEventStream(request))
                    return Task.CompletedTask;

                var code = response.HTTPStatusCode.Code;

                this.Log.Log(
                    code >= 500 ? LogLevel.Error
                        // A 401 is how the web interface asks whether anybody
                        // is signed in, and the answer "nobody" is not a fault.
                        : code == 401 ? LogLevel.Debug
                        : code >= 400 ? LogLevel.Warning
                        : LogLevel.Debug,
                    $"{code} {response.HTTPStatusCode.Name} for {request.HTTPMethod} {request.Path}",
                    "http"
                );

                return Task.CompletedTask;

            };

            #endregion

            #endregion

        }

        #endregion


        #region Start()

        /// <summary>
        /// Start listening.
        /// </summary>
        public async Task Start()
        {

            if (started)
                return;

            // Before the port opens, and that order is the point: a web
            // interface reachable before its accounts exist is a door with
            // nobody behind it.
            await EnsureAccounts();

            try
            {
                await httpServer.Start();
            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(HTTPPort, problem);
            }

            StartCheckingTheClock();

            started = true;

            Log.Notice($"The web interface is listening on {WebInterfaceURL}", "web", "http");
            Log.Info   ($"The JSON API is at {WebInterfaceURL}{httpRootPath.ToString().Trim('/')}/v1/status", "web", "http");

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

        }

        #endregion

        #region Stop()

        /// <summary>
        /// Stop listening.
        /// </summary>
        public async Task Stop()
        {

            if (!started)
                return;

            Log.Notice("The electric vehicle is shutting down.", "vehicle");

            timeCheckTimer?.Dispose();
            timeCheckTimer = null;

            // Before the server, and that order is the whole point: every
            // browser with the Logs page open holds a request that is waiting
            // for the next log entry rather than for its socket, and the HTTP
            // server waits for every request it started. Closing the sockets
            // does not wake those, so they are ended here first.
            API.CloseEventStreams();

            await httpServer.Stop();

            started = false;

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

                var found = await V2GLink.DiscoverAsync(link, V2GSettings, Log, CancellationToken);

                found.JSON["interface"] = link.Name;

                lastDiscovery          = found.JSON;

                // Kept beside the JSON rather than dug back out of it: this is
                // where a link-local address still carries the scope id of the
                // interface it was heard on, and a session started right after
                // a discovery is the one caller that needs it.
                lastDiscoveryEndpoint  = found.Found;

                return lastDiscovery;

            }
            finally
            {
                discoveryLock.Release();
            }

        }

        #endregion


        #region (private) EnsureAccounts()

        /// <summary>
        /// Make the four groups and, at a first start, the one account that is
        /// in the last of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The groups are made every start rather than only the first, because
        /// they are this vehicle's vocabulary and not somebody's data: a group
        /// deleted by hand would otherwise leave a role that can never be held
        /// again, and the routes asking for it would refuse everybody with no
        /// way to put it right.
        /// </para>
        /// <para>
        /// The account is made only when there is none at all. Nobody can sign
        /// in to a web interface whose accounts are empty, and an
        /// unauthenticated setup page would be a door of its own - so the
        /// password is made up here and shown once, on the console, to whoever
        /// started the process. It is never written down: what the accounts
        /// hold is the hash the HTTPExt API makes of it.
        /// </para>
        /// <para>
        /// The membership edge is put on the group <em>before</em> the group is
        /// stored, so that both go to disk in one write. Adding it afterwards
        /// would leave a group that is right in memory and wrong in the file,
        /// and the next start would read the file.
        /// </para>
        /// </remarks>
        private async Task EnsureAccounts()
        {

            // Read what is on disk first. The HTTPExt API writes its accounts
            // as it goes but does not read them back when it is built, so a
            // vehicle that skipped this would find no accounts at every start,
            // make a second root beside the first, and refuse the password its
            // owner already has.
            await ExtAPI.LoadDatabase();

            var firstStart  = !ExtAPI.Users.Any();

            IUser?  admin   = null;

            #region The one account, when there is none

            if (firstStart)
            {

                var password  = RandomExtensions.RandomString(24);
                var userId    = User_Id.Parse(DefaultAdminUser);

                // CreateUser rather than AddUser: the password is set from
                // inside the OnAdded callback, where the user already has its
                // API back-reference, and that is the only place the password
                // store can be reached. AddUser followed by ChangePassword
                // looks equivalent and writes the account without one - which
                // is an account nobody can sign in to, and nothing says so.
                var organization  = await ExtAPI.CreateOrganizationIfNotExists(
                                              Organization_Id.Parse(DefaultOrganization),
                                              I18NString.Create(Languages.en, DefaultOrganization)
                                          );

                if (organization is not Organization vehicleOrganization)
                    throw new InvalidOperationException("The organization of this vehicle could not be created, and an account outside one cannot sign in.");

                admin         = await ExtAPI.CreateUser(
                                          userId,
                                          I18NString.Create(Languages.en, DefaultAdminUser),
                                          SimpleEMailAddress.Parse($"{DefaultAdminUser}@localhost"),
                                          User2OrganizationEdgeLabel.IsAdmin,
                                          vehicleOrganization,
                                          Password:                  password,

                                          // Nothing is sent and nobody is told:
                                          // a vehicle has no mail server, no
                                          // second user to notify, and the one
                                          // account it makes is announced on the
                                          // console it was started from.
                                          SkipDefaultNotifications:  true,
                                          SkipNewUserEMail:          true,
                                          SkipNewUserNotifications:  true,

                                          // Without this nobody can sign in, and
                                          // nothing says why: the sign-in paths
                                          // require an accepted EULA and refuse a
                                          // correct password without one. There is
                                          // no agreement to show here - whoever
                                          // started the process owns the vehicle -
                                          // so it is accepted at the moment the
                                          // account is made.
                                          AcceptedEULA:              TimeProvider.GetUtcNow().AddSeconds(-1),

                                          IsAuthenticated:           true
                                      );

                if (admin is null)
                    throw new InvalidOperationException("The account of this vehicle could not be created, so nobody could sign in to it.");

                GeneratedPassword = password;

                Log.Notice($"No accounts were found, so '{DefaultAdminUser}' was made up and put in the {UserRole.SystemAdmin.Name} group.",
                           "web", "auth");

            }

            #endregion

            #region The four groups

            foreach (var role in UserRole.All)
            {

                if (ExtAPI.TryGetUserGroup(role.GroupId, out _))
                    continue;

                await ExtAPI.AddUserGroup(
                          new UserGroup(
                              role.GroupId,
                              I18NString.Create(Languages.en, role.Name)
                          )
                      );

            }

            #endregion

            #region The one account joins the one group that can fix the rest

            // Through AddUserToUserGroup, which writes a command of its own.
            // Putting the edge on the group object before storing it looks
            // equivalent and is not: what AddUserGroup writes is the group,
            // and a group's stored form does not carry its members - so the
            // membership was there until the next start and gone after it,
            // which is the worst shape a permission can have.
            if (admin is not null &&
                ExtAPI.TryGetUser     (admin.Id,                     out var storedAdmin) &&
                ExtAPI.TryGetUserGroup(UserRole.SystemAdmin.GroupId, out var adminGroup)  &&
                storedAdmin is User user &&
                adminGroup  is UserGroup group)
            {

                await ExtAPI.AddUserToUserGroup(
                          user,
                          User2UserGroupEdgeLabel.IsAdmin,
                          group
                      );

            }

            #endregion

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this vehicle is, as the Configuration page of the web
        /// interface reads it.
        /// </summary>
        /// <remarks>
        /// Read-only: it answers "what am I running", not "change it". Nothing
        /// here is a secret - the web login appears with its username and the
        /// path of its file, and never with anything about its password.
        /// </remarks>
        public JObject ConfigurationJSON()

            => new (

                   new JProperty("vehicle",    new JObject(
                       new JProperty("name",           VehicleName),
                       new JProperty("vin",            VIN),
                       new JProperty("version",        Version),
                       new JProperty("createdAt",      CreatedAt.ToString("o")),
                       new JProperty("machine",        Environment.MachineName),
                       new JProperty("runtime",        Environment.Version.ToString()),
                       new JProperty("os",             Environment.OSVersion.ToString())
                   )),

                   new JProperty("battery",    new JObject(
                       new JProperty("capacityKWh",                BatteryCapacity_kWh),
                       new JProperty("stateOfChargePercent",       StateOfCharge_percent),
                       new JProperty("targetStateOfChargePercent", TargetStateOfCharge_percent),
                       new JProperty("maxChargingPowerKW",         MaxChargingPower_kW),
                       new JProperty("taperFromPercent",           TaperFrom_percent)
                   )),

                   new JProperty("http",       new JObject(
                       new JProperty("serverName",     httpServer.HTTPServerName),
                       new JProperty("url",            WebInterfaceURL.ToString()),
                       new JProperty("apiPath",        httpRootPath.ToString()),
                       new JProperty("running",        started),
                       new JProperty("frontend",       Frontend.Description),
                       new JProperty("webInterface",   WebInterface is not null)
                   )),

                   new JProperty("web",        new JObject(
                       new JProperty("accountsPath",   AccountsPath),
                       new JProperty("extAPIPath",     ExtAPIPath.ToString()),
                       new JProperty("users",          ExtAPI.Users.     Count()),
                       new JProperty("groups",         ExtAPI.UserGroups.Count()),
                       new JProperty("cookie",         ExtAPI.SessionCookieName.ToString()),
                       new JProperty("maxLifetime",    ExtAPI.MaxSignInSessionLifetime.ToString())
                   )),

                   new JProperty("log",        new JObject(
                       new JProperty("capacity",       Log.Capacity),
                       new JProperty("entries",        Log.Count),
                       new JProperty("lastId",         Log.LastId),
                       new JProperty("debugBridge",    traceBridge is not null),
                       new JProperty("console",        consoleLog is not null),
                       new JProperty("tags",           new JArray(Log.KnownTags))
                   )),

                   new JProperty("v2g",        new JObject(
                       new JProperty("interface",      V2GSettings.InterfaceName),
                       new JProperty("candidates",     new JArray(V2GLink.Candidates().Select(candidate => candidate.Name))),
                       new JProperty("lastDiscovery",  lastDiscovery)
                   )),

                   new JProperty("time",       new JObject(
                       new JProperty("nts",            ntsClient.Hostname.ToString()),
                       new JProperty("now",            TimeProvider.GetUtcNow().ToString("o"))
                   )),

                   new JProperty("assemblies", new JArray(
                       AssemblyJSON<HTTPServer>                                                   ("Hermod"),
                       AssemblyJSON<NTSClient>                                                    ("Norn"),
                       AssemblyJSON<protocols.ISO15118.SDP.Client.EVCC_SDPClient>                 ("ISO 15118 SDP"),
                       AssemblyJSON<protocols.ISO15118.NetworkInterfaces.V2GNetworkInterface>     ("ISO 15118 interfaces")
                   ))

               );

        #endregion

        #region (private static) AssemblyJSON<T>(Name)

        private static JObject AssemblyJSON<T>(String Name)
        {

            var assembly = typeof(T).Assembly.GetName();

            return new JObject(
                       new JProperty("name",      Name),
                       new JProperty("assembly",  assembly.Name),
                       new JProperty("version",   assembly.Version?.ToString(3))
                   );

        }

        #endregion

        #region (private static) IsEventStream(Request)

        /// <summary>
        /// Whether this request is a browser hanging on the event stream.
        /// </summary>
        private static Boolean IsEventStream(HTTPRequest Request)
            => Request.Path.ToString().EndsWith("/events", StringComparison.Ordinal);

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop listening and let go of the console and the debug bridge.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            await Stop();

            traceBridge?.Dispose();
            consoleLog? .Dispose();

            sessionLock.    Dispose();
            discoveryLock.  Dispose();
            reconfigureLock.Dispose();

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
