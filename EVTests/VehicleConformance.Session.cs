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

        #region (private static) OneSessionAt(Listener, Protocol)

        /// <summary>
        /// One DC session of the given protocol, as the reference station runs
        /// one, at the listener's port.
        /// </summary>
        private static async Task OneSessionAt(TcpV2GListener   Listener,
                                               ProtocolVariant  Protocol)
        {

            using var stream = await Listener.AcceptAsync();

            await SapHandshake.RunSeccSideAsync(stream, Protocol, mode: PowerMode.Dc);

            if (Protocol == ProtocolVariant.Iso15118_2)
                await new Secc2   (PowerMode.Dc, TimeSpan.FromSeconds(60), TimeProvider.System).RunAsync(stream);
            else
                await new Secc20Dc(              TimeSpan.FromSeconds(60), TimeProvider.System).RunAsync(stream);

        }

        #endregion

    }

}
