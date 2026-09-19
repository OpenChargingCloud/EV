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

using NUnit.Framework;

using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.EV.Logging;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// The vehicle's side of the 10BASE-T1S bus: which medium the session
    /// settings turn into, and what attaching answers when there is no bus
    /// to attach to.
    /// </summary>
    /// <remarks>
    /// Nothing here has a coordinator: the emulated medium is opened on a
    /// group nobody beacons on and the real one is asked for where it cannot
    /// be had. What a vehicle does on a bus with a station is the test
    /// environment's to show; what it does without one is this file's, and
    /// is what every CCS vehicle with an "auto" in its file does at every
    /// session.
    /// </remarks>
    [TestFixture]
    public class V2GLinkT1STests
    {

        #region What the settings turn into

        [Test]
        public void ABusAloneIsTheEmulatedMediumOnThatGroup()
        {

            var options = V2GLink.T1SMediumFor(new SessionConfiguration(T1SBus: "239.151.18.9:26190"), "eth0");

            Assert.That(options.Kind,           Is.EqualTo(T1STransportKind.UDP));
            Assert.That(options.Group,          Is.EqualTo(new IPEndPoint(IPAddress.Parse("239.151.18.9"), 26190)));
            Assert.That(options.InterfaceName,  Is.Null, "the emulated medium joins wherever the operating system says");

        }

        [Test]
        public void AnAdapterIsTheV2GInterfaceUnlessAnotherIsNamed()
        {

            var unnamed = V2GLink.T1SMediumFor(new SessionConfiguration(T1STransport: T1STransportKind.AfPacket), "eth0");
            var named   = V2GLink.T1SMediumFor(new SessionConfiguration(T1STransport: T1STransportKind.AfPacket, T1SInterface: "t1s0"), "eth0");
            var auto    = V2GLink.T1SMediumFor(new SessionConfiguration(T1STransport: T1STransportKind.Auto), "eth0");

            Assert.That(unnamed.InterfaceName,  Is.EqualTo("eth0"));
            Assert.That(named.  InterfaceName,  Is.EqualTo("t1s0"));
            Assert.That(auto.   InterfaceName,  Is.EqualTo("eth0"));

        }

        [Test]
        public void NothingSaidIsNoBus()
        {
            Assert.That(V2GLink.T1SMediumFor(new SessionConfiguration(), "eth0").Kind, Is.EqualTo(T1STransportKind.None));
        }

        #endregion

        #region Attaching where there is nothing to attach to

        [Test]
        public async Task AutoWithoutAnAdapterDeclinesAndIsNotAFailure()
        {

            if (OperatingSystem.IsLinux())
                Assert.Ignore("On Linux, Auto with an interface tries the adapter; the declining is tested where there is none.");

            await using var attachment = await V2GLink.AttachAsync(
                                             new T1STransportOptions(T1STransportKind.Auto, "eth0"),
                                             3,
                                             new EventLog()
                                         );

            Assert.That(attachment.IsAttached,                       Is.False);
            Assert.That(attachment.IsDeclined,                       Is.True);
            Assert.That(attachment.JSON.Value<String>("outcome"),    Is.EqualTo("declined"));
            Assert.That(attachment.JSON.Value<String>("transport"),  Is.EqualTo("none"));
            Assert.That(attachment.JSON.Value<String>("reason"),     Does.Contain("Linux only"));
            Assert.That(attachment.JSON["error"],                    Is.Null);

        }

        [Test]
        public async Task AnAdapterAskedForWhereThereIsNoneIsAFailure()
        {

            await using var attachment = await V2GLink.AttachAsync(
                                             new T1STransportOptions(T1STransportKind.AfPacket, "no-such-interface-0"),
                                             3,
                                             new EventLog()
                                         );

            Assert.That(attachment.IsAttached,                       Is.False);
            Assert.That(attachment.IsDeclined,                       Is.False);
            Assert.That(attachment.JSON.Value<String>("outcome"),    Is.EqualTo("noMedium"));
            Assert.That(attachment.JSON.Value<String>("transport"),  Is.EqualTo("afpacket"));
            Assert.That(attachment.JSON.Value<String>("error"),      Is.Not.Null.And.Not.Empty);

        }

        [Test]
        public async Task AGroupNobodyCoordinatesIsNotAttachedAndSaysWhy()
        {

            var started = DateTimeOffset.UtcNow;

            await using var attachment = await V2GLink.AttachAsync(
                                             new T1STransportOptions(T1STransportKind.UDP,
                                                                     Group: new IPEndPoint(IPAddress.Parse("239.151.18.10"), 26191)),
                                             3,
                                             new EventLog(),
                                             Timeout: TimeSpan.FromMilliseconds(800)
                                         );

            Assert.That(attachment.IsAttached,                       Is.False);
            Assert.That(attachment.IsDeclined,                       Is.False);
            Assert.That(attachment.JSON.Value<String>("outcome"),    Is.EqualTo("notAttached"));
            Assert.That(attachment.JSON.Value<String>("transport"),  Is.EqualTo("udp"));
            Assert.That(attachment.JSON.Value<String>("medium"),     Does.Contain("239.151.18.10:26191"));
            Assert.That(attachment.JSON.Value<String>("error"),      Does.Contain("BEACON"));
            Assert.That(DateTimeOffset.UtcNow - started,             Is.LessThan(TimeSpan.FromSeconds(5)), "gave up when told to");

        }

        #endregion

    }

}
