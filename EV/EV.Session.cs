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

using System.Net;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.Discovery;
using cloud.charging.open.protocols.ISO15118.Session;
using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.Simulation;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.Transport;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.protocols.WWCP.node;

#endregion

namespace cloud.charging.open.EV
{

    /// <summary>
    /// Charging: what this vehicle does once it has found a station.
    /// </summary>
    /// <remarks>
    /// One session at a time, and that is not a limitation of the code. A vehicle has one cable. Two
    /// sessions would be two vehicles, and the second one would be charging through the first one's
    /// battery.
    ///
    /// Everything here writes into the log while it happens rather than returning a transcript. A session
    /// is hundreds of exchanges, so the question somebody actually has while one runs is "where is it
    /// now" - which only the Logs page and the event stream can answer. What comes back at the end is the
    /// sum, for the page to show when it is over.
    /// </remarks>
    public partial class EV
    {

        #region Data

        /// <summary>
        /// Lets one session run at a time.
        /// </summary>
        /// <remarks>
        /// Refused rather than queued, like the discovery beside it: a second session waiting behind the
        /// first would start minutes after somebody asked for it, against a station they may have walked
        /// away from.
        /// </remarks>
        private readonly  SemaphoreSlim             sessionLock = new (1, 1);

        /// <summary>
        /// What stops the session that is running, or null while none is.
        /// </summary>
        /// <remarks>
        /// Held here rather than passed around because the request that stops a session is not the request
        /// that started it: one is a browser hanging on a POST that will not answer for ten minutes, the
        /// other is somebody pressing a button in the meantime.
        /// </remarks>
        private           CancellationTokenSource?  sessionCancellation;

        /// <summary>
        /// How the last session went, as the web interface reads it, or null while none has been run.
        /// </summary>
        private           JObject?                  lastSession;

        /// <summary>
        /// What a paused session left behind, so that the next run can rejoin it.
        /// </summary>
        /// <remarks>
        /// Kept in memory and not written down. A paused session is a station holding a cable and a
        /// reservation open for a few minutes; it does not survive the vehicle being restarted, and
        /// pretending otherwise would produce a rejoin attempt against a session that ended hours ago.
        /// </remarks>
        private           ResumableSession?         pausedSession;

        #endregion

        #region Properties

        /// <summary>
        /// What one session does, beyond what the vehicle itself is.
        /// </summary>
        public SessionConfiguration   SessionSettings  { get; private set; } = new ();

        /// <summary>
        /// Whether a session is running right now.
        /// </summary>
        public Boolean                SessionRunning
            => sessionCancellation is not null;

        #endregion


        #region RunSessionAsync(Connect = null, Pause = false, ResumeFrom = null, PauseResume = false, ...)

