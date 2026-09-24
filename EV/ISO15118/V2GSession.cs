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

using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.Sap;
using cloud.charging.open.protocols.ISO15118.Security;
using cloud.charging.open.protocols.ISO15118.Session;
using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.Simulation;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso2;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso20;
using cloud.charging.open.protocols.ISO15118.Timing;
using cloud.charging.open.protocols.ISO15118.Transport;

// System.Diagnostics carries an EventLog of its own, and this project's is the
// one every line below means.
using Stopwatch = System.Diagnostics.Stopwatch;

using Iso2   = cloud.charging.open.protocols.ISO15118_2.Generated;
using Iso20  = cloud.charging.open.protocols.ISO15118_20.CommonMessages.Generated;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.EV.ISO15118
{

    /// <summary>
    /// What came of one session: everything the page shows, and the handle a pause leaves behind.
    /// </summary>
    /// <param name="JSON">The run, as the web interface reads it.</param>
    /// <param name="Paused">What a rejoin would need, when the session ended paused.</param>
    public sealed record SessionResult(JObject            JSON,
                                       ResumableSession?  Paused);


    /// <summary>
    /// One ISO 15118 session, from the TCP connection to <c>SessionStop</c>.
    /// </summary>
    /// <remarks>
    /// The behaviour is not here. It is in <c>WWCP_ISO15118_Session</c> - <c>Evcc2</c> for ISO 15118-2 and
    /// the <c>Evcc20*</c> family for -20 - which is the same code the conformance harnesses drive and the
    /// same code the station's side is written against. What is here is the wiring those state machines
    /// need and the running commentary they do not produce: which protocol the handshake settled on, what
    /// the TLS layer turned out to be, and what the run added up to.
    ///
    /// <b>Everything goes into the log as it happens.</b> A session is hundreds of exchanges and the one
    /// question somebody has while it runs is "where is it now", which a result returned at the end cannot
    /// answer. The Logs page and the Server-Sent Events stream are how a run is watched, so this writes
    /// there rather than returning a transcript - the tags are <c>15118</c> and one of <c>sap</c>,
    /// <c>tls</c>, <c>session</c>.
    /// </remarks>
    public static class V2GSession
    {

        #region Data

        /// <summary>
        /// How long one message may take before the state machines give up on it.
        /// </summary>
        /// <remarks>
        /// A flat two seconds, which is not what ISO 15118's performance tables say: they give each
        /// message its own budget, and some of them are much shorter. This vehicle is a conformance and
        /// research peer rather than a car, and one figure that is comfortably longer than every real
        /// budget is the honest simplification - a timeout that fires is then a counterparty that stopped
        /// answering, never this side being stricter than the standard.
        /// </remarks>
        public static readonly TimeSpan  MessageTimeout = TimeSpan.FromSeconds(2);

        #endregion


        #region RunAsync(Options, Log, CancellationToken = default)

        /// <summary>
        /// Connect to a station, agree on a protocol, and charge until the session ends.
        /// </summary>
        /// <param name="Options">Where to, what to speak, and what this vehicle wants.</param>
        /// <param name="Log">Where the run is written while it happens.</param>
        /// <param name="CancellationToken">Abort the session. What has already been metered stays metered.</param>
        public static async Task<SessionResult> RunAsync(SessionOptions     Options,
                                                         EventLog           Log,
                                                         CancellationToken  CancellationToken   = default)
        {

            var started  = DateTimeOffset.UtcNow;
            var watch    = Stopwatch.StartNew();

            #region Whose word this vehicle takes, and for what

            // Three questions, three answers, and none of them stands in for
            // another. The store built these out of its active roots, so a root
            // switched off between two sessions is believed in the first and not
            // in the second, without anything here having to know that happened.

            var trust = Options.V2GRoots;

            if (trust is not null)
                Log.Info($"TLS: a station's certificate has to chain to {String.Join(", ", trust.RootSubjects)}.",
                         "15118", "tls");

            if (Options.MORoots is not null)
                Log.Info($"Plug & Charge: a contract certificate has to chain to {String.Join(", ", Options.MORoots.RootSubjects)}.",
                         "15118", "pnc");

            if (Options.OEMRoots is not null)
                Log.Info($"CertificateInstallation: an OEM provisioning certificate has to chain to {String.Join(", ", Options.OEMRoots.RootSubjects)}.",
                         "15118", "pnc");

            #endregion

            Log.Notice($"Session: connecting to [{Options.Host}]:{Options.Port} - {Options}.", "15118", "session");

            await using var stream     = await ConnectAsync(Options, trust, Log, CancellationToken);

            var             transport  = TransportOf(Options, stream, Log);
            var             options    = Options;

            #region The handshake, and what it settled on

            if (Options.OfferBoth)
            {

                // The state machine is chosen AFTER the handshake: offer both,
                // run whichever the station picked. That is the case a
                // multiplexing station exists for, and the reason this is not
                // decided when the options are built.
                var accepted = await SapHandshake.RunEvccSideAsync(
                                         stream,
                                         [ new SapOffer(ProtocolVariant.Iso15118_20, Options.Mode),
                                           new SapOffer(ProtocolVariant.Iso15118_2,  Options.Mode) ],
                                         CancellationToken,
                                         transport: transport
                                     );

                Log.Notice($"SAP: offered ISO 15118-20 (priority 1) and -2 (priority 2); " +
                           $"the station picked {V2GInterface.Name(accepted.Protocol)}.",
                           "15118", "sap");

                options = Options with { Protocol = accepted.Protocol };

            }

            else
            {

                await SapHandshake.RunEvccSideAsync(stream, Options.Protocol, CancellationToken,
                                                   mode: Options.Mode, transport: transport);

                Log.Notice($"SAP: offered ISO 15118{V2GInterface.Name(Options.Protocol)} alone, and the station took it.",
                           "15118", "sap");

            }

            #endregion

            var result = options.Protocol == ProtocolVariant.Iso15118_2
                             ? await RunIso2Async (stream, options, Log, CancellationToken)
                             : await RunIso20Async(stream, options, Log, CancellationToken);

            watch.Stop();

            result.JSON["startedAt"]   = started.ToString("o");
            result.JSON["elapsed_ms"]  = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
            result.JSON["station"]     = $"[{Options.Host}]:{Options.Port}";
            result.JSON["protocol"]    = V2GInterface.Name(options.Protocol);
            result.JSON["mode"]        = options.MCS ? "MCS" : V2GInterface.Name(options.Mode);

            Log.Notice($"Session: {result.JSON.Value<String>("outcome")} after {watch.Elapsed.TotalSeconds:F1} s.",
                       "15118", "session");

            return result;

        }

        #endregion


        #region (private static) RunIso2Async (Stream, Options, Log, CancellationToken)

        /// <summary>
        /// One ISO 15118-2 session.
        /// </summary>
        private static async Task<SessionResult> RunIso2Async(Stream             Stream,
                                                              SessionOptions     Options,
                                                              EventLog           Log,
                                                              CancellationToken  CancellationToken)
        {

            var evcc = new Evcc2(Stream, Options.Mode, TimeProvider.System, new TaskAsyncDelay(), MessageTimeout) {

                           StopMode          = Options.Pause
                                                   ? Iso2.ChargingSession.Pause
                                                   : Iso2.ChargingSession.Terminate,

                           ResumeSessionId   = Options.Resume?.SessionId,
                           Renegotiate       = Options.Renegotiate,
                           Battery           = Options.Battery,

                           // [V2G2-743]: a resumed session asks for the
                           // remainder, not for the whole of what it originally
                           // wanted. Only the batteryless case needs telling -
                           // a pack that came along already knows what it took.
                           AlreadyChargedWh  = Options.Resume?.DeliveredWh ?? 0

                       };

            if (Options.ContractCertificate is not null)
                evcc.Pnc = VehicleCredentials.LoadContract(Options.ContractCertificate, Options.MORoots, Log);

            if (Options.TariffCertificate is not null)
                evcc.TariffVerifyKey = VehicleCredentials.LoadTariffVerifyKey(Options.TariffCertificate, Log);

            // Accepted and refused nowhere, and there is no -2 path that uses
            // it: this vehicle implements CertificateInstallation only for -20.
            // Said out loud rather than ignored, because a setting that is
            // quietly dropped is worse than one that is not offered.
            if (Options.OEMCertificate is not null)
                Log.Warning("CertificateInstallation: the OEM certificate is not used in an ISO 15118-2 session - " +
                            "this vehicle implements it only for -20. Pin the protocol to \"20\" to use it.",
                            "15118", "pnc");

            await evcc.RunAsync(CancellationToken);

            #region What it added up to

            Log.Notice($"Session: {evcc.Exchanges} exchanges, {evcc.BytesOnWire} bytes on the wire (request side), " +
                       $"auth {evcc.AuthorizationMode}, {evcc.MeteringReceiptsSent} metering receipt(s), " +
                       $"{evcc.Renegotiations} renegotiation(s), session setup {evcc.SessionSetupCode}.",
                       "15118", "session");

            if (evcc.Battery is { } battery && evcc.BatteryStop is { } stop)
                Log.Notice($"Battery: {battery.Describe(stop)}", "15118", "session");

            if (evcc.Tariff is { } tariff)
                Log.Info($"Tariff: {tariff.TuplesOffered} tuple(s), signature " +
                         (tariff.SignaturePresent
                              ? $"present, digests {(tariff.DigestOk ? "OK" : "FAILED")}, " +
                                $"ECDSA {(tariff.SignatureOk ? $"OK (grammar {tariff.SignatureGrammar})" : "FAILED or unverified")}"
                              : "absent") +
                         $"; chose tuple {tariff.ChosenTupleId}, profile {tariff.ProfileEntries} entr{(tariff.ProfileEntries == 1 ? "y" : "ies")}.",
                         "15118", "tariff");

            #endregion

            var json = new JObject(
                           new JProperty("outcome",           "completed"),
                           new JProperty("sessionId",         Convert.ToHexString(evcc.SessionId)),
                           new JProperty("sessionSetup",      evcc.SessionSetupCode.ToString()),
                           new JProperty("exchanges",         evcc.Exchanges),
                           new JProperty("bytesOnWire",       evcc.BytesOnWire),
                           new JProperty("authorization",     evcc.AuthorizationMode),
                           new JProperty("meteringReceipts",  evcc.MeteringReceiptsSent),
                           new JProperty("renegotiations",    evcc.Renegotiations),
                           new JProperty("paused",            Options.Pause),
                           new JProperty("battery",           BatteryJSON(evcc.Battery, evcc.BatteryStop)),
                           new JProperty("tariff",            evcc.Tariff is null
                                                                  ? null
                                                                  : new JObject(
                                                                        new JProperty("tuplesOffered",     evcc.Tariff.TuplesOffered),
                                                                        new JProperty("signaturePresent",  evcc.Tariff.SignaturePresent),
                                                                        new JProperty("digestOk",          evcc.Tariff.DigestOk),
                                                                        new JProperty("signatureOk",       evcc.Tariff.SignatureOk)
                                                                    ))
                       );

            // -2 binds nothing to the session and renegotiates the service on a
            // resume - both by design there, and both changed in -20. So the
            // handle carries the identification and the meter reading, and the
            // two -20 fields stay empty.
            return new SessionResult(
                       json,
                       new ResumableSession(evcc.SessionId, null, 0,
                                            (Options.Resume?.DeliveredWh ?? 0) + evcc.Meter.Energy)
                   );

        }

        #endregion

        #region (private static) RunIso20Async(Stream, Options, Log, CancellationToken)

        /// <summary>
        /// One ISO 15118-20 session.
        /// </summary>
        private static async Task<SessionResult> RunIso20Async(Stream             Stream,
                                                               SessionOptions     Options,
                                                               EventLog           Log,
                                                               CancellationToken  CancellationToken)
        {

            Evcc20Base evcc = (Options.Mode, Options.MCS) switch {
                                  (PowerMode.Dc, true )  => new Evcc20Mcs(Stream, TimeProvider.System, new TaskAsyncDelay(), MessageTimeout),
                                  (PowerMode.Dc, false)  => new Evcc20Dc (Stream, TimeProvider.System, new TaskAsyncDelay(), MessageTimeout),
                                  _                      => new Evcc20Ac (Stream, TimeProvider.System, new TaskAsyncDelay(), MessageTimeout)
                              };

            evcc.StopMode = Options.Pause
                                ? Iso20.ChargingSession.Pause
                                : Iso20.ChargingSession.Terminate;

            evcc.ResumeFrom(Options.Resume);

            evcc.Battery  = Options.Battery;

            // The one battery goal that is also a protocol field: -20 carries
            // it as seconds from the session's own time anchor, and a Dynamic
            // station schedules against it.
            if (Options.DepartureTime is { } departure)
                evcc.DepartureTime = departure;

            if (Options.ContractCertificate is not null)
                evcc.Pnc = VehicleCredentials.LoadContract(Options.ContractCertificate, Options.MORoots, Log);

            if (Options.OEMCertificate is not null)
                evcc.CertInstallRequest = VehicleCredentials.LoadOEM(Options.OEMCertificate, Options.OEMRoots, Log);

            if (Options.TariffCertificate is not null)
                evcc.TariffVerifyKey = VehicleCredentials.LoadTariffVerifyKey(Options.TariffCertificate, Log);

            await evcc.RunAsync(CancellationToken);

            #region What it added up to

            Log.Notice($"Session: {evcc.Exchanges} exchanges, {evcc.BytesOnWire} bytes on the wire (request side), " +
                       $"auth {evcc.AuthorizationMode}, session setup {evcc.SessionSetupCode}.",
                       "15118", "session");

            if (evcc.Battery is { } battery && evcc.BatteryStop is { } stop)
                Log.Notice($"Battery: {battery.Describe(stop)}", "15118", "session");

            var installedChainsTo = (String?) null;

            if (evcc.InstalledContractCertificate is { } installed)
            {

                Log.Notice("CertificateInstallation: a contract certificate was issued and its private key unwrapped - " +
                           "the ECDH/AES-GCM round trip closed.",
                           "15118", "pnc");

                // The one chain check that is about somebody else's word. The
                // rest of this vehicle's certificates were put there by its
                // operator; this one arrived over the wire from a station, and
                // the signature over the response says only that the CPS signed
                // what it sent - not that the contract belongs to a Mobility
                // Operator this vehicle has any reason to believe.
                //
                // Reported and not thrown: the session has already charged by
                // the time this runs, and a contract that was installed is a
                // fact whether or not it chains. What would be wrong is letting
                // it pass unremarked.
                if (Options.MORoots is null)
                    Log.Warning("CertificateInstallation: nothing vouches for the contract certificate the station " +
                                "issued - this vehicle holds no Mobility Operator root. Import one as \"moRoot\" to " +
                                "have the issued chain checked.",
                                "15118", "pnc");

                else
                {

                    var verdict = Options.MORoots.Validate(installed, evcc.InstalledContractSubCertificates);

                    if (verdict.Ok)
                    {
                        installedChainsTo = verdict.Anchor;
                        Log.Notice($"CertificateInstallation: the issued contract certificate chains to {verdict.Anchor}.",
                                   "15118", "pnc");
                    }
                    else
                        Log.Error($"CertificateInstallation: the issued contract certificate does NOT chain to any " +
                                  $"Mobility Operator root this vehicle holds - {verdict.Reason}.",
                                  "15118", "pnc");

                }

            }

            else if (Options.OEMCertificate is not null)
                Log.Warning("CertificateInstallation: not completed - the station either did not offer the service " +
                            "or refused the request.",
                            "15118", "pnc");

            if (evcc.Tariff is { } tariff)
                Log.Info($"Tariff: AbsolutePriceSchedule signature {(tariff.SignaturePresent ? "present" : "absent")}, " +
                         $"digest {(tariff.DigestOk ? "OK" : "FAILED")}, " +
                         $"ECDSA-P521/SHA-512 {(tariff.SignatureOk ? "OK" : "FAILED or unverified")}.",
                         "15118", "tariff");

            if (evcc.ResumeRefused)
                Log.Warning("Session: the station refused the resume and opened a new session - everything the paused " +
                            "session carried, authorization included, was dropped.",
                            "15118", "session");

            else if (evcc.ResumedStationVerified == true)
                Log.Notice("Session: the resumed session is confirmed to be with the same station (certificate binding).",
                           "15118", "session");

            #endregion

            var json = new JObject(
                           new JProperty("outcome",           "completed"),
                           new JProperty("sessionId",         Convert.ToHexString(evcc.SessionId)),
                           new JProperty("sessionSetup",      evcc.SessionSetupCode.ToString()),
                           new JProperty("exchanges",         evcc.Exchanges),
                           new JProperty("bytesOnWire",       evcc.BytesOnWire),
                           new JProperty("authorization",     evcc.AuthorizationMode),
                           new JProperty("paused",            Options.Pause),
                           new JProperty("resumeRefused",     evcc.ResumeRefused),
                           new JProperty("sameStation",       evcc.ResumedStationVerified),
                           new JProperty("contractInstalled", evcc.InstalledContractCertificate is not null),
                           new JProperty("contractChainsTo",  installedChainsTo),
                           new JProperty("battery",           BatteryJSON(evcc.Battery, evcc.BatteryStop)),
                           new JProperty("tariff",            evcc.Tariff is null
                                                                  ? null
                                                                  : new JObject(
                                                                        new JProperty("signaturePresent",  evcc.Tariff.SignaturePresent),
                                                                        new JProperty("digestOk",          evcc.Tariff.DigestOk),
                                                                        new JProperty("signatureOk",       evcc.Tariff.SignatureOk)
                                                                    ))
                       );

            return new SessionResult(json, evcc.PausedSession);

        }

        #endregion


        #region (private static) ConnectAsync(Options, Trust, Log, CancellationToken)

        /// <summary>
        /// The TCP connection the session runs on, with whatever TLS was asked for on top of it.
        /// </summary>
        private static async Task<Stream> ConnectAsync(SessionOptions      Options,
                                                       V2GChainValidator?  Trust,
                                                       EventLog            Log,
                                                       CancellationToken   CancellationToken)
        {

            switch (Options.TLS)
            {

                #region BouncyCastle: the ISO 15118-20-faithful profile

                case TlsStack.BouncyCastle:
                {

                    var bc = VehicleCredentials.BouncyCastleOptions(
                                 Options.VehicleCertificate,
                                 Options.PKIDirectory,
                                 Log
                             );

                    // Pinning and chaining are not alternatives and can both be
                    // on: one says "this exact station", the other "a station
                    // some V2G root vouches for".
                    if (Trust is not null)
                        bc = bc with {
                                 ValidatePeerChain = chain => Report("TLS station", Trust.Validate(chain[0], chain[1..]), Log)
                             };

                    return await TcpV2GClient.ConnectAsync(Options.Host, Options.Port, bc, CancellationToken);

                }

                #endregion

                #region .NET SslStream

                case TlsStack.Dotnet:
                {

                    if (Trust is null)
                        Log.Warning("TLS: accepting any station certificate - no trust roots are configured. " +
                                    "A handshake that succeeds here says this vehicle was authenticated, not the station.",
                                    "15118", "tls");

                    var (leaf, chain) = Credentials.LoadForTls(Options.VehicleCertificate, null,
                                                               "session.vehicleCertificate");

                    if (leaf is not null)
                        Log.Info($"TLS: presenting the Vehicle certificate {leaf.Subject} (+{chain?.Count ?? 0} intermediate(s)) for mutual TLS.",
                                 "15118", "tls");

                    var tls = new TlsOptions {

                                  ServerCertificateValidation = Trust is null

                                      ? (_, _, _, _) => true

                                      // The chain argument carries what the
                                      // station put on the wire beyond its
                                      // leaf; without it a station that sends
                                      // its Sub-CAs is judged as though it had
                                      // sent none.
                                      : (_, certificate, builtChain, _) => certificate is not null &&
                                            Report("TLS station",
                                                   Trust.Validate(new X509Certificate2(certificate),
                                                                  TrustRoots.PeerIntermediates(builtChain)),
                                                   Log),

                                  // Both, so that this interoperates with a
                                  // station in either mode - a -2 profile is
                                  // TLS 1.2 with P-256, and -20's is TLS 1.3.
                                  EnabledSslProtocols     = SslProtocols.Tls12 | SslProtocols.Tls13,
                                  ClientCertificate       = leaf,
                                  ClientCertificateChain  = chain

                              };

                    return await TcpV2GClient.ConnectAsync(Options.Host, Options.Port, tls, CancellationToken);

                }

                #endregion

                default:
                    return await TcpV2GClient.ConnectAsync(Options.Host, Options.Port, ct: CancellationToken);

            }

        }

        #endregion

        #region (private static) TransportOf(Options, Stream, Log)

        /// <summary>
        /// What to tell the handshake about the connection underneath it - and, where the answer would
        /// stop a run this vehicle makes on purpose, the line that says so instead.
        /// </summary>
        /// <remarks>
        /// [V2G20-1237] forbids offering ISO 15118-20 on plain TCP or on TLS 1.2 and below, and a good deal
        /// of what this vehicle is pointed at offers exactly that on purpose. So the transport is reported
        /// only when the rule would be satisfied anyway, and otherwise this says what it is about to do and
        /// leaves the transport unstated - the requirement is then not applied, deliberately and out loud.
        ///
        /// The BouncyCastle backend is TLS 1.3 by construction on both sides and hands back a stream
        /// <see cref="Iso20Transport.Of"/> deliberately does not guess about, so that case is named here
        /// rather than sniffed.
        /// </remarks>
        private static TransportSecurity TransportOf(SessionOptions  Options,
                                                     Stream          Stream,
                                                     EventLog        Log)
        {

            var actual = Options.TLS == TlsStack.BouncyCastle
                             ? TransportSecurity.Tls13
                             : Iso20Transport.Of(Stream);

            if (Options.Protocol != ProtocolVariant.Iso15118_20 && !Options.OfferBoth)
                return actual;

            if (Iso20Transport.MayCarryIso20(actual))
                return actual;

            Log.Warning($"SAP: offering ISO 15118-20 on {Iso20Transport.Describe(actual)} - [V2G20-1237] says a vehicle " +
                         "should not, and this one is doing it anyway because that is what the run is for. " +
                         "TLS 1.3 makes the offer conformant.",
                        "15118", "sap");

            return TransportSecurity.Unknown;

        }

        #endregion

        #region (private static) Report(What, Result, Log)

        /// <summary>
        /// Turns a chain verdict into the boolean a TLS callback needs, and says what it decided.
        /// </summary>
        /// <remarks>
        /// A refused handshake is otherwise a bare connection reset with the reason known only inside the
        /// callback - which is the shape of failure that costs an afternoon.
        /// </remarks>
        private static Boolean Report(String       What,
                                      ChainResult  Result,
                                      EventLog     Log)
        {

            if (Result.Ok)
                Log.Info($"{What}: the chain is valid, anchored at {Result.Anchor}.", "15118", "tls");
            else
                Log.Error($"{What}: the chain was REJECTED - {Result.Reason}.", "15118", "tls");

            return Result.Ok;

        }

        #endregion

        #region (private static) BatteryJSON(Battery, Stop)

        /// <summary>
        /// The pack as the page reads it, or null where this run had none.
        /// </summary>
        private static JObject? BatteryJSON(EvBattery?  Battery,
                                            ChargeStop? Stop)

            => Battery is null
                   ? null
                   : new JObject(
                         new JProperty("capacityKWh",           Math.Round(Battery.CapacityWh  / 1000, 2)),
                         new JProperty("startedAtPercent",      Math.Round(Battery.StartSoC,    1)),
                         new JProperty("stateOfChargePercent",  Math.Round(Battery.SoC,         1)),
                         new JProperty("deliveredKWh",          Math.Round(Battery.DeliveredWh / 1000, 3)),
                         new JProperty("iterations",            Battery.Iterations),
                         new JProperty("simulatedMinutes",      Math.Round(Battery.Elapsed.TotalMinutes, 0)),
                         new JProperty("stoppedBecause",        Stop?.ToString()),
                         new JProperty("minimumMissed",         Battery.MinimumSoCMissed),
                         new JProperty("describe",              Stop is null ? null : Battery.Describe(Stop.Value))
                     );

        #endregion

    }

}
