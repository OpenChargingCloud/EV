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

using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.Session;
using cloud.charging.open.protocols.ISO15118.Simulation;
using cloud.charging.open.protocols.ISO15118.StateMachines;

#endregion

namespace cloud.charging.open.EV.ISO15118
{

    /// <summary>
    /// One session run, with everything already decided.
    /// </summary>
    /// <remarks>
    /// The difference between this and <see cref="Configuration.SessionConfiguration"/> is the difference
    /// between a setting and a run. The configuration is what the file says and what the web interface
    /// edits, where every field may be absent because absent means "no opinion". This is what one run
    /// actually does, where nothing may be absent because a socket cannot be opened towards an opinion.
    ///
    /// <see cref="EV"/> is what turns one into the other: it fills the gaps from its own defaults, builds
    /// the battery out of the vehicle section, and resolves the station - by name or over SDP - so that
    /// this record can require a host and a port.
    /// </remarks>
    public sealed record SessionOptions
    {

        #region Where to

        /// <summary>
        /// The station's address, as something a socket cannot misunderstand: an IPv6 literal here
        /// carries its zone as a number.
        /// </summary>
        public required String                Host                 { get; init; }

        /// <summary>The station's TCP port.</summary>
        public required Int32                 Port                 { get; init; }

        #endregion

        #region What to speak

        /// <summary>
        /// The protocol at the top of the offer. With <see cref="OfferBoth"/> it is a preference and the
        /// station decides; without it, a pin.
        /// </summary>
        public ProtocolVariant                Protocol             { get; init; } = ProtocolVariant.Iso15118_20;

        /// <summary>
        /// Whether both protocols go out in one handshake. What a modern vehicle does: offer what you
        /// speak and let the station choose, so that a -2-only station is met without a second connection.
        /// </summary>
        public Boolean                        OfferBoth            { get; init; } = true;

        /// <summary>
        /// AC or DC. Unlike the protocol this is not negotiated - the connector decides it, and a station
        /// told something else fails on a message set it did not expect.
        /// </summary>
        public PowerMode                      Mode                 { get; init; } = PowerMode.Dc;

        /// <summary>
        /// The DC message set under energy-transfer services 8/9, with a megawatt envelope. ISO 15118-20
        /// only, which is why it is a flag beside <see cref="Mode"/> rather than a third mode.
        /// </summary>
        public Boolean                        MCS                  { get; init; }

        /// <summary>Which TLS stack carries it, if any.</summary>
        public TlsStack                       TLS                  { get; init; } = TlsStack.None;

        #endregion

        #region What this vehicle holds up

        /// <summary>The development hierarchy a station minted, which this vehicle reads its chain out of.</summary>
        public String?                        PKIDirectory         { get; init; }

        /// <summary>The Vehicle certificate: who this vehicle is.</summary>
        public String?                        VehicleCertificate   { get; init; }

        /// <summary>The V2G root(s) a station's certificate must chain to; a file or a directory of them.</summary>
        public String?                        TrustRoots           { get; init; }

        /// <summary>The contract certificate: who pays.</summary>
        public String?                        ContractCertificate  { get; init; }

        /// <summary>The OEM provisioning certificate: what this vehicle was born with.</summary>
        public String?                        OEMCertificate       { get; init; }

        /// <summary>The public key a station's signed tariff is checked against.</summary>
        public String?                        TariffCertificate    { get; init; }

        /// <summary>What opens the four files above. Never written down, never on the API.</summary>
        public CertificatePasswords           Passwords            { get; init; } = CertificatePasswords.None;

        #endregion

        #region What it wants

        /// <summary>
        /// The pack, and every goal that ends the session.
        /// </summary>
        /// <remarks>
        /// Always present, unlike the EVCC of WWCP_ISO15118 where a battery is what the nine battery flags
        /// turn on. The difference is not an accident: that program's default is a three-iteration message
        /// sequence because every recorded interop run was taken at one, and this vehicle's battery is
        /// configuration it always has - there is no state in which it does not know how big its own pack
        /// is. A run that should be a message sequence rather than a charging session gets there by naming
        /// a short charging time.
        /// </remarks>
        public EvBattery?                     Battery              { get; init; }

        /// <summary>
        /// When this vehicle leaves, in seconds from the session's own time anchor - the one battery goal
        /// that is also a protocol field, and what a Dynamic ISO 15118-20 station schedules against.
        /// </summary>
        public UInt32?                        DepartureTime        { get; init; }

        /// <summary>ISO 15118-2: send PowerDelivery(Renegotiate) after the first cycle.</summary>
        public Boolean                        Renegotiate          { get; init; }

        #endregion

        #region How it ends

        /// <summary>
        /// Whether the session ends paused rather than terminated, so that it can be rejoined.
        /// </summary>
        public Boolean                        Pause                { get; init; }

        /// <summary>
        /// The paused session to rejoin, or null to open a new one.
        /// </summary>
        public ResumableSession?              Resume               { get; init; }

        #endregion


        #region (override) ToString()

        public override String ToString()

            => $"[{Host}]:{Port}, " +
               (OfferBoth
                    ? "offering ISO 15118-20 and -2"
                    : $"ISO 15118{V2GInterface.Name(Protocol)}") +
               $", {(MCS ? "MCS" : V2GInterface.Name(Mode))}" +
               $", {TLS switch { TlsStack.None => "plain TCP", TlsStack.Dotnet => "TLS (.NET)", _ => "TLS (BouncyCastle)" }}";

        #endregion

    }

}