        /// <summary>
        /// Drive up to a station and charge.
        /// </summary>
        /// <remarks>
        /// The whole of it: the SLAC pairing where one is configured, then the station - named or found
        /// over SDP - then the protocol handshake, then the session itself to <c>SessionStop</c>.
        ///
        /// One at a time. A second request while one is running is answered with what is happening rather
        /// than queued behind it.
        /// </remarks>
        /// <param name="Connect">The station, as <c>host:port</c>; without one the configured station, and without that a station found over SDP.</param>
        /// <param name="Pause">Whether to end the session paused rather than terminated, so that it can be rejoined later.</param>
        /// <param name="ResumeFrom">A paused session's identification in hexadecimal, to rejoin one this vehicle did not pause itself.</param>
        /// <param name="PauseResume">Pause after charging, reconnect, and rejoin - both halves in one run.</param>
        /// <param name="CancellationToken">Abort the session. What has already been metered stays metered.</param>
        public async Task<JObject> RunSessionAsync(String?            Connect             = null,
                                                   Boolean            Pause               = false,
                                                   String?            ResumeFrom          = null,
                                                   Boolean            PauseResume         = false,
                                                   CancellationToken  CancellationToken   = default)
        {

            if (!await sessionLock.WaitAsync(0, CancellationToken))
                return new JObject(
                           new JProperty("outcome",  "busy"),
                           new JProperty("error",    "A session is already running on this vehicle. It appears in the log as it happens, and on this page when it ends.")
                       );

            // Linked, so that either the browser going away or the Stop button
            // ends it - and so that the token the state machines are given is
            // the one thing both of those reach.
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

            sessionCancellation = cancellation;

            try
            {

                #region Who is at the other end of the cable

                // SLAC first, when there is a peer to pair with: on a real
                // vehicle this is what decides which of the stations that can
                // hear it is the one its cable is plugged into, and it happens
                // before there is an IP link to discover anything over.
                JObject? slac = null;

                if (SessionSettings.SLACPeer is { } peer)
                {

                    var endpoint = V2GEndpoint.Parse(peer, "'session.slacPeer'");

                    slac = await V2GLink.PairAsync(
                               endpoint.IPEndPoint ?? new IPEndPoint(IPAddress.Parse(endpoint.ConnectHost), endpoint.Port),
                               Log,
                               cancellation.Token
                           );

                    if (slac.Value<String>("outcome") != "paired")
                        return Remember(Failed("slacFailed",
                                               slac.Value<String>("error") ?? "The SLAC pairing did not complete.",
                                               slac));

                }

                // Or the coupler's bus, on a Megawatt Charging System: joined
                // here, before SDP, and left when the session ends - which is
                // what the "await using" is. A vehicle on a T1S bus is a node
                // the station asks every cycle, for as long as it is plugged
                // in, and a session that attached and never left would leave
                // the station polling a node that had driven away.
                JObject? t1s = null;

                await using var bus = SessionSettings.T1STransportInEffect != T1STransportKind.None
                                          ? await V2GLink.AttachAsync(
                                                V2GLink.T1SMediumFor(SessionSettings, V2GSettings.InterfaceName),
                                                SessionSettings.T1SWeightInEffect,
                                                Log,
                                                CancellationToken:  cancellation.Token
                                            )
                                          : null;

                if (bus is not null)
                {

                    t1s = bus.JSON;

                    if (!bus.IsAttached && !bus.IsDeclined)
                        return Remember(Failed("t1sFailed",
                                               t1s.Value<String>("error") ?? "The vehicle could not join the coupler's bus.",
                                               slac, T1S: t1s));

                }

                #endregion

                #region Where to

                var station = await StationForSessionAsync(Connect, cancellation.Token);

                if (station.Endpoint is null)
                    return Remember(Failed(station.Outcome, station.Error!, slac, station.Discovery));

                #endregion

                #region What to run it with

                var options = OptionsFor(station.Endpoint, Pause || PauseResume, ResumeFrom);

                #endregion

                #region One connection, or the two halves of a pause and a rejoin

                JObject result;

                if (!PauseResume)
                {

                    var run = await V2GSession.RunAsync(options, Log, cancellation.Token);

                    pausedSession  = Pause ? run.Paused : null;
                    result         = run.JSON;

                    if (Pause && run.Paused is not null)
                        result["pausedSessionId"] = Convert.ToHexString(run.Paused.SessionId);

                }

                else
                {

                    // One pack for both connections. A vehicle's battery does
                    // not forget what it took while the cable was out, and
                    // building a fresh one per connection is what made a
                    // resumed session ask for the whole of its original
                    // energy again - [V2G2-743] says it asks for the
                    // remainder.
                    var pack   = options.Battery;

                    var first  = await V2GSession.RunAsync(options, Log, cancellation.Token);

                    Log.Notice($"Session: paused ({Convert.ToHexString(first.Paused?.SessionId ?? [])}); reconnecting to rejoin it.",
                               "15118", "session");

                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);

                    // A fresh connection, including a fresh discovery where
                    // this run is discovering: a station that moved ports
                    // between the two halves is exactly what the second half
                    // is there to survive.
                    var again  = await StationForSessionAsync(Connect, cancellation.Token);

                    if (again.Endpoint is null)
                        return Remember(Failed(again.Outcome, again.Error!, slac, again.Discovery, first.JSON));

                    var second = await V2GSession.RunAsync(
                                     options with {
                                         Host     = again.Endpoint.Host,
                                         Port     = again.Endpoint.Port,
                                         Pause    = false,
                                         Resume   = first.Paused,
                                         Battery  = pack
                                     },
                                     Log,
                                     cancellation.Token
                                 );

                    pausedSession       = null;
                    result              = second.JSON;
                    result["pausedRun"] = first.JSON;

                }

                #endregion

                if (slac is not null)
                    result["slac"] = slac;

                if (t1s is not null)
                    result["t1s"] = t1s;

                return Remember(result);

            }
            catch (OperationCanceledException)
            {

                Log.Info("Session: the session was stopped.", "15118", "session");

                return Remember(Failed("cancelled", "The session was stopped."));

            }
            catch (Exception e)
            {

                // A station that is not there, a TLS handshake that could not
                // be made, a protocol the two sides could not agree on: all of
                // it arrives here as one exception, and the log already has the
                // step it happened at.
                Log.Error($"Session: the session failed: {e.Message}", "15118", "session");

                return Remember(Failed("failed", e.Message));

            }
            finally
            {

                sessionCancellation = null;

                cancellation.Dispose();
                sessionLock.Release();

            }

        }

