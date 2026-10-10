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

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using cloud.charging.open.protocols.ISO15118.Sap;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso20;
using cloud.charging.open.protocols.ISO15118.Transport;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// A station that tells the vehicle to end the charging - EVSENotification
    /// Terminate in an ISO 15118-20 charge-loop response, as a station does
    /// whose coupler got too hot: the vehicle ends the session for good, even
    /// where it was told to pause it, and nothing is left to rejoin.
    /// </summary>
    public partial class VehicleConformance
    {

        #region ToldTerminateAPausingVehicleLeavesNothingToRejoin()

        /// <summary>
        /// Asked to end paused, told Terminate by the station: the run says the
        /// station ended it, not that it paused, keeps no paused session to
        /// rejoin, and the log says why.
        /// </summary>
        [Test]
        public async Task ToldTerminateAPausingVehicleLeavesNothingToRejoin()
        {

            var vehicle  = TerminateReady();

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var station  = OneTerminatingSessionAt(listener);
            var run      = await vehicle.RunSessionAsync(Connect:            $"127.0.0.1:{listener.LocalEndpoint.Port}",
                                                         Pause:              true,
                                                         CancellationToken:  timeout.Token);

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            var logged   = vehicle.Log.Recent(100, Tag: "session").Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(run.Value<String>("outcome"),                       Is.EqualTo("completed"),          run.ToString());
                Assert.That(run.Value<Boolean?>("terminatedByStation"),         Is.True,                          run.ToString());
                Assert.That(run.Value<Boolean?>("paused"),                      Is.False,                         run.ToString());
                Assert.That(run["pausedSessionId"],                             Is.Null,                          run.ToString());
                Assert.That(run["battery"]?.Value<String>("stoppedBecause"),    Is.EqualTo("StationTerminated"),  run.ToString());
                Assert.That(vehicle.SessionConfigurationJSON()["paused"]?.Type, Is.EqualTo(JTokenType.Null),      "nothing to rejoin");
                Assert.That(logged,                                             Has.Some.Contains("told the vehicle to end the charging"),
                                                                                String.Join(Environment.NewLine, logged));
            });

        }

        #endregion

        #region ToldTerminateAPauseAndRejoinDoesNotRejoin()

        /// <summary>
        /// A pause and a rejoin in one run, told Terminate in its first half:
        /// there is no second half, because there is no paused session for one
        /// to rejoin - it does not reconnect to a station that ended it.
        /// </summary>
        [Test]
        public async Task ToldTerminateAPauseAndRejoinDoesNotRejoin()
        {

            var vehicle  = TerminateReady();

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var station  = OneTerminatingSessionAt(listener);
            var run      = await vehicle.RunSessionAsync(Connect:            $"127.0.0.1:{listener.LocalEndpoint.Port}",
                                                         PauseResume:        true,
                                                         CancellationToken:  timeout.Token);

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            var logged   = vehicle.Log.Recent(100, Tag: "session").Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(run.Value<String>("outcome"),               Is.EqualTo("completed"),  run.ToString());
                Assert.That(run.Value<Boolean?>("terminatedByStation"), Is.True,                  run.ToString());
                Assert.That(run["pausedRun"],                           Is.Null,                  "a second half was run");
                Assert.That(logged,                                     Has.None.Contains("reconnecting to rejoin"),
                                                                        String.Join(Environment.NewLine, logged));
            });

        }

        #endregion


        #region (private) TerminateReady()

        /// <summary>
        /// The vehicle, set to ISO 15118-20 and to a goal the station's Terminate
        /// comes before.
        /// </summary>
        private EV TerminateReady()
        {

            var vehicle = (EV) Node;

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                  ["protocol"]                = "20",
                                                                  ["maxChargingTimeSeconds"]  = 300
                                                              },
                                                              out var refused),
                        Is.True, refused);

            return vehicle;

        }

        #endregion

        #region (private static) OneTerminatingSessionAt(Listener)

        /// <summary>
        /// One ISO 15118-20 DC session at the listener's port, of a reference
        /// station that says Terminate in its first charge-loop response.
        /// </summary>
        private static async Task OneTerminatingSessionAt(TcpV2GListener Listener)
        {

            using var stream = await Listener.AcceptAsync();

            await SapHandshake.RunSeccSideAsync(stream, ProtocolVariant.Iso15118_20, mode: PowerMode.Dc);

            var station = new Secc20Dc(TimeSpan.FromSeconds(60), TimeProvider.System);
            station.Terminate();

            await station.RunAsync(stream);

        }

        #endregion

    }

}
