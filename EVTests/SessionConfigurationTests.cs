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

using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.StateMachines;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What the "session" section may say, and what it may not.
    /// </summary>
    public class SessionConfigurationTests
    {

        #region AnEmptySectionSaysNothing()

        [Test]
        public void AnEmptySectionSaysNothing()
        {

            Assert.That(SessionConfiguration.TryParse([], out var configuration, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(configuration!.Connect,             Is.Null);
                Assert.That(configuration!.Protocol,            Is.Null);
                Assert.That(configuration!.OfferBoth,           Is.Null);
                Assert.That(configuration!.Mode,                Is.Null);
                Assert.That(configuration!.TLS,                 Is.Null);
                Assert.That(configuration!.Cleared,             Is.Empty);
            });

        }

        #endregion

        #region WhatIsWrittenComesBack()

        [Test]
        public void WhatIsWrittenComesBack()
        {

            var written = new SessionConfiguration(
                              Connect:                       "[::1]:15118",
                              Protocol:                      ProtocolVariant.Iso15118_2,
                              OfferBoth:                     false,
                              Mode:                          PowerMode.Ac,
                              MCS:                           false,
                              TLS:                           TlsStack.BouncyCastle,
                              TargetEnergy_kWh:              12.5,
                              MaxChargingTime:               TimeSpan.FromMinutes(90),
                              DepartureIn:                   TimeSpan.FromMinutes(45),
                              MinimumStateOfCharge_percent:  60,
                              Renegotiate:                   true,
                              SLACPeer:                      "127.0.0.1:9000"
                          ).ToJSON();

            Assert.That(SessionConfiguration.TryParse(written, out var read, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(read!.Connect,                       Is.EqualTo("[::1]:15118"));
                Assert.That(read!.Protocol,                      Is.EqualTo(ProtocolVariant.Iso15118_2));
                Assert.That(read!.OfferBoth,                     Is.False);
                Assert.That(read!.Mode,                          Is.EqualTo(PowerMode.Ac));
                Assert.That(read!.MCS,                           Is.False);
                Assert.That(read!.TLS,                           Is.EqualTo(TlsStack.BouncyCastle));
                Assert.That(read!.TargetEnergy_kWh,              Is.EqualTo(12.5));
                Assert.That(read!.MaxChargingTime,               Is.EqualTo(TimeSpan.FromMinutes(90)));
                Assert.That(read!.DepartureIn,                   Is.EqualTo(TimeSpan.FromMinutes(45)));
                Assert.That(read!.MinimumStateOfCharge_percent,  Is.EqualTo(60));
                Assert.That(read!.Renegotiate,                   Is.True);
                Assert.That(read!.SLACPeer,                      Is.EqualTo("127.0.0.1:9000"));
            });

        }

        #endregion

        #region OfferingBothIsNotTheSameAsSayingNothing()

        [Test]
        public void OfferingBothIsNotTheSameAsSayingNothing()
        {

            // "both" is -20 at the top of the offer with -2 behind it, which is
            // a choice somebody made and has to survive being written out.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("protocol", "both")),
                                                      out var both, out var error),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(both!.Protocol,         Is.EqualTo(ProtocolVariant.Iso15118_20));
                Assert.That(both!.OfferBoth,        Is.True);
                Assert.That(both!.ProtocolWritten,  Is.EqualTo("both"));
            });

            // Nothing said at all: both fields stay null, and nothing is
            // written - which is what leaves the constructor's choice standing.
            Assert.That(SessionConfiguration.TryParse([], out var silent, out error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(silent!.Protocol,         Is.Null);
                Assert.That(silent!.OfferBoth,        Is.Null);
                Assert.That(silent!.ProtocolWritten,  Is.Null);
                Assert.That(silent!.ToJSON().ContainsKey("protocol"), Is.False);
            });

        }

        #endregion

        #region MCSIsDCUnderAnotherName()

        [Test]
        public void MCSIsDCUnderAnotherName()
        {

            // Not a third mode on the wire - the DC message set under
            // energy-transfer services 8/9 - and still a third word here.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("mode", "mcs")),
                                                      out var mcs, out var error),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(mcs!.Mode,         Is.EqualTo(PowerMode.Dc));
                Assert.That(mcs!.MCS,          Is.True);
                Assert.That(mcs!.ModeWritten,  Is.EqualTo("mcs"));
            });

        }

        [Test]
        public void MCSAgainstAPinnedIso2IsRefused()
        {

            // Those services exist in no other catalogue, so this is a request
            // that cannot be met. Refused rather than quietly running plain DC.
            var json = new JObject(
                           new JProperty("mode",      "mcs"),
                           new JProperty("protocol",  "2")
                       );

            Assert.That(SessionConfiguration.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("mcs").And.Contain("ISO 15118-20"));

        }

        [Test]
        public void MCSBesideAnOfferOfBothIsFine()
        {

            // The handshake can still settle on -20; only the -2 half cannot
            // carry it.
            var json = new JObject(
                           new JProperty("mode",      "mcs"),
                           new JProperty("protocol",  "both")
                       );

            Assert.That(SessionConfiguration.TryParse(json, out var read, out var error), Is.True, error);
            Assert.That(read!.MCS, Is.True);

        }

        #endregion

        #region AnEndpointIsRefusedWhereASocketCouldNotUseIt()

        [Test]
        [TestCase("localhost")]
        [TestCase("fe80::1:15118")]
        [TestCase("[::1]")]
        public void AnEndpointIsRefusedWhereASocketCouldNotUseIt(String Written)
        {

            // An unbracketed IPv6 literal is the one that matters: "fe80::1:15118"
            // is a perfectly good address in its own right, so splitting it at
            // the last colon would connect somewhere else entirely.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("connect", Written)),
                                                      out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("session.connect"));

        }

        #endregion

        #region AFieldLeftOutIsNotTheSameAsOneSetToNull()

        [Test]
        public void AFieldLeftOutIsNotTheSameAsOneSetToNull()
        {

            // Left out: nothing to say, and nothing to apply.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("renegotiate", true)),
                                                      out var silent, out var error),
                        Is.True, error);

            Assert.That(silent!.Cleared, Does.Not.Contain("connect"));

            // Explicitly null: take it back. Without the difference there is no
            // way back from having named a station.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("connect", JValue.CreateNull())),
                                                      out var cleared, out error),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(cleared!.Connect, Is.Null);
                Assert.That(cleared!.Cleared, Does.Contain("connect"));
            });

        }

        #endregion

        #region ClearingSurvivesBeingWrittenOut()

        [Test]
        public void ClearingSurvivesBeingWrittenOut()
        {

            // The null has to reach the file, or the next start would fall back
            // to whatever the constructor was handed rather than to the default.
            var json = new SessionConfiguration(
                           Cleared: new HashSet<String> { "connect", "tls" }
                       ).ToJSON();

            Assert.Multiple(() => {
                Assert.That(json["connect"]?.Type, Is.EqualTo(JTokenType.Null));
                Assert.That(json["tls"]?.Type,     Is.EqualTo(JTokenType.Null));
            });

            Assert.That(SessionConfiguration.TryParse(json, out var read, out var error), Is.True, error);

            Assert.That(read!.Cleared, Is.EquivalentTo(new[] { "connect", "tls" }));

        }

        #endregion

        #region ASettingThatIsAlsoASecretIsNotOneOfTheseFields()

        [Test]
        public void ASettingThatIsAlsoASecretIsNotOneOfTheseFields()
        {

            // The point of the split: a password cannot be written to the
            // configuration file because there is no field on this record that
            // could carry one there. Checked as a property of the type rather
            // than trusted as a habit.
            var written = new SessionConfiguration(
                              VehicleCertificate:   "vehicle.p12",
                              ContractCertificate:  "contract.p12",
                              OEMCertificate:       "oem.p12",
                              TariffCertificate:    "tariff.p12"
                          ).ToJSON();

            foreach (var property in written.Properties())
                Assert.That(property.Name.ToLowerInvariant(), Does.Not.Contain("pass"),
                            $"'{property.Name}' looks like it could carry a password into the configuration file.");

            Assert.That(written.Properties().Count(), Is.EqualTo(4));

        }

        #endregion

        #region EveryClearableFieldIsAFieldThisSectionHas()

        [Test]
        public void EveryClearableFieldIsAFieldThisSectionHas()
        {

            // A name in Clearable that nothing ever writes would be a setting
            // somebody could ask to clear and never see change.
            var everything = new SessionConfiguration(
                                 Connect:                       "[::1]:15118",
                                 Protocol:                      ProtocolVariant.Iso15118_20,
                                 OfferBoth:                     false,
                                 Mode:                          PowerMode.Dc,
                                 MCS:                           false,
                                 TLS:                           TlsStack.Dotnet,
                                 PKIDirectory:                  "pki",
                                 VehicleCertificate:            "vehicle.p12",
                                 TrustRoots:                    "roots",
                                 ContractCertificate:           "contract.p12",
                                 OEMCertificate:                "oem.p12",
                                 TariffCertificate:             "tariff.p12",
                                 TargetEnergy_kWh:              10,
                                 MaxChargingTime:               TimeSpan.FromMinutes(10),
                                 DepartureIn:                   TimeSpan.FromMinutes(20),
                                 MinimumStateOfCharge_percent:  50,
                                 Renegotiate:                   false,
                                 SLACPeer:                      "127.0.0.1:9000"
                             ).ToJSON();

            foreach (var field in SessionConfiguration.Clearable)
                Assert.That(everything.ContainsKey(field), Is.True,
                            $"'{field}' is listed as clearable but is never written.");

        }

        #endregion

    }

}