        #endregion

        #region PairAsync(CancellationToken = default)

        /// <summary>
        /// Run the SLAC pairing stage on its own, against the configured peer.
        /// </summary>
        /// <remarks>
        /// A session does this by itself where a peer is configured, so this is here for the half that
        /// fails first and fails differently: SLAC agreeing and SDP then finding nothing is a very
        /// different link from SLAC never agreeing at all, and asking the two questions separately is what
        /// tells them apart.
        ///
        /// Refused while a session is running, because a vehicle that re-pairs mid-charge is agreeing with
        /// a station about a network it is already using.
        /// </remarks>
        public async Task<JObject> PairAsync(CancellationToken CancellationToken = default)
        {

            if (SessionSettings.SLACPeer is not { } peer)
                return new JObject(
                           new JProperty("outcome",  "notConfigured"),
                           new JProperty("error",    "No SLAC peer is configured.")
                       );

            if (SessionRunning)
                return new JObject(
                           new JProperty("outcome",  "busy"),
                           new JProperty("error",    "A session is running on this vehicle, and it is already paired.")
                       );

            var endpoint = V2GEndpoint.Parse(peer, "'session.slacPeer'");

            return await V2GLink.PairAsync(
                       endpoint.IPEndPoint ?? new IPEndPoint(IPAddress.Parse(endpoint.ConnectHost), endpoint.Port),
                       Log,
                       CancellationToken
                   );

        }

        #endregion

        #region AttachToBusAsync(CancellationToken = default)

        /// <summary>
        /// Join the coupler's 10BASE-T1S bus on its own, against the configured
        /// bus, stay on it long enough to be asked a few times, and leave.
        /// </summary>
        /// <remarks>
        /// A session does this by itself where a bus is configured, so this is
        /// here for the same reason the SLAC stage has one: joining and then
        /// finding no station over SDP is a different link from never being
        /// given a node identifier at all, and asking the two questions
        /// separately is what tells them apart.
        ///
        /// Stays on the bus for two seconds before leaving, so that the
        /// station's log shows this vehicle being asked and answering rather
        /// than a node that joined and left within one cycle.
        /// </remarks>
        public async Task<JObject> AttachToBusAsync(CancellationToken CancellationToken = default)
        {

            if (SessionSettings.T1STransportInEffect == T1STransportKind.None)
                return new JObject(
                           new JProperty("outcome",  "notConfigured"),
                           new JProperty("error",    "No T1S transport is configured.")
                       );

            if (SessionRunning)
                return new JObject(
                           new JProperty("outcome",  "busy"),
                           new JProperty("error",    "A session is running on this vehicle, and it is already on the bus.")
                       );

            await using var bus = await V2GLink.AttachAsync(
                                      V2GLink.T1SMediumFor(SessionSettings, V2GSettings.InterfaceName),
                                      SessionSettings.T1SWeightInEffect,
                                      Log,
                                      CancellationToken:  CancellationToken
                                  );

            if (bus.IsAttached)
            {

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);
                }
                catch (OperationCanceledException) { }

