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

#endregion

namespace cloud.charging.open.EV.Configuration
{

    /// <summary>
    /// The "vehicle" section of the configuration file: what this vehicle is,
    /// and what its battery wants.
    /// </summary>
    /// <remarks>
    /// As in the DNS and NTS sections, null means "the file does not say":
    /// what is missing keeps whatever the vehicle was given at construction,
    /// and a vehicle given nothing keeps the system default.
    ///
    /// The battery figures are the ones that reach a station on the wire, and
    /// each of them lands in a different field in each of the four modes - see
    /// the EVCC README in WWCP_ISO15118. They are kept here as plain numbers
    /// rather than as protocol values precisely because of that: the same
    /// "9 kW" is an <c>EVMaxCurrent</c> in -2 AC and an <c>EVTargetCurrent</c>
    /// in -20 DC, and deciding which is the session's business and not the
    /// configuration's.
    /// </remarks>
    /// <param name="Name">What to call this vehicle in the log and on the page.</param>
    /// <param name="VIN">The vehicle identification number, where there is one.</param>
    /// <param name="BatteryCapacity_kWh">The usable capacity of the pack.</param>
    /// <param name="StateOfCharge_percent">How full it is at plug-in.</param>
    /// <param name="MaxChargingPower_kW">What the vehicle asks a station for.</param>
    /// <param name="TaperFrom_percent">Where it starts asking for less; 100 charges flat.</param>
    /// <param name="TargetStateOfCharge_percent">How full it wants to be when it leaves.</param>
    public sealed record VehicleConfiguration(String?   Name                         = null,
                                              String?   VIN                          = null,
                                              Double?   BatteryCapacity_kWh          = null,
                                              Double?   StateOfCharge_percent        = null,
                                              Double?   MaxChargingPower_kW          = null,
                                              Double?   TaperFrom_percent            = null,
                                              Double?   TargetStateOfCharge_percent  = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName             = "vehicle";

        /// <summary>
        /// What to call a vehicle nobody has named.
        /// </summary>
        public const String  DefaultName             = "EV";

        /// <summary>
        /// The pack of a vehicle that does not say how big its own is.
        /// </summary>
        /// <remarks>
        /// 60 kWh, which is what the EVCC of WWCP_ISO15118 assumes, so that a
        /// session run from here and one run from there ask a station for the
        /// same thing.
        /// </remarks>
        public const Double  DefaultCapacity_kWh     = 60;

        /// <summary>
        /// Where a pack starts when nobody says.
        /// </summary>
        public const Double  DefaultSoC_percent      = 30;

        /// <summary>
        /// Where a vehicle starts asking for less than the full figure, when
        /// nobody says. Above this the ask falls off linearly to nothing at
        /// 100 %, which is arithmetic and not a charging curve.
        /// </summary>
        public const Double  DefaultTaperFrom_percent = 80;

        /// <summary>
        /// The longest a name or a VIN may be written. A VIN is 17 characters
        /// by ISO 3779; the limit is wider because this one is a simulator and
        /// somebody will want to write "the red one" in it.
        /// </summary>
        public const Int32   MaxNameLength           = 80;

        /// <summary>
        /// The biggest pack this will accept as a figure somebody meant.
        /// </summary>
        public const Double  MaxCapacity_kWh         = 1000;

        /// <summary>
        /// The most a vehicle may ask a station for. Well past MCS, and still
        /// short of a figure that is a typing mistake.
        /// </summary>
        public const Double  MaxPower_kW             = 5000;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "vehicle" section, or the one sentence that says what is wrong
        /// with it.
        /// </summary>
        public static Boolean TryParse(JObject                                        JSON,
                                       [NotNullWhen(true)]  out VehicleConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?                Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadString(JSON, "name",        SectionName, MaxNameLength, out var name, out Error) ||
                !ConfigurationReader.TryReadString(JSON, "vin",         SectionName, MaxNameLength, out var vin,  out Error) ||
                !ConfigurationReader.TryReadNumber(JSON, "batteryCapacityKWh",       SectionName, 0.1, MaxCapacity_kWh, out var capacity,  out Error) ||
                !ConfigurationReader.TryReadNumber(JSON, "stateOfChargePercent",     SectionName, 0,   100,             out var soc,       out Error) ||
                !ConfigurationReader.TryReadNumber(JSON, "maxChargingPowerKW",       SectionName, 0.1, MaxPower_kW,     out var power,     out Error) ||
                !ConfigurationReader.TryReadNumber(JSON, "taperFromPercent",         SectionName, 0,   100,             out var taperFrom, out Error) ||
                !ConfigurationReader.TryReadNumber(JSON, "targetStateOfChargePercent", SectionName, 0, 100,             out var targetSoC, out Error))
            {
                return false;
            }

            // A vehicle that starts fuller than it wants to be is not a vehicle
            // with an odd configuration, it is a session that ends on its first
            // iteration - so it is refused here rather than discovered there.
            if (soc.HasValue && targetSoC.HasValue && soc.Value > targetSoC.Value)
            {
                Error = $"'{SectionName}.stateOfChargePercent' ({soc.Value} %) is above " +
                        $"'{SectionName}.targetStateOfChargePercent' ({targetSoC.Value} %), " +
                         "so there would be nothing to charge.";
                return false;
            }

            Configuration = new VehicleConfiguration(
                                name,
                                vin,
                                capacity,
                                soc,
                                power,
                                taperFrom,
                                targetSoC
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

            if (Name is not null)                      json.Add("name",                        Name);
            if (VIN  is not null)                      json.Add("vin",                         VIN);
            if (BatteryCapacity_kWh.HasValue)          json.Add("batteryCapacityKWh",          BatteryCapacity_kWh.        Value);
            if (StateOfCharge_percent.HasValue)        json.Add("stateOfChargePercent",        StateOfCharge_percent.      Value);
            if (MaxChargingPower_kW.HasValue)          json.Add("maxChargingPowerKW",          MaxChargingPower_kW.        Value);
            if (TaperFrom_percent.HasValue)            json.Add("taperFromPercent",            TaperFrom_percent.          Value);
            if (TargetStateOfCharge_percent.HasValue)  json.Add("targetStateOfChargePercent",  TargetStateOfCharge_percent.Value);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Name ?? DefaultName}, " +
               $"{StateOfCharge_percent ?? DefaultSoC_percent:F0} % of " +
               $"{BatteryCapacity_kWh ?? DefaultCapacity_kWh:F0} kWh";

        #endregion

    }

}
