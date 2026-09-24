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

using cloud.charging.open.protocols.ISO15118.SDP.Messages;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.EV.Configuration
{

    /// <summary>
    /// The "v2g" section of the configuration file: the wire below the
    /// charging cable, from this vehicle's side.
    /// </summary>
    /// <remarks>
    /// These are the settings of the SECC Discovery Protocol client and of the
    /// interface it broadcasts on. They map one to one onto
    /// <c>EVCC_SDPClientOptions</c>, and are kept here as a section of their
    /// own so that the page can change them between one discovery and the next
    /// without restarting the vehicle - which is the whole point of having a
    /// web interface on a thing that spends its time being pointed at
    /// somebody else's station.
    ///
    /// Nothing here turns discovery on. A vehicle does not multicast to
    /// <c>ff02::1</c> because a binary started; it does it when somebody says
    /// so, from the page or from the command line.
    /// </remarks>
    /// <param name="InterfaceName">The interface the station is on - the powerline modem. Null takes the first candidate with an IPv6 link-local address.</param>
    /// <param name="NoInterface">Whether the document said so in as many words: <c>"interface": null</c>, which is how a setting is taken back rather than left alone.</param>
    /// <param name="RequestedSecurity">Whether the vehicle asks for a TLS endpoint or a plain one.</param>
    /// <param name="PerAttemptTimeout">How long one attempt waits for an answer before the next request goes out.</param>
    /// <param name="MaxRetries">How many attempts at most.</param>
    /// <param name="TotalDeadline">How long the whole discovery may take, however many attempts that is.</param>
    /// <param name="RejectNoTLSResponses">Whether a station answering "no TLS" to a request for TLS is refused.</param>
    /// <param name="RequireLinkLocalSECCAddress">Whether an answer naming an address that is not link-local is refused.</param>
    /// <param name="MulticastLoopback">Whether this vehicle also hears its own request - which is what a station and a vehicle on one machine need.</param>
    public sealed record V2GConfiguration(String?        InterfaceName                = null,
                                          Boolean        NoInterface                  = false,
                                          SDP_Security?  RequestedSecurity            = null,
                                          TimeSpan?      PerAttemptTimeout            = null,
                                          Int32?         MaxRetries                   = null,
                                          TimeSpan?      TotalDeadline                = null,
                                          Boolean?       RejectNoTLSResponses         = null,
                                          Boolean?       RequireLinkLocalSECCAddress  = null,
                                          Boolean?       MulticastLoopback            = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName             = "v2g";

        /// <summary>
        /// The longest an interface name may be written.
        /// </summary>
        public const Int32   MaxInterfaceNameLength  = 128;

        /// <summary>
        /// The most attempts one discovery may make. [V2G2-159] asks for the
        /// request to be repeated every 250 ms for up to 60 s, which is 240;
        /// the ceiling is above that so that a longer per-attempt timeout does
        /// not run into it.
        /// </summary>
        public const Int32   MaxRetriesCeiling       = 1000;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "v2g" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                    JSON,
                                       [NotNullWhen(true)]  out V2GConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadString (JSON, "interface",         SectionName, MaxInterfaceNameLength, out var interfaceName, out Error) ||
                !ConfigurationReader.TryReadString (JSON, "requestedSecurity", SectionName, 16,                     out var security,      out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "perAttemptTimeoutSeconds", SectionName, 0.01, 60,        out var perAttempt,    out Error) ||
                !ConfigurationReader.TryReadNumber (JSON, "maxRetries",        SectionName, 1, MaxRetriesCeiling,   out var maxRetries,    out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "totalDeadlineSeconds",     SectionName, 0.1, 600,        out var deadline,      out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "rejectNoTLSResponses",        SectionName,               out var rejectNoTLS,   out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "requireLinkLocalSECCAddress", SectionName,               out var requireLL,     out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "multicastLoopback",           SectionName,               out var loopback,      out Error))
            {
                return false;
            }

            // "interface": null is not the same as no "interface" at all. The
            // first says "whichever one comes first", which is a setting; the
            // second says nothing, and leaves whatever was chosen before in
            // place. Without the difference there is no way back from having
            // named one - see ApplyV2GConfiguration.
            var noInterface = JSON.TryGetValue("interface", out var interfaceToken) &&
                              interfaceToken.Type == JTokenType.Null;

            SDP_Security? requestedSecurity = null;

            if (security is not null)
            {

                requestedSecurity = security.ToLowerInvariant() switch {
                                        "tls"    => SDP_Security.TLS,
                                        "notls"  => SDP_Security.NoTLS,
                                        _        => null
                                    };

                if (requestedSecurity is null)
                {
                    Error = $"'{SectionName}.requestedSecurity' must be \"tls\" or \"noTls\".";
                    return false;
                }

            }

            Configuration = new V2GConfiguration(
                                interfaceName,
                                noInterface,
                                requestedSecurity,
                                perAttempt,
                                maxRetries.HasValue ? (Int32) maxRetries.Value : null,
                                deadline,
                                rejectNoTLS,
                                requireLL,
                                loopback
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

            // Written as an explicit null rather than left out, so that the
            // file keeps saying "whichever one comes first" instead of falling
            // back to whatever the next start was handed.
            if (InterfaceName is not null)             json.Add("interface",                    InterfaceName);
            else if (NoInterface)                      json.Add("interface",                    JValue.CreateNull());
            if (RequestedSecurity.HasValue)            json.Add("requestedSecurity",            NameOf(RequestedSecurity.Value));
            if (PerAttemptTimeout.HasValue)            json.Add("perAttemptTimeoutSeconds",     PerAttemptTimeout.Value.TotalSeconds);
            if (MaxRetries.HasValue)                   json.Add("maxRetries",                   MaxRetries.Value);
            if (TotalDeadline.HasValue)                json.Add("totalDeadlineSeconds",         TotalDeadline.Value.TotalSeconds);
            if (RejectNoTLSResponses.HasValue)         json.Add("rejectNoTLSResponses",         RejectNoTLSResponses.Value);
            if (RequireLinkLocalSECCAddress.HasValue)  json.Add("requireLinkLocalSECCAddress",  RequireLinkLocalSECCAddress.Value);
            if (MulticastLoopback.HasValue)            json.Add("multicastLoopback",            MulticastLoopback.Value);

            return json;

        }

        #endregion

        #region (static) NameOf(Security)

        /// <summary>
        /// How the configuration file and the web interface write it.
        /// </summary>
        public static String NameOf(SDP_Security Security)

            => Security == SDP_Security.TLS
                   ? "tls"
                   : "noTls";

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{InterfaceName ?? "first candidate interface"}, " +
               $"asking for {NameOf(RequestedSecurity ?? SDP_Security.TLS)}";

        #endregion

    }

}