                bus.JSON["stayed_ms"] = 2000;

            }

            return bus.JSON;

        }

        #endregion

        #region CancelSession()

        /// <summary>
        /// Stop the session that is running. False when there was none.
        /// </summary>
        /// <remarks>
        /// What has already been metered stays metered: this ends the exchange, it does not undo it. A
        /// station on the other side sees a vehicle that stopped answering, which is what a vehicle driving
        /// off mid-session looks like to one.
        /// </remarks>
        public Boolean CancelSession()
        {

            var running = sessionCancellation;

            if (running is null)
                return false;

            Log.Notice("Session: stopping, because somebody asked.", "15118", "session");

            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The session ended between the check above and here, which is
                // not a failure to stop it - it is already stopped.
                return false;
            }

            return true;

        }

        #endregion


        #region (private) StationForSessionAsync(Connect, CancellationToken)

        /// <summary>
        /// Where this session goes: what the caller named, what the file says, or whatever answers on the
        /// link.
        /// </summary>
        private async Task<(SeccEndpoint? Endpoint, String Outcome, String? Error, JObject? Discovery)>
            StationForSessionAsync(String?            Connect,
                                   CancellationToken  CancellationToken)
        {

            var named = Connect ?? SessionSettings.Connect;

            if (named is not null)
            {

                var endpoint = V2GEndpoint.Parse(named, Connect is not null ? "the station to connect to" : "'session.connect'");

                // Whether the station speaks TLS is this vehicle's choice when
                // it was told where to go - there is no SDP answer to read it
                // out of - so the endpoint carries what the settings say.
                return (new SeccEndpoint(
                            IPAddress.TryParse(endpoint.ConnectHost, out var address)
                                ? address
                                : (await Dns.GetHostAddressesAsync(endpoint.Host, CancellationToken)).First(),
                            endpoint.Port,
                            (SessionSettings.TLS ?? TlsStack.None) != TlsStack.None
                        ),
                        "named", null, null);

            }

            // Nothing named: find one. The discovery writes itself into the log
            // exactly as the button on the page does, because it is the same
            // call.
            var discovery = await DiscoverAsync(CancellationToken: CancellationToken);

            // This discovery's outcome, and not merely "is there an endpoint
            // from some discovery": a request that was refused because another
            // one was already running leaves the previous run's endpoint
            // standing, and connecting to that would be a session against a
            // station this run never looked for.
            if (discovery.Value<String>("outcome") != "found" ||
                lastDiscoveryEndpoint is null)
            {
                return (null,
                        "noStation",
                        discovery.Value<String>("error")
                            ?? "No station answered, and none was configured to connect to.",
                        discovery);
            }

            return (lastDiscoveryEndpoint, "discovered", null, discovery);

        }

        #endregion

        #region (private) OptionsFor(Station, Pause, ResumeFrom)

        /// <summary>
        /// One run, assembled: what the file says, what this vehicle is, and the defaults wherever neither
        /// says anything.
        /// </summary>
        private SessionOptions OptionsFor(SeccEndpoint  Station,
                                          Boolean       Pause,
                                          String?       ResumeFrom)
        {

            var settings = SessionSettings;

            // A resume this vehicle did not pause itself carries the
            // identification alone: there is no binding to compare against, and
            // no meter reading to carry forward. Deliberate - see
            // Evcc20Base.ResumeBinding for why a vehicle may proceed where a
            // station may not.
            var resume   = ResumeFrom is not null
                               ? new ResumableSession(Convert.FromHexString(ResumeFrom), null, 0)
                               : pausedSession;

            return new SessionOptions {

                       Host                 = Station.Host,
                       Port                 = Station.Port,

                       Protocol             = settings.Protocol  ?? ProtocolVariant.Iso15118_20,
                       OfferBoth            = settings.OfferBoth ?? true,
                       Mode                 = settings.Mode      ?? PowerMode.Dc,
                       MCS                  = settings.MCS       ?? false,
                       TLS                  = settings.TLS       ?? TlsStack.None,

                       PKIDirectory         = settings.PKIDirectory,

                       VehicleCertificate   = Resolve(settings.VehicleCertificate,  CertificateKind.Vehicle,            "vehicleCertificate"),
                       ContractCertificate  = Resolve(settings.ContractCertificate, CertificateKind.Contract,           "contractCertificate"),
                       OEMCertificate       = Resolve(settings.OEMCertificate,      CertificateKind.OEMProvisioning,    "oemCertificate"),
                       TariffCertificate    = Resolve(settings.TariffCertificate,   CertificateKind.TariffVerification, "tariffCertificate"),

                       V2GRoots             = ValidatorFor(Certificates, CertificateKind.V2GRoot, Log),
                       MORoots              = ValidatorFor(Certificates, CertificateKind.MORoot,  Log),
                       OEMRoots             = ValidatorFor(Certificates, CertificateKind.OEMRoot, Log),

                       Battery              = BuildBattery(),

                       DepartureTime        = settings.DepartureIn is { } departure
                                                  ? (UInt32) Math.Max(1, Math.Round(departure.TotalSeconds))
                                                  : null,

                       Renegotiate          = settings.Renegotiate ?? false,

                       Pause                = Pause,
                       Resume               = resume

                   };

        }

        #endregion

        #region (private) Resolve(Handle, Kind, Field)

        /// <summary>
        /// One of the session's certificate handles, as the file a loader can open.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Refused rather than skipped, and refused here rather than at the handshake. A session that named
        /// a certificate somebody has since switched off, deleted or let expire is a session whose operator
        /// believes it is charging under that contract - so it does not quietly run without one. The
        /// exception lands in the session's own error handling and comes back as a failed session naming
        /// the certificate, which is the shortest path from the symptom to the cause.
        /// </para>
        /// <para>
        /// The kind is checked again although
        /// <see cref="TryUpdateSessionConfiguration"/> checked it when the setting was made: the store can
        /// change under a setting that was good when it was written, and by this point it is being handed
        /// to a loader that would read it as something it is not.
        /// </para>
        /// </remarks>
        private String? Resolve(String?          Handle,
                                CertificateKind  Kind,
                                String           Field)
        {

            if (Handle is null)
                return null;

            var entry = Certificates.Get(Handle)
                            ?? throw new ArgumentException(
                                   $"session.{Field}: there is no certificate '{Handle}' in this vehicle's store " +
                                    "any more. Choose another one, or import it again.");

            if (entry.Kind != Kind)
                throw new ArgumentException(
                          $"session.{Field}: '{entry.Label}' is a {entry.Kind.AsText()} and this names a " +
                          $"{Kind.AsText()}.");

            if (!entry.IsActive)
                throw new ArgumentException(
                          $"session.{Field}: '{entry.Label}' is switched off in the certificate store. " +
                           "Switch it on, or choose another one.");

            if (entry.IsExpired)
                throw new ArgumentException(
                          $"session.{Field}: '{entry.Label}' expired on {entry.NotAfter.UtcDateTime:yyyy-MM-dd}.");

            if (entry.IsNotYetValid)
                throw new ArgumentException(
                          $"session.{Field}: '{entry.Label}' is not valid until {entry.NotBefore.UtcDateTime:yyyy-MM-dd}.");

            var path = Certificates.FullPath(entry);

            return File.Exists(path)
                       ? path
                       : throw new ArgumentException(
                             $"session.{Field}: '{entry.Label}' is in the index and its file '{entry.FileName}' " +
                              "is gone. Import it again.");

        }

        #endregion

        #region (private) BuildBattery()

        /// <summary>
        /// The pack this vehicle charges, and every goal that ends the session.
        /// </summary>
        /// <remarks>
        /// Always built, unlike the EVCC of WWCP_ISO15118 where the nine battery flags turn one on. The
        /// difference is deliberate: that program's default is a three-iteration message sequence because
        /// every recorded interop run was taken at one, and this vehicle's battery is configuration it
        /// always has - there is no state in which it does not know how big its own pack is.
        ///
        /// Which means a bare run charges to full, and one iteration is one simulated minute, so a full
        /// charge is several hundred exchanges. Against a loopback station that is seconds; against a live
        /// one, name a charging time.
        /// </remarks>
        private EvBattery BuildBattery()

            => new (BatteryCapacity_kWh, StateOfCharge_percent) {

                   TargetSoC        = TargetStateOfCharge_percent,
                   TargetEnergyWh   = SessionSettings.TargetEnergy_kWh * 1000,
                   MaxDuration      = SessionSettings.MaxChargingTime,
                   DepartureIn      = SessionSettings.DepartureIn,
                   MinimumSoC       = SessionSettings.MinimumStateOfCharge_percent,
                   RequestedPowerW  = MaxChargingPower_kW * 1000,
                   TaperFromSoC     = TaperFrom_percent

               };

        #endregion

        #region (private) Failed(Outcome, Error, ...) / Remember(Result)

        /// <summary>
        /// A run that did not get as far as a session, in the shape every run answers with.
        /// </summary>
        private static JObject Failed(String    Outcome,
                                      String    Error,
                                      JObject?  SLAC        = null,
                                      JObject?  Discovery   = null,
                                      JObject?  PausedRun   = null,
                                      JObject?  T1S         = null)
        {

            var json = new JObject(
                           new JProperty("outcome",  Outcome),
                           new JProperty("error",    Error)
                       );

            if (SLAC      is not null)  json.Add("slac",      SLAC);
            if (T1S       is not null)  json.Add("t1s",       T1S);
            if (Discovery is not null)  json.Add("discovery", Discovery);
            if (PausedRun is not null)  json.Add("pausedRun", PausedRun);

            return json;

        }

        /// <summary>
        /// Keep this run as the one the page shows, and hand it back.
        /// </summary>
        private JObject Remember(JObject Result)
        {

            // "busy" deliberately never gets here: a request that was refused
            // because another session is running did not happen, and must not
            // replace the result of the one that did.
            lastSession = Result;

            return Result;

        }

        #endregion

    }

}
