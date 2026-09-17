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

using cloud.charging.open.protocols.ISO15118.SDP.Messages;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What the "v2g" section may say - and in particular the difference
    /// between a field left out and a field set to null, which is the only way
    /// back from having named an interface.
    /// </summary>
    public class V2GConfigurationTests
    {

        #region WhatIsWrittenComesBack()

        [Test]
        public void WhatIsWrittenComesBack()
        {

            var written = new V2GConfiguration(
                              InterfaceName:                "eth0",
                              RequestedSecurity:            SDP_Security.NoTLS,
                              PerAttemptTimeout:            TimeSpan.FromMilliseconds(250),
                              MaxRetries:                   20,
                              TotalDeadline:                TimeSpan.FromSeconds(30),
                              RejectNoTLSResponses:         false,
                              RequireLinkLocalSECCAddress:  false,
                              MulticastLoopback:            true
                          ).ToJSON();

            Assert.That(V2GConfiguration.TryParse(written, out var read, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(read!.InterfaceName,               Is.EqualTo("eth0"));
                Assert.That(read!.NoInterface,                 Is.False);
                Assert.That(read!.RequestedSecurity,           Is.EqualTo(SDP_Security.NoTLS));
                Assert.That(read!.PerAttemptTimeout,           Is.EqualTo(TimeSpan.FromMilliseconds(250)));
                Assert.That(read!.MaxRetries,                  Is.EqualTo(20));
                Assert.That(read!.TotalDeadline,               Is.EqualTo(TimeSpan.FromSeconds(30)));
                Assert.That(read!.RejectNoTLSResponses,        Is.False);
                Assert.That(read!.RequireLinkLocalSECCAddress, Is.False);
                Assert.That(read!.MulticastLoopback,           Is.True);
            });

        }

        #endregion

        #region AnInterfaceLeftOutIsNotTheSameAsOneSetToNull()

        [Test]
        public void AnInterfaceLeftOutIsNotTheSameAsOneSetToNull()
        {

            // Left out: the document has nothing to say, and whatever was
            // chosen before stands.
            Assert.That(V2GConfiguration.TryParse([], out var silent, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(silent!.InterfaceName, Is.Null);
                Assert.That(silent!.NoInterface,   Is.False);
            });

            // Explicitly null: the document says "whichever one comes first",
            // which is a setting and takes a named interface back.
            var cleared = new JObject(new JProperty("interface", JValue.CreateNull()));

            Assert.That(V2GConfiguration.TryParse(cleared, out var explicitly, out error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(explicitly!.InterfaceName, Is.Null);
                Assert.That(explicitly!.NoInterface,   Is.True);
            });

        }

        #endregion

        #region ClearingTheInterfaceSurvivesBeingWrittenOut()

        [Test]
        public void ClearingTheInterfaceSurvivesBeingWrittenOut()
        {

            // The null has to reach the file, or the next start would fall back
            // to whatever the constructor was handed instead of to "whichever
            // one comes first".
            var json = new V2GConfiguration(NoInterface: true).ToJSON();

            Assert.That(json.ContainsKey("interface"),         Is.True);
            Assert.That(json["interface"]?.Type,               Is.EqualTo(JTokenType.Null));

            Assert.That(V2GConfiguration.TryParse(json, out var read, out var error), Is.True, error);
            Assert.That(read!.NoInterface,                     Is.True);

        }

        #endregion

        #region OnlyTheTwoSecuritiesSDPHasAreAccepted()

        [Test]
        [TestCase("tls",    SDP_Security.TLS)]
        [TestCase("TLS",    SDP_Security.TLS)]
        [TestCase("noTls",  SDP_Security.NoTLS)]
        [TestCase("notls",  SDP_Security.NoTLS)]
        public void OnlyTheTwoSecuritiesSDPHasAreAccepted(String Written, SDP_Security Expected)
        {

            var json = new JObject(new JProperty("requestedSecurity", Written));

            Assert.That(V2GConfiguration.TryParse(json, out var read, out var error), Is.True, error);
            Assert.That(read!.RequestedSecurity, Is.EqualTo(Expected));

        }

        [Test]
        public void AnythingElseIsRefusedAndSaysWhatIsAllowed()
        {

            var json = new JObject(new JProperty("requestedSecurity", "maybe"));

            Assert.That(V2GConfiguration.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("v2g.requestedSecurity").And.Contain("tls").And.Contain("noTls"));

        }

        #endregion

    }

}
