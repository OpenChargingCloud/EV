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
    /// A station whose meter counts ten percent more than the vehicle took,
    /// against a vehicle set to stop beyond five: the vehicle ends the charging
    /// over the meter, for good, even where it was told to pause - and over
    /// ISO 15118-20 it tells the station why.
    /// </summary>
    public partial class VehicleConformance
    {

        #region AMeterThatCountsTooMuchEndsTheCharging(Protocol)

        /// <summary>
        /// Asked to end paused, the station's meter ten percent over: the run
        /// says the vehicle ended it over the meter and why, that it did not
        /// pause, keeps nothing to rejoin, and the log says so. The reason went
        /// to the station over -20, and stayed in the vehicle over -2.
        /// </summary>
        [TestCase("2")]
        [TestCase("20")]
        public async Task AMeterThatCountsTooMuchEndsTheCharging(String Protocol)
        {

            var vehicle  = TerminateReady(Protocol);

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { ["stopOnMeterDeviation"] = "5" },
                                                              out var refused),
                        Is.True, refused);

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var station  = OneOvercountingSessionAt(listener, Protocol);
            var run      = await vehicle.RunSessionAsync(Connect:            $"127.0.0.1:{listener.LocalEndpoint.Port}",
                                                         Pause:              true,
                                                         CancellationToken:  timeout.Token);

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            var logged     = vehicle.Log.Recent(100, Tag: "session").Select(entry => entry.Message).ToArray();
            var deviation  = run["meteringDeviation"] as JObject;

            Assert.Multiple(() => {
                Assert.That(run.Value<String>("outcome"),                       Is.EqualTo("completed"),          run.ToString());
                Assert.That(deviation,                                          Is.Not.Null,                      run.ToString());
                Assert.That(deviation?.Value<String>("explanation"),            Does.StartWith("meter "),         run.ToString());
                Assert.That(deviation?.Value<Double>("stationNetWh") ?? 0,      Is.GreaterThan(deviation?.Value<Double>("vehicleWh") ?? 0),
                                                                                run.ToString());
                Assert.That(run.Value<Boolean?>("terminatedByStation"),         Is.False,                         run.ToString());
                Assert.That(run.Value<Boolean?>("paused"),                      Is.False,                         run.ToString());
                Assert.That(run["pausedSessionId"],                             Is.Null,                          run.ToString());
                Assert.That(run["battery"]?.Value<String>("stoppedBecause"),    Is.EqualTo("MeteringDeviation"),  run.ToString());
                Assert.That(vehicle.SessionConfigurationJSON()["paused"]?.Type, Is.EqualTo(JTokenType.Null),      "nothing to rejoin");
                Assert.That(logged,                                             Has.Some.Contains("ended the charging over the station's meter"),
                                                                                String.Join(Environment.NewLine, logged));
            });

            if (Protocol == "20")
                Assert.Multiple(() => {
                    Assert.That(deviation?.Value<String>("terminationCode"),    Is.EqualTo(MeteringDeviation.TerminationCode), run.ToString());
                    Assert.That(deviation?.Value<Boolean>("toldStation"),       Is.True,                          run.ToString());
                    Assert.That(logged,                                         Has.Some.Contains($"EVTerminationCode {MeteringDeviation.TerminationCode}"),
                                                                                String.Join(Environment.NewLine, logged));
                });

            else
                Assert.Multiple(() => {
                    Assert.That(deviation?["terminationCode"]?.Type,            Is.EqualTo(JTokenType.Null),      run.ToString());
                    Assert.That(deviation?.Value<Boolean>("toldStation"),       Is.False,                         run.ToString());
                    Assert.That(logged,                                         Has.Some.Contains("stayed in the vehicle"),
                                                                                String.Join(Environment.NewLine, logged));
                });

        }

        #endregion

        #region AMeterThatAgreesEndsNothing()

        /// <summary>
        /// The same limit against a meter that counts what the vehicle took: the
        /// run ends paused as it was asked to, and says nothing of the meter.
        /// </summary>
        [Test]
        public async Task AMeterThatAgreesEndsNothing()
        {

            var vehicle  = TerminateReady("20");

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { ["stopOnMeterDeviation"] = "5" },
                                                              out var refused),
                        Is.True, refused);

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var station  = OneOvercountingSessionAt(listener, "20", Scale: 1.0);
            var run      = await vehicle.RunSessionAsync(Connect:            $"127.0.0.1:{listener.LocalEndpoint.Port}",
                                                         Pause:              true,
                                                         CancellationToken:  timeout.Token);

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Multiple(() => {
                Assert.That(run.Value<String>("outcome"),          Is.EqualTo("completed"),      run.ToString());
                Assert.That(run["meteringDeviation"]?.Type,        Is.EqualTo(JTokenType.Null),  run.ToString());
                Assert.That(run.Value<Boolean?>("paused"),         Is.True,                      run.ToString());
            });

        }

        #endregion


        #region (private static) OneOvercountingSessionAt(Listener, Protocol, Scale = 1.10)

        /// <summary>
        /// One DC session of the given protocol at the listener's port, of a
        /// reference station whose installed meter counts <paramref name="Scale"/>
        /// times what it delivered.
        /// </summary>
        private static async Task OneOvercountingSessionAt(TcpV2GListener  Listener,
                                                           String          Protocol,
                                                           Double          Scale   = 1.10)
        {

            using var stream = await Listener.AcceptAsync();
            using var key    = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var meter = new SigningMeter("VAN*M1", key, TimeProvider.System) { Scale = Scale };

            if (Protocol == "2")
            {

                await SapHandshake.RunSeccSideAsync(stream, ProtocolVariant.Iso15118_2, mode: PowerMode.Dc);

                var station = new Secc2(PowerMode.Dc, TimeSpan.FromSeconds(60), TimeProvider.System) { InstalledMeter = meter };

                await station.RunAsync(stream);

            }
            else
            {

                await SapHandshake.RunSeccSideAsync(stream, ProtocolVariant.Iso15118_20, mode: PowerMode.Dc);

                var station = new Secc20Dc(TimeSpan.FromSeconds(60), TimeProvider.System) { InstalledMeter = meter };

                await station.RunAsync(stream);

            }

        }

        #endregion

    }

}
