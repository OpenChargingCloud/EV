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
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using cloud.charging.open.protocols.ISO15118.Metering;
using cloud.charging.open.protocols.ISO15118.Sap;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso2;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso20;
using cloud.charging.open.protocols.ISO15118.Transport;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// A session of the vehicle, against a station of the reference
    /// implementation that runs in this process.
    /// </summary>
    public partial class VehicleConformance
    {

        #region WhatThePackDidIsLoggedWithItsNameOnce(Protocol)

        /// <summary>
        /// What the pack did in a session is logged as the session describes
        /// it, and that names the battery itself: the line it was logged in
        /// named it again, "Battery: Battery: 56.7 % of 60.5 kWh". Over -2 and
        /// over -20, which log it each.
        /// </summary>
        [TestCase("2")]
        [TestCase("20")]
        public async Task WhatThePackDidIsLoggedWithItsNameOnce(String Protocol)
        {

            var vehicle  = (EV) Node;

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                  ["protocol"]                = Protocol,
                                                                  ["maxChargingTimeSeconds"]  = 300
                                                              },
                                                              out var refused),
                        Is.True, refused);

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));

            var station  = OneSessionAt(listener, Protocol == "2" ? ProtocolVariant.Iso15118_2 : ProtocolVariant.Iso15118_20);
            var run      = await vehicle.RunSessionAsync(Connect: $"127.0.0.1:{listener.LocalEndpoint.Port}");

            Assert.That(run.Value<String>("outcome"), Is.EqualTo("completed"), run.ToString());

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            var described  = run["battery"]?.Value<String>("describe");
            var logged     = vehicle.Log.Recent(100, Tag: "session").Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(described, Does.StartWith("Battery: "),             run.ToString());
                Assert.That(logged,    Has.Some.EqualTo(described),             String.Join(Environment.NewLine, logged));
                Assert.That(logged,    Has.None.Contains("Battery: Battery:"),  String.Join(Environment.NewLine, logged));
            });

        }

        #endregion

        #region TheStationsMeterStandsBesideTheVehiclesOwnCount(Protocol)

        /// <summary>
        /// A station with a meter reports its readings, and the result carries
        /// each of them beside what the vehicle had counted itself by then: the
        /// same watt-hour in a session where nothing is wrong, signed and said
        /// to be so, with the meter's clock read off the same element. Over -20
        /// the vehicle has to ask for the reading, and does.
        /// </summary>
        [TestCase("2")]
        [TestCase("20")]
        public async Task TheStationsMeterStandsBesideTheVehiclesOwnCount(String Protocol)
        {

            var vehicle  = (EV) Node;

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                  ["protocol"]                = Protocol,
                                                                  ["maxChargingTimeSeconds"]  = 300
                                                              },
                                                              out var refused),
                        Is.True, refused);

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));

            var meter    = new SigningMeter("VAN*M1", ECDsa.Create(ECCurve.NamedCurves.nistP256), TimeProvider.System);
            var station  = OneSessionAt(listener, Protocol == "2" ? ProtocolVariant.Iso15118_2 : ProtocolVariant.Iso15118_20, meter);
            var run      = await vehicle.RunSessionAsync(Connect: $"127.0.0.1:{listener.LocalEndpoint.Port}");

            Assert.That(run.Value<String>("outcome"), Is.EqualTo("completed"), run.ToString());

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            var readings  = run["transparency"]?["readings"] as JArray;
            var logged    = vehicle.Log.Recent(200, Tag: "meter").Select(entry => entry.Message).ToArray();

            Assert.That(readings, Is.Not.Null.And.Not.Empty, run.ToString());

            Assert.Multiple(() => {

                foreach (var reading in readings!)
                {
                    Assert.That(reading.Value<String> ("meterId"),       Is.EqualTo("VAN*M1"),  reading.ToString());
                    Assert.That(reading.Value<Boolean>("signed"),        Is.True,               reading.ToString());
                    Assert.That(reading.Value<Double> ("differenceWh"),  Is.Zero,               reading.ToString());
                    Assert.That(reading.Value<String> ("meterTime"),     Is.Not.Null,           reading.ToString());
                }

                Assert.That(run["transparency"]?["meterClock"]?.Value<Boolean?>("agrees"),  Is.True,  run.ToString());

                // The first reading while the session ran - the ones after it agree as it did, and
                // a line for each would be hundreds - and one line that sums them up.
                Assert.That(logged.Count(line => line.Contains("VAN*M1 reads")),  Is.EqualTo(1),                         String.Join(Environment.NewLine, logged));
                Assert.That(logged,                                               Has.Some.Contains("at the last one"),  String.Join(Environment.NewLine, logged));

            });

        }

        #endregion

        #region TheStationsClockIsComparedBeforeAnythingElse(Skew, Agrees)

        /// <summary>
        /// Over -20 the station's clock is on the header of every response, so
        /// the vehicle knows how far it is off after the first one - and says so
        /// as a warning when that is more than it tolerates, ninety seconds here.
        /// </summary>
        [TestCase( 0, true )]
        [TestCase(90, false)]
        public async Task TheStationsClockIsComparedBeforeAnythingElse(Int32 Skew, Boolean Agrees)
        {

            var vehicle  = (EV) Node;

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                  ["protocol"]                = "20",
                                                                  ["maxChargingTimeSeconds"]  = 300
                                                              },
                                                              out var refused),
                        Is.True, refused);

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));

            var station  = OneSessionAt(listener, ProtocolVariant.Iso15118_20, Clock: new SkewedClock(TimeSpan.FromSeconds(Skew)));
            var run      = await vehicle.RunSessionAsync(Connect: $"127.0.0.1:{listener.LocalEndpoint.Port}");

            Assert.That(run.Value<String>("outcome"), Is.EqualTo("completed"), run.ToString());

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            var clock   = run["transparency"]?["stationClock"];
            var logged  = vehicle.Log.Recent(200, Tag: "clock").ToArray();

            Assert.That(clock, Is.Not.Null, run.ToString());

            Assert.Multiple(() => {

                Assert.That(clock!.Value<Boolean?>("agrees"),                 Is.EqualTo(Agrees),              clock.ToString());
                Assert.That(clock.Value<Double>("firstOffset_ms") / 1000,     Is.EqualTo(Skew).Within(2.0),    clock.ToString());
                Assert.That(clock.Value<Int32> ("samples"),                   Is.EqualTo(run.Value<Int32>("exchanges")), run.ToString());

                Assert.That(logged.Count(entry => entry.Message.Contains("the station's clock is")),  Is.EqualTo(1));

                // No reading at this station, so no second clock - and the result says null, not zero.
                Assert.That(run["transparency"]?["meterClock"]?.Type,  Is.EqualTo(JTokenType.Null),  run.ToString());

            });

        }

        /// <summary>A clock that is a fixed amount ahead of the system's.</summary>
        private sealed class SkewedClock(TimeSpan Skew) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Skew;
        }

        #endregion

        #region (private static) OneSessionAt(Listener, Protocol, Meter = null, Clock = null)

        /// <summary>
        /// One DC session of the given protocol, as the reference station runs
        /// one, at the listener's port.
        /// </summary>
        /// <param name="Meter">The station's meter, where it has one: it reports a reading only then.</param>
        /// <param name="Clock">The station's clock, where it is not the system's.</param>
        private static async Task OneSessionAt(TcpV2GListener   Listener,
                                               ProtocolVariant  Protocol,
                                               SigningMeter?    Meter   = null,
                                               TimeProvider?    Clock   = null)
        {

            using var stream = await Listener.AcceptAsync();

            await SapHandshake.RunSeccSideAsync(stream, Protocol, mode: PowerMode.Dc);

            if (Protocol == ProtocolVariant.Iso15118_2)
                await new Secc2   (PowerMode.Dc, TimeSpan.FromSeconds(60), Clock ?? TimeProvider.System) { InstalledMeter = Meter }.RunAsync(stream);
            else
                await new Secc20Dc(              TimeSpan.FromSeconds(60), Clock ?? TimeProvider.System) { InstalledMeter = Meter }.RunAsync(stream);

        }

        #endregion

    }

}
