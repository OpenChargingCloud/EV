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

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.Transport;

#endregion

namespace cloud.charging.open.EV.Configuration
{

    /// <summary>
    /// The "session" section of the configuration file: what this vehicle does
    /// once it has found a station.
    /// </summary>
    /// <remarks>
    /// As in every other section, null means "the file does not say": what is
    /// missing keeps whatever the vehicle was given at construction, and a
    /// vehicle given nothing keeps the system default.
    ///
    /// <b>Certificates are named here and kept elsewhere.</b> Each of the four
    /// below is a handle into the certificate store - see
    /// <see cref="CertificatesConfiguration"/> and
    /// <see cref="Certificates.CertificateStore"/> - and never a path and never
    /// a password. Which certificate one run uses is a choice about that run and
    /// belongs here; what certificates this vehicle has at all is a property of
    /// the machine and belongs there.
    ///
    /// The trust anchors are not here at all, and deliberately so. A session
    /// does not pick which roots it believes: that is what the vehicle believes,
    /// it is every usable root of the right kind in the store, and a session
    /// that could narrow it would be a session that could widen it.
    ///
    /// What belongs in the other two sections and not here: the battery is
    /// <see cref="VehicleConfiguration"/>, because it is what the vehicle *is*
    /// rather than what one run does with it; the SDP client is
    /// <see cref="V2GConfiguration"/>, because finding a station is a question
    /// that can be asked without charging afterwards.
    /// </remarks>
    /// <param name="Connect">The station to drive to, as <c>host:port</c>; without one the vehicle looks for a station over SDP first.</param>
    /// <param name="Protocol">The protocol at the top of the offer. With <paramref name="OfferBoth"/> it is a preference; without it, a pin.</param>
    /// <param name="OfferBoth">Whether both protocols go out in one handshake and the station picks. What a modern vehicle does.</param>
    /// <param name="Mode">AC or DC - not negotiated, so the station has to have been told the same thing.</param>
    /// <param name="MCS">The DC message set under energy-transfer services 8/9, with a megawatt envelope. ISO 15118-20 only.</param>
    /// <param name="TLS">Which TLS stack a session runs on, if any.</param>
    /// <param name="PKIDirectory">The development hierarchy a station minted, which this vehicle reads its own chain out of.</param>
    /// <param name="VehicleCertificate">The Vehicle certificate this run presents, as a handle into the store: who this vehicle is.</param>
    /// <param name="ContractCertificate">The contract certificate this run pays with, as a handle into the store.</param>
    /// <param name="OEMCertificate">The OEM provisioning certificate, as a handle into the store: what the vehicle was born with.</param>
    /// <param name="TariffCertificate">The public key a station's signed tariff is checked against, as a handle into the store.</param>
    /// <param name="TargetEnergy_kWh">Charge until this much has been delivered.</param>
    /// <param name="MaxChargingTime">Stop after this much simulated time.</param>
    /// <param name="DepartureIn">When the vehicle leaves - on the wire in ISO 15118-20, and the end of the session in both.</param>
    /// <param name="MinimumStateOfCharge_percent">What the driver needs by then. A floor, not a goal.</param>
    /// <param name="Renegotiate">ISO 15118-2: send PowerDelivery(Renegotiate) after the first cycle.</param>
    /// <param name="SLACPeer">The station's SLAC endpoint over a simulated medium, as <c>host:port</c>; without one no pairing stage runs.</param>
    /// <param name="Cleared">
    /// The fields the document set to an explicit null, which is how a setting is taken back rather than
    /// left alone. Not something the file says about the vehicle - it is something a request says about
    /// the file - so it is a set of names rather than a field per setting.
    /// </param>
    public sealed record SessionConfiguration(String?           Connect                       = null,
                                              ProtocolVariant?  Protocol                      = null,
                                              Boolean?          OfferBoth                     = null,
                                              PowerMode?        Mode                          = null,
                                              Boolean?          MCS                           = null,
                                              TlsStack?         TLS                           = null,
                                              String?           PKIDirectory                  = null,
                                              String?           VehicleCertificate            = null,
                                              String?           ContractCertificate           = null,
                                              String?           OEMCertificate                = null,
                                              String?           TariffCertificate             = null,
                                              Double?           TargetEnergy_kWh              = null,
                                              TimeSpan?         MaxChargingTime               = null,
                                              TimeSpan?         DepartureIn                   = null,
                                              Double?           MinimumStateOfCharge_percent  = null,
                                              Boolean?          Renegotiate                   = null,
                                              String?           SLACPeer                      = null,
                                              IReadOnlySet<String>?
                                                                Cleared                       = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName             = "session";

        /// <summary>
        /// The longest a path may be written.
        /// </summary>
        public const Int32   MaxPathLength           = 4096;

        /// <summary>
        /// The longest a handle into the certificate store may be written.
        /// </summary>
        /// <remarks>
        /// A handle is sixteen hexadecimal digits; the room above that is so
        /// that somebody who pasted a whole SHA-256 fingerprint is told what is
        /// wrong with it rather than that it is too long.
        /// </remarks>
        public const Int32   MaxHandleLength         = 64;

        /// <summary>
        /// The fields of this section that name a certificate in the store.
        /// </summary>
        public static readonly IReadOnlyList<String>  CertificateFields = [
            "vehicleCertificate", "contractCertificate", "oemCertificate", "tariffCertificate"
        ];

        /// <summary>
        /// Every field of this section that may be taken back with an explicit
        /// null.
        /// </summary>
        /// <remarks>
        /// All of them, and that is not laziness: every setting here is one a
        /// bench is configured with and a run against something else has to be
        /// able to drop. A vehicle pointed at <c>[::1]:15118</c> for an
        /// afternoon needs a way back to finding a station on the link, and
        /// leaving a field out cannot be that way - leaving it out is what
        /// "change nothing" means everywhere else in this file.
        /// </remarks>
        public static readonly IReadOnlySet<String>  Clearable = new HashSet<String> {
            "connect", "protocol", "mode", "tls", "pkiDirectory", "vehicleCertificate",
            "contractCertificate", "oemCertificate", "tariffCertificate", "slacPeer",
            "targetEnergyKWh", "maxChargingTimeSeconds", "departureInSeconds",
            "minimumStateOfChargePercent", "renegotiate"
        };

        /// <summary>
        /// The longest an endpoint may be written. Generous, because a
        /// bracketed IPv6 literal with a zone is already 50 characters.
        /// </summary>
        public const Int32   MaxEndpointLength       = 256;

        /// <summary>
        /// The most energy one run may be asked to deliver, in kWh. Above a
        /// megawatt-hour it is a typing mistake rather than an unusual truck.
        /// </summary>
        public const Double  MaxTargetEnergy_kWh     = 1000;

        /// <summary>
        /// The longest a run may be allowed to take, in seconds. One iteration
        /// of the charge loop is one simulated minute, so a day is already
        /// 1440 exchanges.
        /// </summary>
        public const Double  MaxChargingTimeSeconds  = 86400;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "session" section, or the one sentence that says what is wrong
        /// with it.
        /// </summary>
        public static Boolean TryParse(JObject                                        JSON,
                                       [NotNullWhen(true)]  out SessionConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?                Error)
        {

            Configuration  = null;
            Error          = null;

            // An explicit null is not the same as a field left out. The first
            // says "back to the default", which is a setting; the second says
            // nothing, and leaves whatever was chosen before in place. Without
            // the difference there is no way back from having named a station.
            var cleared = new HashSet<String>(
                              Clearable.Where(field => JSON.TryGetValue(field, out var token) &&
                                                       token.Type == JTokenType.Null)
                          );

            #region Settings that used to be paths

            // Refused by name rather than ignored. A field this section no
            // longer knows would otherwise be passed over in silence - see
            // EVConfiguration.TryParse - and somebody whose configuration was
            // written before the store existed would get a vehicle that trusts
            // no station at all and says nothing about why.

            if (JSON.TryGetValue("trustRoots", out var staleRoots) && staleRoots.Type != JTokenType.Null)
            {
                Error = $"'{SectionName}.trustRoots' is no longer a setting. Trust anchors live in the " +
                         "certificate store now: import them as v2gRoot, moRoot or oemRoot, and every " +
                         "usable one of each kind is believed.";
                return false;
            }

            foreach (var field in CertificateFields)
            {

                if (JSON.TryGetValue(field, out var token) &&
                    token.Type == JTokenType.String &&
                    token.Value<String>()?.Trim() is { Length: > 0 } written &&
                    LooksLikeAPath(written))
                {
                    Error = $"'{SectionName}.{field}' names a certificate in the store and not a file, and " +
                            $"'{written}' is a path. Import that file into the store and name what it " +
                             "became.";
                    return false;
                }

            }

            #endregion

            if (!ConfigurationReader.TryReadString (JSON, "connect",             SectionName, MaxEndpointLength, out var connect,      out Error) ||
                !ConfigurationReader.TryReadString (JSON, "protocol",            SectionName, 8,                 out var protocolText, out Error) ||
                !ConfigurationReader.TryReadString (JSON, "mode",                SectionName, 8,                 out var modeText,     out Error) ||
                !ConfigurationReader.TryReadString (JSON, "tls",                 SectionName, 16,                out var tlsText,      out Error) ||
                !ConfigurationReader.TryReadString (JSON, "pkiDirectory",        SectionName, MaxPathLength,     out var pkiDirectory, out Error) ||
                !ConfigurationReader.TryReadString (JSON, "vehicleCertificate",  SectionName, MaxHandleLength,   out var vehicleCert,  out Error) ||
                !ConfigurationReader.TryReadString (JSON, "contractCertificate", SectionName, MaxHandleLength,   out var contractCert, out Error) ||
                !ConfigurationReader.TryReadString (JSON, "oemCertificate",      SectionName, MaxHandleLength,   out var oemCert,      out Error) ||
                !ConfigurationReader.TryReadString (JSON, "tariffCertificate",   SectionName, MaxHandleLength,   out var tariffCert,   out Error) ||
                !ConfigurationReader.TryReadString (JSON, "slacPeer",            SectionName, MaxEndpointLength, out var slacPeer,     out Error) ||
                !ConfigurationReader.TryReadNumber (JSON, "targetEnergyKWh",             SectionName, 0.001, MaxTargetEnergy_kWh,    out var targetEnergy, out Error) ||
                !ConfigurationReader.TryReadNumber (JSON, "minimumStateOfChargePercent", SectionName, 0,     100,                    out var minimumSoC,   out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "maxChargingTimeSeconds",      SectionName, 1,     MaxChargingTimeSeconds, out var maxTime,      out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "departureInSeconds",          SectionName, 1,     MaxChargingTimeSeconds, out var departure,    out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "renegotiate",         SectionName,                    out var renegotiate,  out Error))
            {
                return false;
            }

            #region What to offer

            // Two fields for one word, the way EvccOptions carries it: the
            // protocol at the top of the offer, and whether the other one goes
            // out beside it. "both" is not a third protocol - it is -20 at
            // priority 1 with -2 behind it, and the station picks.
            //
            // Both stay null when the file says nothing, which is what keeps
            // "the file has no opinion" apart from "the file says both".
            ProtocolVariant?  protocol   = null;
            Boolean?          offerBoth  = null;

            if (protocolText is not null)
            {

                (protocol, offerBoth) = protocolText.ToLowerInvariant() switch {
                                            "2"     => (ProtocolVariant.Iso15118_2,  false),
                                            "20"    => (ProtocolVariant.Iso15118_20, false),
                                            "both"  => (ProtocolVariant.Iso15118_20, true),
                                            _       => ((ProtocolVariant?) null, (Boolean?) null)
                                        };

                if (protocol is null)
                {
                    Error = $"'{SectionName}.protocol' must be \"2\", \"20\" or \"both\".";
                    return false;
                }

            }

            PowerMode?  mode  = null;
            Boolean?    mcs   = null;

            if (modeText is not null)
            {

                (mode, mcs) = modeText.ToLowerInvariant() switch {
                                  "ac"   => (PowerMode.Ac, false),
                                  "dc"   => (PowerMode.Dc, false),
                                  "mcs"  => (PowerMode.Dc, true),
                                  _      => ((PowerMode?) null, (Boolean?) null)
                              };

                if (mode is null)
                {
                    Error = $"'{SectionName}.mode' must be \"ac\", \"dc\" or \"mcs\".";
                    return false;
                }

            }

            TlsStack? tls = null;

            if (tlsText is not null)
            {

                tls = tlsText.ToLowerInvariant() switch {
                          "none"                  => TlsStack.None,
                          "dotnet"                => TlsStack.Dotnet,
                          "bc" or "bouncycastle"  => TlsStack.BouncyCastle,
                          _                       => null
                      };

                if (tls is null)
                {
                    Error = $"'{SectionName}.tls' must be \"none\", \"dotnet\" or \"bc\".";
                    return false;
                }

            }

            #endregion

            #region Endpoints, refused here rather than at the socket

            // V2GEndpoint knows what an IPv6 literal with a zone is, and what
            // the framework's own parsers quietly do to one. Asking it here
            // turns a mistyped address into a refused save rather than into a
            // session that fails minutes later at a socket, pointing at the
            // station.
            foreach (var (field, value) in new[] { ("connect", connect), ("slacPeer", slacPeer) })
            {

                if (value is null)
                    continue;

                try
                {
                    V2GEndpoint.Parse(value, $"'{SectionName}.{field}'");
                }
                catch (ArgumentException e)
                {
                    Error = e.Message;
                    return false;
                }

            }

            #endregion

            #region MCS is an ISO 15118-20 session and nothing else

            // Energy-transfer services 8/9 exist in no other catalogue, so
            // pinning -2 and asking for MCS is a request that cannot be met.
            // Refused rather than quietly running plain DC: a session that
            // silently degrades is the one failure an MCS run must not
            // produce. "both" is fine - the handshake can still settle on -20.
            if (mcs == true && protocol == ProtocolVariant.Iso15118_2 && offerBoth != true)
            {
                Error = $"'{SectionName}.mode' \"mcs\" is an ISO 15118-20 session, and '{SectionName}.protocol' is pinned to \"2\".";
                return false;
            }

            #endregion

            Configuration = new SessionConfiguration(
                                connect,
                                protocol,
                                offerBoth,
                                mode,
                                mcs,
                                tls,
                                pkiDirectory,
                                vehicleCert,
                                contractCert,
                                oemCert,
                                tariffCert,
                                targetEnergy,
                                maxTime,
                                departure,
                                minimumSoC,
                                renegotiate,
                                slacPeer,
                                cleared
                            );

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this vehicle was not
        /// told about is not written.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Connect             is not null)  json.Add("connect",             Connect);
            if (ProtocolWritten     is not null)  json.Add("protocol",            ProtocolWritten);
            if (ModeWritten         is not null)  json.Add("mode",                ModeWritten);
            if (TLSWritten          is not null)  json.Add("tls",                 TLSWritten);
            if (PKIDirectory        is not null)  json.Add("pkiDirectory",        PKIDirectory);
            if (VehicleCertificate  is not null)  json.Add("vehicleCertificate",  VehicleCertificate);
            if (ContractCertificate is not null)  json.Add("contractCertificate", ContractCertificate);
            if (OEMCertificate      is not null)  json.Add("oemCertificate",      OEMCertificate);
            if (TariffCertificate   is not null)  json.Add("tariffCertificate",   TariffCertificate);
            if (SLACPeer            is not null)  json.Add("slacPeer",            SLACPeer);

            if (TargetEnergy_kWh.HasValue)             json.Add("targetEnergyKWh",              TargetEnergy_kWh.Value);
            if (MaxChargingTime.HasValue)              json.Add("maxChargingTimeSeconds",       MaxChargingTime. Value.TotalSeconds);
            if (DepartureIn.HasValue)                  json.Add("departureInSeconds",           DepartureIn.     Value.TotalSeconds);
            if (MinimumStateOfCharge_percent.HasValue) json.Add("minimumStateOfChargePercent",  MinimumStateOfCharge_percent.Value);
            if (Renegotiate.HasValue)                  json.Add("renegotiate",                  Renegotiate.Value);

            // Written as explicit nulls rather than left out, so that the file
            // keeps saying "back to the default" instead of falling silent and
            // letting the next start decide.
            foreach (var field in Cleared ?? (IReadOnlySet<String>) new HashSet<String>())
                if (!json.ContainsKey(field))
                    json.Add(field, JValue.CreateNull());

            return json;

        }

        #endregion

        #region Properties

        /// <summary>
        /// How the protocol choice is written in the file and on the page, or
        /// null where there is nothing to write.
        /// </summary>
        public String? ProtocolWritten

            => OfferBoth == true
                   ? "both"
                   : Protocol switch {
                         ProtocolVariant.Iso15118_2   => "2",
                         ProtocolVariant.Iso15118_20  => "20",
                         _                            => null
                     };

        /// <summary>
        /// How the power mode is written. MCS is not a third mode on the wire
        /// - it is DC under energy-transfer services 8/9 - but it is a third
        /// thing to choose, so it is a third word here.
        /// </summary>
        public String? ModeWritten

            => MCS == true
                   ? "mcs"
                   : Mode switch {
                         PowerMode.Ac  => "ac",
                         PowerMode.Dc  => "dc",
                         _             => null
                     };

        /// <summary>
        /// How the TLS stack is written.
        /// </summary>
        public String? TLSWritten

            => TLS switch {
                   TlsStack.None          => "none",
                   TlsStack.Dotnet        => "dotnet",
                   TlsStack.BouncyCastle  => "bc",
                   _                      => null
               };

        #endregion

        #region (private static) LooksLikeAPath(Written)

        /// <summary>
        /// Whether what somebody wrote is a file name rather than a handle into
        /// the store.
        /// </summary>
        /// <remarks>
        /// Only ever used to give a better sentence back. A handle that is not
        /// in the store is refused by the store either way, so this does not
        /// decide whether something is accepted - it decides whether somebody
        /// is told "there is no such certificate" or "that is a path, and paths
        /// are not how this works any more".
        /// </remarks>
        private static Boolean LooksLikeAPath(String Written)

            => Written.Contains('/')  ||
               Written.Contains('\\') ||
               Written.Contains('.')  ||
               (Written.Length > 1 && Written[1] == ':');

        #endregion

        #region (override) ToString()

        public override String ToString()

            => String.Join(", ",
                   new[] {
                       Connect ?? "a station found over SDP",
                       ProtocolWritten is not null ? $"ISO 15118-{ProtocolWritten}" : null,
                       ModeWritten?.ToUpperInvariant(),
                       TLSWritten is not null ? $"TLS {TLSWritten}" : null
                   }.Where(part => part is not null));

        #endregion

    }

}
