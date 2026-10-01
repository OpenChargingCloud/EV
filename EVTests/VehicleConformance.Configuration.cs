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
using System.Globalization;

using Newtonsoft.Json.Linq;
using NUnit.Framework;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// The vehicle's own sections of the configuration file - the vehicle,
    /// its link and its session - answered as the node's sections are.
    /// </summary>
    public partial class VehicleConformance
    {

        #region AChangeOfItsOwnTheFileCannotTakeIsAServerError(Section, Cannot)

        /// <summary>
        /// A change of the vehicle, of its link or of its session that is fine
        /// in itself, and that the configuration file cannot be read or written
        /// with, is answered 500 with why - and changes nothing, as a change of
        /// the name resolution or of the time source is. It was a 400, as if
        /// something had been wrong with the change.
        /// </summary>
        [TestCase("vehicle", "written")]
        [TestCase("vehicle", "read")]
        [TestCase("v2g",     "written")]
        [TestCase("v2g",     "read")]
        [TestCase("session", "written")]
        [TestCase("session", "read")]
        public async Task AChangeOfItsOwnTheFileCannotTakeIsAServerError(String Section, String Cannot)
        {

            using var http  = await SignedIn();

            var path        = $"api/v1/configuration/{Section}";
            var before      = await GetJSON(http, path);

            // Where the file's next version is written first is a directory -
            // or the file is no longer JSON, as after an edit by hand that went
            // wrong.
            if (Cannot == "written")
                System.IO.Directory.CreateDirectory(Node.ConfigFile.Path + ".tmp");

            else
                File.WriteAllText(Node.ConfigFile.Path, $"{{ \"{Section}\": ");

            var change      = Section switch {
                                  "vehicle"  => JSONBody(new JProperty("name",               $"Not {before.Value<String>("name")}")),
                                  "v2g"      => JSONBody(new JProperty("multicastLoopback",  !(before["settings"]?.Value<Boolean>("multicastLoopback") ?? false))),
                                  _          => JSONBody(new JProperty("renegotiate",        !(before["settings"]?.Value<Boolean>("renegotiate")       ?? false)))
                              };

            // Where the page shows what the change would change.
            var shown       = Section == "vehicle" ? "name" : "settings";

            var response    = await http.PutAsync(path, change);
            var said        = JObject.Parse(await response.Content.ReadAsStringAsync());
            var after       = await GetJSON(http, path);

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,          Is.EqualTo(HttpStatusCode.InternalServerError), said.ToString());
                Assert.That(said.Value<String>("error"),  Does.StartWith($"'{Node.ConfigFile.Path}' could not be {Cannot}: "));
                Assert.That(after[shown]?.ToString(),     Is.EqualTo(before[shown]?.ToString()), "what the page shows as saved");
            });

        }

        #endregion

        #region WhatChangedIsLoggedWithAPointWhateverTheCulture()

        /// <summary>
        /// What a change of the vehicle or of its session is logged with says
        /// its numbers with a decimal point, as the node says its own, whatever
        /// the culture of the machine it runs on: on a German Windows it was
        /// "battery = 60,5 kWh", where --battery 60.5 is read with a point.
        /// </summary>
        [Test]
        public void WhatChangedIsLoggedWithAPointWhateverTheCulture()
        {

            var vehicle  = (EV) Node;
            var culture  = CultureInfo.CurrentCulture;

            try
            {

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

                Assert.That(vehicle.TryUpdateVehicleConfiguration(new JObject {
                                                                      ["batteryCapacityKWh"]  = 60.5,
                                                                      ["maxChargingPowerKW"]  = 22.5
                                                                  },
                                                                  out var vehicleRefused),
                            Is.True, vehicleRefused);

                Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                      ["targetEnergyKWh"]     = 12.5
                                                                  },
                                                                  out var sessionRefused),
                            Is.True, sessionRefused);

            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }

            var logged = vehicle.Log.Recent(50).Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(logged, Has.Some.Contains("battery = 60.5 kWh"),       String.Join(Environment.NewLine, logged));
                Assert.That(logged, Has.Some.Contains("charging power = 22.5 kW"), String.Join(Environment.NewLine, logged));
                Assert.That(logged, Has.Some.Contains("target energy = 12.5 kWh"), String.Join(Environment.NewLine, logged));
            });

        }

        #endregion

        #region ARefusalOfItsOwnIsWhatItWasWhileTheFileCannotBeWritten()

        /// <summary>
        /// What was wrong with a change of the vehicle, of its link or of its
        /// session is answered as it was while the configuration file cannot be
        /// written: a 500 is the file's, and only where it was the file that
        /// refused.
        /// </summary>
        [Test]
        public async Task ARefusalOfItsOwnIsWhatItWasWhileTheFileCannotBeWritten()
        {

            using var http  = await SignedIn();

            System.IO.Directory.CreateDirectory(Node.ConfigFile.Path + ".tmp");

            var overFull    = await http.PutAsync("api/v1/configuration/vehicle", JSONBody(new JProperty("stateOfChargePercent", 101)));
            var noAttempt   = await http.PutAsync("api/v1/configuration/v2g",     JSONBody(new JProperty("maxRetries",           0)));
            var notThere    = await http.PutAsync("api/v1/configuration/session", JSONBody(new JProperty("contractCertificate",  "nothing-by-this-handle")));

            Assert.Multiple(() => {
                Assert.That(overFull. StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a battery fuller than full");
                Assert.That(noAttempt.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a discovery of no attempts");
                Assert.That(notThere. StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a certificate the store does not have");
            });

        }

        #endregion

    }

}
