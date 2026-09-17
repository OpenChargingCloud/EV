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

namespace cloud.charging.open.EV.ISO15118
{

    /// <summary>
    /// What opens the four PKCS#12 files a vehicle may carry.
    /// </summary>
    /// <remarks>
    /// Its own type, and separate from <see cref="Configuration.SessionConfiguration"/>, so that the
    /// distinction is structural rather than remembered: the paths are part of what this vehicle is and
    /// are written down; the passwords are not, and there is no field on the configuration record that
    /// could carry one into the file by accident.
    ///
    /// Nothing here reaches the JSON API either, in or out. A password that could be read back from
    /// <c>/api/v1/configuration/session</c> would be a password anybody who may read the configuration
    /// holds, and reading the configuration is the one permission every role has.
    ///
    /// The environment rather than the command line, by default: a password given as a switch stands in
    /// the process list for every other user of the machine to see, which is the same reason the charging
    /// station reads its V2G certificate password from <c>CHARGINGSTATION_V2G_CERT_PASSWORD</c>.
    /// </remarks>
    /// <param name="Vehicle">For the Vehicle certificate - who this vehicle is.</param>
    /// <param name="Contract">For the contract certificate - who pays.</param>
    /// <param name="OEM">For the OEM provisioning certificate - what the vehicle was born with.</param>
    /// <param name="Tariff">For the certificate a station's signed tariff is checked against.</param>
    public sealed record CertificatePasswords(String?  Vehicle   = null,
                                              String?  Contract  = null,
                                              String?  OEM       = null,
                                              String?  Tariff    = null)
    {

        #region Data

        /// <summary>The environment variable holding the Vehicle certificate's password.</summary>
        public const String  VehicleVariable   = "EV_VEHICLE_CERT_PASSWORD";

        /// <summary>The environment variable holding the contract certificate's password.</summary>
        public const String  ContractVariable  = "EV_CONTRACT_CERT_PASSWORD";

        /// <summary>The environment variable holding the OEM provisioning certificate's password.</summary>
        public const String  OEMVariable       = "EV_OEM_CERT_PASSWORD";

        /// <summary>The environment variable holding the tariff certificate's password.</summary>
        public const String  TariffVariable    = "EV_TARIFF_CERT_PASSWORD";

        /// <summary>
        /// Nothing given at all, which is what a vehicle with unencrypted
        /// PKCS#12 files - or with none - runs on.
        /// </summary>
        public static readonly CertificatePasswords None = new ();

        #endregion


        #region (static) FromEnvironment()

        /// <summary>
        /// Whatever the environment holds, and null for each variable it does
        /// not.
        /// </summary>
        public static CertificatePasswords FromEnvironment()

            => new (Environment.GetEnvironmentVariable(VehicleVariable),
                    Environment.GetEnvironmentVariable(ContractVariable),
                    Environment.GetEnvironmentVariable(OEMVariable),
                    Environment.GetEnvironmentVariable(TariffVariable));

        #endregion

        #region Or(Other)

        /// <summary>
        /// These passwords, and for each one this record does not have, the
        /// other record's.
        /// </summary>
        /// <remarks>
        /// The precedence a command line expects: what was typed wins, and the
        /// environment fills the rest in. Field by field rather than
        /// record by record, so that naming one password on the command line
        /// does not discard the three that were in the environment.
        /// </remarks>
        public CertificatePasswords Or(CertificatePasswords Other)

            => new (Vehicle  ?? Other.Vehicle,
                    Contract ?? Other.Contract,
                    OEM      ?? Other.OEM,
                    Tariff   ?? Other.Tariff);

        #endregion

        #region (override) ToString()

        /// <summary>
        /// Which of them are set, and never what any of them is.
        /// </summary>
        /// <remarks>
        /// A record's generated ToString prints every field, and this one would
        /// have printed four passwords into whatever interpolated it. That is
        /// the whole reason this override exists.
        /// </remarks>
        public override String ToString()
        {

            var held = new[] {
                           Vehicle  is not null ? "vehicle"  : null,
                           Contract is not null ? "contract" : null,
                           OEM      is not null ? "OEM"      : null,
                           Tariff   is not null ? "tariff"   : null
                       }.Where(name => name is not null).ToArray();

            return held.Length == 0
                       ? "no certificate passwords"
                       : $"password(s) for: {String.Join(", ", held)}";

        }

        #endregion

    }

}
