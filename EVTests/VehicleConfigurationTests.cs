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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What the "vehicle" section of the configuration file may say, and what
    /// it may not.
    /// </summary>
    public class VehicleConfigurationTests
    {

        #region AnEmptySectionSaysNothing()

        [Test]
        public void AnEmptySectionSaysNothing()
        {

            Assert.That(VehicleConfiguration.TryParse([], out var configuration, out var error), Is.True, error);

            // Every field null, which is "the file has no opinion" and not
            // "the file says zero" - the difference the whole precedence rule
            // hangs on.
            Assert.Multiple(() => {
                Assert.That(configuration!.Name,                        Is.Null);
                Assert.That(configuration!.BatteryCapacity_kWh,         Is.Null);
                Assert.That(configuration!.StateOfCharge_percent,       Is.Null);
                Assert.That(configuration!.TargetStateOfCharge_percent, Is.Null);
            });

        }

        #endregion

        #region WhatIsWrittenComesBack()

        [Test]
        public void WhatIsWrittenComesBack()
        {

            var written = new VehicleConfiguration(
                              Name:                         "Testwagen",
                              VIN:                          "WVWZZZ1KZAW000001",
                              BatteryCapacity_kWh:          77,
                              StateOfCharge_percent:        20,
                              MaxChargingPower_kW:          11,
                              TaperFrom_percent:            85,
                              TargetStateOfCharge_percent:  80
                          ).ToJSON();

            Assert.That(VehicleConfiguration.TryParse(written, out var read, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(read!.Name,                        Is.EqualTo("Testwagen"));
                Assert.That(read!.VIN,                         Is.EqualTo("WVWZZZ1KZAW000001"));
                Assert.That(read!.BatteryCapacity_kWh,         Is.EqualTo(77));
                Assert.That(read!.StateOfCharge_percent,       Is.EqualTo(20));
                Assert.That(read!.MaxChargingPower_kW,         Is.EqualTo(11));
                Assert.That(read!.TaperFrom_percent,           Is.EqualTo(85));
                Assert.That(read!.TargetStateOfCharge_percent, Is.EqualTo(80));
            });

        }

        #endregion

        #region AFieldItWasNotToldAboutIsNotWritten()

        [Test]
        public void AFieldItWasNotToldAboutIsNotWritten()
        {

            var json = new VehicleConfiguration(Name: "Testwagen").ToJSON();

            // Not "vin": null. A page that offers a name and sends one must not
            // be able to take a VIN away with it - see TryMergeSection.
            Assert.Multiple(() => {
                Assert.That(json.ContainsKey("name"), Is.True);
                Assert.That(json.ContainsKey("vin"),  Is.False);
                Assert.That(json.ContainsKey("batteryCapacityKWh"), Is.False);
            });

        }

        #endregion

        #region AStateOfChargeAboveTheTargetIsRefused()

        [Test]
        public void AStateOfChargeAboveTheTargetIsRefused()
        {

            var json = new JObject(
                           new JProperty("stateOfChargePercent",        90),
                           new JProperty("targetStateOfChargePercent",  80)
                       );

            Assert.That(VehicleConfiguration.TryParse(json, out _, out var error), Is.False);

            // The sentence has to name both figures, or somebody reading it off
            // a form with five numbers on it has been told nothing.
            Assert.That(error, Does.Contain("90").And.Contain("80"));

        }

        #endregion

        #region AFigureOutsideItsRangeIsRefusedByName()

        [Test]
        [TestCase("stateOfChargePercent",        101.0)]
        [TestCase("stateOfChargePercent",         -1.0)]
        [TestCase("batteryCapacityKWh",            0.0)]
        [TestCase("taperFromPercent",            140.0)]
        public void AFigureOutsideItsRangeIsRefusedByName(String Field, Double Value)
        {

            Assert.That(VehicleConfiguration.TryParse(new JObject(new JProperty(Field, Value)),
                                                      out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain($"vehicle.{Field}"));

        }

        #endregion

        #region SomethingThatIsNotANumberIsRefused()

        [Test]
        public void SomethingThatIsNotANumberIsRefused()
        {

            Assert.That(VehicleConfiguration.TryParse(new JObject(new JProperty("batteryCapacityKWh", "seventy-seven")),
                                                      out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("vehicle.batteryCapacityKWh"));

        }

        #endregion

    }

}
