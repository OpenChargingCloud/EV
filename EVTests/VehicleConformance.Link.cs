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

using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.Slac;
using cloud.charging.open.protocols.ISO15118.SLAC.StateMachine;
using cloud.charging.open.protocols.ISO15118.SLAC.Transport;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.T1S;
using cloud.charging.open.protocols.ISO15118.T1S.PLCA;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;
using cloud.charging.open.protocols.ISO15118.Transport;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// A vehicle plugged in - directly, or over a SLAC pairing with a station
    /// of the reference implementation that runs in this process - holds that
    /// link until it is unplugged, and a session runs over it without pairing
    /// a second time.
    /// </summary>
    public partial class VehicleConformance
    {

        #region (private) SlacStation(Nid)

        /// <summary>
        /// A station's side of SLAC on the simulated medium, on a port of its
        /// own on the loopback, agreeing on the given network.
        /// </summary>
        private static async Task<(SlacEvseStage Stage, UdpSlacTransport Transport, Task<SlacResult> Matched)> SlacStation(Byte[] Nid)
        {

            var transport  = new UdpSlacTransport(V2GInterface.RandomMac(), new IPEndPoint(IPAddress.Loopback, 0));

            var stage      = new SlacEvseStage(transport,
                                               new EvseSlacOptions {
                                                   EvseId  = new Byte[17],
                                                   Nid     = Nid,
                                                   Nmk     = RandomNumberGenerator.GetBytes(16)
                                               });

            await stage.StartAsync();

            return (stage, transport, stage.WaitForMatchAsync());

        }

        #endregion


        #region PluggedInDirectlyIsALinkWithNothingBeforeSDP()

        /// <summary>
        /// Plugged in directly, a vehicle holds a link - with no pairing and no
        /// bus - until it is unplugged.
        /// </summary>
        [Test]
        public async Task PluggedInDirectlyIsALinkWithNothingBeforeSDP()
        {

            var vehicle  = (EV) Node;

            var plugged  = await vehicle.PlugInAsync(LinkMedium.Direct);

            Assert.Multiple(() => {
                Assert.That(plugged.Value<String>("outcome"),          Is.EqualTo("pluggedIn"), plugged.ToString());
                Assert.That(plugged.Value<String>("via"),              Is.EqualTo("direct"));
                Assert.That(plugged["slac"],                           Is.Null);
                Assert.That(plugged["t1s"],                            Is.Null);
                Assert.That(vehicle.LinkJSON?.Value<String>("via"),    Is.EqualTo("direct"), "held");
            });

            Assert.That(await vehicle.UnplugAsync(), Is.True,  "it was plugged in");
            Assert.That(vehicle.LinkJSON,            Is.Null,  "and is not any more");
            Assert.That(await vehicle.UnplugAsync(), Is.False, "nothing to unplug the second time");

        }

        #endregion

        #region PluggedInOverSLACSaysWhichStationIsAtTheCable()

        /// <summary>
        /// Plugged in over SLAC, a vehicle says which station it paired with -
        /// its MAC address and how loudly it heard the vehicle - and the
        /// network they agreed on.
        /// </summary>
        [Test]
        public async Task PluggedInOverSLACSaysWhichStationIsAtTheCable()
        {

            var vehicle  = (EV) Node;
            var nid      = RandomNumberGenerator.GetBytes(7);

            var (stage, transport, matched) = await SlacStation(nid);

            await using var _ = stage;

            var plugged  = await vehicle.PlugInAsync(LinkMedium.SLAC,
                                                     new SessionConfiguration(SLACPeer: $"127.0.0.1:{transport.LocalEndpoint.Port}"));

            Assert.Multiple(() => {
                Assert.That(plugged.Value<String>("outcome"),                    Is.EqualTo("pluggedIn"), plugged.ToString());
                Assert.That(plugged.Value<String>("via"),                        Is.EqualTo("slac"));
                Assert.That(plugged["slac"]!.Value<String>("nid"),              Is.EqualTo(Convert.ToHexString(nid)));
                Assert.That(plugged["slac"]!["station"]!.Value<String>("mac"),  Is.EqualTo(transport.LocalMac.ToString()), "the station at the cable");
                Assert.That(plugged["slac"]!["candidates"]!.Children().Count(),  Is.EqualTo(1), "the one station that answered");
            });

            Assert.That(vehicle.SessionSettings.SLACPeer, Is.Null, "plugging in sets nothing");

        }

        #endregion

        #region PluggedInOverT1SIsANodeOnTheCouplersBusUntilUnplugged()

        /// <summary>
        /// Plugged in over 10BASE-T1S, onto the emulated bus of a coupler of
        /// the reference implementation: the vehicle is a node the coupler
        /// knows, and stays one until it is unplugged - which leaves the bus.
        /// </summary>
        [Test]
        public async Task PluggedInOverT1SIsANodeOnTheCouplersBusUntilUnplugged()
        {

            var vehicle  = (EV) Node;
            var group    = new IPEndPoint(IPAddress.Parse("239.151.18.3"), TestPorts.Free());

            await using var medium      = new UdpMulticastT1STransport(T1SConstants.RandomLocalMac(), group);
            await using var coordinator = new PlcaCoordinator(medium, new PlcaCoordinatorOptions(
                                                                          Name:                        "test coupler",
                                                                          TransmitOpportunityTimeout:  TimeSpan.FromMilliseconds(100),
                                                                          DiscoveryWindow:             TimeSpan.FromMilliseconds(100),
                                                                          CycleGap:                    TimeSpan.FromMilliseconds(50),
                                                                          LostAfterMissedCycles:       3
                                                                      ));

            var left = new TaskCompletionSource<PlcaNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.NodeLeft += (_, node) => left.TrySetResult(node);

            await medium.     StartAsync();
            await coordinator.StartAsync();

            var plugged  = await vehicle.PlugInAsync(LinkMedium.T1S,
                                                     new SessionConfiguration(T1STransport:  T1STransportKind.UDP,
                                                                              T1SBus:        $"{group.Address}:{group.Port}"));

            Assert.Multiple(() => {
                Assert.That(plugged.Value<String>("outcome"),          Is.EqualTo("pluggedIn"), plugged.ToString());
                Assert.That(plugged.Value<String>("via"),              Is.EqualTo("t1s"));
                Assert.That(plugged["t1s"]!.Value<String>("outcome"),  Is.EqualTo("attached"));
                Assert.That(coordinator.Nodes,                         Has.Count.EqualTo(1), "the coupler knows the vehicle");
            });

            Assert.That(await vehicle.UnplugAsync(), Is.True);

            Assert.That(await left.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.Not.Null, "the vehicle left the bus");
            Assert.That(vehicle.LinkJSON, Is.Null);

        }

        #endregion

        #region PluggedInOverSLACWithNoPeerIsRefusedAndNotPluggedIn()

        [Test]
        public async Task PluggedInOverSLACWithNoPeerIsRefusedAndNotPluggedIn()
        {

            var vehicle  = (EV) Node;

            var plugged  = await vehicle.PlugInAsync(LinkMedium.SLAC);

            Assert.That(plugged.Value<String>("outcome"), Is.EqualTo("notConfigured"), plugged.ToString());
            Assert.That(plugged.Value<String>("error"),   Does.Contain("no SLAC peer"));
            Assert.That(vehicle.LinkJSON,                 Is.Null);

        }

        #endregion

        #region ASessionOverAHeldSLACLinkDoesNotPairAgain()

        /// <summary>
        /// Plugged in over SLAC, and the station's SLAC side gone afterwards: a
        /// session still runs, over the pairing the vehicle holds, and says it.
        /// One that paired again would find nobody to pair with.
        /// </summary>
        [Test]
        public async Task ASessionOverAHeldSLACLinkDoesNotPairAgain()
        {

            var vehicle  = (EV) Node;
            var nid      = RandomNumberGenerator.GetBytes(7);

            var (stage, transport, matched) = await SlacStation(nid);

            var peer     = $"127.0.0.1:{transport.LocalEndpoint.Port}";

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                  ["protocol"]                = "20",
                                                                  ["maxChargingTimeSeconds"]  = 300,
                                                                  ["slacPeer"]                = peer
                                                              },
                                                              out var refused),
                        Is.True, refused);

            var plugged  = await vehicle.PlugInAsync(LinkMedium.SLAC);

            Assert.That(plugged.Value<String>("outcome"), Is.EqualTo("pluggedIn"), plugged.ToString());

            await matched.WaitAsync(TimeSpan.FromSeconds(10));
            await stage.DisposeAsync();

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));

            var station  = OneSessionAt(listener, ProtocolVariant.Iso15118_20);
            var run      = await vehicle.RunSessionAsync(Connect: $"127.0.0.1:{listener.LocalEndpoint.Port}");

            Assert.That(run.Value<String>("outcome"), Is.EqualTo("completed"), run.ToString());

            await station.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Multiple(() => {
                Assert.That(run["slac"]?.Value<String>("nid"),        Is.EqualTo(Convert.ToHexString(nid)), "the pairing held");
                Assert.That(vehicle.LinkJSON?.Value<String>("via"),   Is.EqualTo("slac"),                  "still plugged in after the session");
            });

        }

        #endregion

        #region ASessionPluggedInDirectlyDoesNotPairWhateverTheSettingsSay()

        /// <summary>
        /// A SLAC peer in the settings that nobody answers at, and the vehicle
        /// plugged in directly: the session does not pair at all.
        /// </summary>
        [Test]
        public async Task ASessionPluggedInDirectlyDoesNotPairWhateverTheSettingsSay()
        {

            var vehicle  = (EV) Node;

            using var nobody = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject {
                                                                  ["protocol"]                = "2",
                                                                  ["maxChargingTimeSeconds"]  = 300,
                                                                  ["slacPeer"]                = $"127.0.0.1:{((IPEndPoint) nobody.Client.LocalEndPoint!).Port}"
                                                              },
                                                              out var refused),
                        Is.True, refused);

            Assert.That((await vehicle.PlugInAsync(LinkMedium.Direct)).Value<String>("outcome"), Is.EqualTo("pluggedIn"));

            using var listener  = new TcpV2GListener(new IPEndPoint(IPAddress.Loopback, 0));

            var station  = OneSessionAt(listener, ProtocolVariant.Iso15118_2);
            var run      = await vehicle.RunSessionAsync(Connect: $"127.0.0.1:{listener.LocalEndpoint.Port}");

            Assert.That(run.Value<String>("outcome"), Is.EqualTo("completed"), run.ToString());
            Assert.That(run["slac"],                  Is.Null, "no pairing");

            await station.WaitAsync(TimeSpan.FromSeconds(30));

        }

        #endregion

        #region PluggingInAndOutOverTheWire()

        /// <summary>
        /// POST and DELETE /api/v1/link: plugged in directly, the V2G
        /// configuration says so; a medium that is none is refused, and so is a
        /// stage field that is wrong, in the words the setting would be.
        /// </summary>
        [Test]
        public async Task PluggingInAndOutOverTheWire()
        {

            using var http = await SignedIn();

            var (badVia,    saidVia)    = await Send(http, HttpMethod.Post,   "api/v1/link", new JObject { ["via"] = "wifi" });
            var (badStage,  saidStage)  = await Send(http, HttpMethod.Post,   "api/v1/link", new JObject { ["via"] = "t1s", ["t1sWeight"] = 9 });
            var (plugged,   pluggedIn)  = await Send(http, HttpMethod.Post,   "api/v1/link", new JObject { ["via"] = "direct" });
            var shown                   = await GetJSON(http, "api/v1/configuration/v2g");
            var (unplugged, afterwards) = await Send(http, HttpMethod.Delete, "api/v1/link", null);

            Assert.Multiple(() => {

                Assert.That(badVia,                                         Is.EqualTo(HttpStatusCode.BadRequest), saidVia.ToString());
                Assert.That(saidVia.ToString(),                             Does.Contain("direct"));
                Assert.That(badStage,                                       Is.EqualTo(HttpStatusCode.BadRequest), saidStage.ToString());
                Assert.That(saidStage.ToString(),                           Does.Contain("t1sWeight"));

                Assert.That(plugged,                                        Is.EqualTo(HttpStatusCode.OK), pluggedIn.ToString());
                Assert.That(pluggedIn["result"]!.Value<String>("outcome"),  Is.EqualTo("pluggedIn"));
                Assert.That(pluggedIn["link"]!.Value<String>("via"),        Is.EqualTo("direct"));
                Assert.That(shown["link"]!.Value<String>("via"),            Is.EqualTo("direct"), "the V2G configuration says how it is plugged in");

                Assert.That(unplugged,                                      Is.EqualTo(HttpStatusCode.OK), afterwards.ToString());
                Assert.That(afterwards["link"]!.Type,                       Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

        #region ChargingAtAStationOverTheWireIsRefusedWhereNoneWasFound()

        /// <summary>
        /// POST /api/v1/session with a station of the last discovery where none
        /// was found is a conflict, said as one, and not a session that
        /// started; a TLS stack that is none is refused as the setting would be.
        /// </summary>
        [Test]
        public async Task ChargingAtAStationOverTheWireIsRefusedWhereNoneWasFound()
        {

            using var http = await SignedIn();

            var (noStation, saidStation) = await Send(http, HttpMethod.Post, "api/v1/session", new JObject { ["station"] = 0 });
            var (badTLS,    saidTLS)     = await Send(http, HttpMethod.Post, "api/v1/session", new JObject { ["station"] = 0, ["tls"] = "ssl3" });
            var (both,      saidBoth)    = await Send(http, HttpMethod.Post, "api/v1/session", new JObject { ["station"] = 0, ["connect"] = "127.0.0.1:15118" });

            Assert.Multiple(() => {
                Assert.That(noStation,               Is.EqualTo(HttpStatusCode.Conflict),   saidStation.ToString());
                Assert.That(saidStation.ToString(),  Does.Contain("Look for one first"));
                Assert.That(badTLS,                  Is.EqualTo(HttpStatusCode.BadRequest), saidTLS.ToString());
                Assert.That(saidTLS.ToString(),      Does.Contain("session.tls"));
                Assert.That(both,                    Is.EqualTo(HttpStatusCode.BadRequest), saidBoth.ToString());
            });

        }

        #endregion

        #region AStationTheLastDiscoveryDidNotFindIsRefusedBeforeAnythingIsSaid()

        [Test]
        public async Task AStationTheLastDiscoveryDidNotFindIsRefusedBeforeAnythingIsSaid()
        {

            var vehicle  = (EV) Node;

            var run      = await vehicle.RunSessionAsync(Station: 0, TLS: TlsStack.None);

            Assert.That(run.Value<String>("outcome"), Is.EqualTo("noStation"), run.ToString());
            Assert.That(run.Value<String>("error"),   Does.Contain("Look for one first"));
            Assert.That(vehicle.SessionRunning,       Is.False);

        }

        #endregion

    }

}
