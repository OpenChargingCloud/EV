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
    /// Everything this vehicle can be told in writing: one document with one
    /// section per thing that can be configured.
    /// </summary>
    /// <remarks>
    /// One file rather than one per subject, because these settings are read
    /// together, changed together and backed up together - and because the
    /// question "what is this vehicle configured as" should have one answer
    /// that fits on a screen instead of a directory to go through.
    ///
    /// Every section is optional and so is every field inside it. A section
    /// that is absent is not a section set to nothing: it means the file has no
    /// opinion, and whatever the vehicle was handed at construction stands. A
    /// vehicle handed nothing either falls back to the system default. So the
    /// order is: system default, then what the constructor was given, then what
    /// this file says - each one only where it actually speaks.
    /// </remarks>
    /// <param name="Vehicle">What this vehicle is, and what its battery wants.</param>
    /// <param name="V2G">The wire below the charging cable, from this side.</param>
    /// <param name="Session">What this vehicle does once it has found a station.</param>
    public sealed record EVConfiguration(VehicleConfiguration?       Vehicle       = null,
                                         V2GConfiguration?           V2G           = null,
                                         SessionConfiguration?       Session       = null)
    {

        #region Properties

        /// <summary>
        /// Whether this document says anything at all.
        /// </summary>
        public Boolean IsEmpty

            => Vehicle      is null &&
               V2G          is null &&
               Session      is null;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The whole document, or the one sentence that says what is wrong with it.
        /// </summary>
        /// <remarks>
        /// A section of the wrong kind is an error rather than a section
        /// skipped: <c>"dns": null</c> is a file that has nothing to say about
        /// DNS, but <c>"dns": "google"</c> is a file whose author believed they
        /// had configured something.
        ///
        /// Sections this vehicle does not know are passed over without a word.
        /// A file written by a newer vehicle should still start an older one,
        /// and the file keeps them - see
        /// <see cref="WWCPConfigFile.TryReplaceSection"/>.
        /// </remarks>
        public static Boolean TryParse(JObject                                   JSON,
                                       [NotNullWhen(true)]  out EVConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?           Error)
        {

            Configuration  = null;
            Error          = null;

            #region Vehicle

            VehicleConfiguration? vehicle = null;

            if (JSON[VehicleConfiguration.SectionName] is JToken vehicleToken && vehicleToken.Type != JTokenType.Null)
            {

                if (vehicleToken is not JObject vehicleJSON)
                {
                    Error = $"'{VehicleConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!VehicleConfiguration.TryParse(vehicleJSON, out vehicle, out Error))
                    return false;

            }

            #endregion

            #region V2G

            V2GConfiguration? v2g = null;

            if (JSON[V2GConfiguration.SectionName] is JToken v2gToken && v2gToken.Type != JTokenType.Null)
            {

                if (v2gToken is not JObject v2gJSON)
                {
                    Error = $"'{V2GConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!V2GConfiguration.TryParse(v2gJSON, out v2g, out Error))
                    return false;

            }

            #endregion

            #region Session

            SessionConfiguration? session = null;

            if (JSON[SessionConfiguration.SectionName] is JToken sessionToken && sessionToken.Type != JTokenType.Null)
            {

                if (sessionToken is not JObject sessionJSON)
                {
                    Error = $"'{SessionConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!SessionConfiguration.TryParse(sessionJSON, out session, out Error))
                    return false;

            }

            #endregion

            Configuration = new EVConfiguration(
                                vehicle,
                                v2g,
                                session
                            );

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The document as it is written to the file.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Vehicle is not null)
                json.Add(VehicleConfiguration.SectionName,  Vehicle.ToJSON());

            if (V2G     is not null)
                json.Add(V2GConfiguration.    SectionName,  V2G.    ToJSON());

            if (Session is not null)
                json.Add(SessionConfiguration.SectionName,  Session.ToJSON());

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => IsEmpty
                   ? "nothing configured"
                   : String.Join(", ",
                         new[] {
                             Vehicle?.     ToString(),
                             V2G?.         ToString(),
                             Session?.     ToString()
                         }.Where(section => section is not null));

        #endregion

    }

}
