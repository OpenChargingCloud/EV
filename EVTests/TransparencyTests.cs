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

using NUnit.Framework;

using Newtonsoft.Json.Linq;

using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.protocols.ISO15118.Metering;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What the session result says of the station's meter beside the vehicle's own count, as the
    /// Charging page reads it.
    /// </summary>
    /// <remarks>
    /// In a bidirectional session the station keeps two rising registers and the vehicle one count that
    /// goes down when it gives energy back, so the difference is taken on the station's net figure. The
    /// JSON carries both registers and that figure, so the page can say all three.
    /// </remarks>
    [TestFixture]
    public class TransparencyTests
    {

        private static JObject ReadingOf(StationReading Reading)

            => (JObject) ((JArray) Transparency.JSON(null, new ClockComparison(), [ Reading ], Reading.VehicleWh)["readings"]!)[0];


        [Test]
        public void ADischargeReadingCarriesBothRegistersAndTheNetFigure()
        {

            var json = ReadingOf(new StationReading(3, "VAN*M*4711", StationWh: 0, VehicleWh: -2001, Signed: true,
                                                    MeterTime: null, DischargedWh: 2001));

            Assert.Multiple(() => {
                Assert.That(json["stationWh"]!.Value<ulong>(),     Is.Zero);
                Assert.That(json["dischargedWh"]!.Value<ulong>(),  Is.EqualTo(2001));
                Assert.That(json["stationNetWh"]!.Value<double>(), Is.EqualTo(-2001));
                Assert.That(json["differenceWh"]!.Value<double>(), Is.Zero,
                            "the vehicle gave back what the meter counted, so there is no difference");
            });

        }

        [Test]
        public void AChargeOnlyReadingSaysWhatItAlwaysDid()
        {

            var json = ReadingOf(new StationReading(3, "VAN*M1", StationWh: 4200, VehicleWh: 4200, Signed: true, MeterTime: null));

            Assert.Multiple(() => {
                Assert.That(json["stationWh"]!.Value<ulong>(),     Is.EqualTo(4200));
                Assert.That(json["dischargedWh"]!.Type,            Is.EqualTo(JTokenType.Null));
                Assert.That(json["stationNetWh"]!.Value<double>(), Is.EqualTo(4200));
                Assert.That(json["differenceWh"]!.Value<double>(), Is.Zero);
            });

        }

    }

}
