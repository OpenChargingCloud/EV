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

using cloud.charging.open.protocols.ISO15118.T1S.Transport;

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
                              VehicleCertificate:   "a1b2c3d4e5f60718",
                              ContractCertificate:  "b1b2c3d4e5f60718",
                              OEMCertificate:       "c1b2c3d4e5f60718",
                              TariffCertificate:    "d1b2c3d4e5f60718"
                          ).ToJSON();

            foreach (var property in written.Properties())
                Assert.That(property.Name.ToLowerInvariant(), Does.Not.Contain("pass"),
                            $"'{property.Name}' looks like it could carry a password into the configuration file.");

            Assert.That(written.Properties().Count(), Is.EqualTo(4));

        }

        #endregion

        #region TrustRootsIsRefusedByName()

        [Test]
        public void TrustRootsIsRefusedByName()
        {

            // Refused rather than ignored. A field this section no longer knows
            // would otherwise be passed over in silence, and somebody whose file
            // was written before the store existed would get a vehicle that
            // trusts no station and says nothing about why.
            Assert.That(SessionConfiguration.TryParse(
                            new JObject(new JProperty("trustRoots", "/etc/v2g/roots")),
                            out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("certificate store"));

        }

        #endregion

        #region APathWhereAHandleBelongsIsSaidToBeThat()

        [Test]
        public void APathWhereAHandleBelongsIsSaidToBeThat()
        {

            foreach (var written in new[] { "/etc/v2g/contract.p12", @"C:\certs\contract.p12", "contract.p12" })
            {

                Assert.That(SessionConfiguration.TryParse(
                                new JObject(new JProperty("contractCertificate", written)),
                                out _, out var error),
                            Is.False, $"'{written}' is a path and should be refused as one");

                Assert.That(error, Does.Contain("store"));

            }

        }

        #endregion

        #region AHandleIsTakenAsOne()

        [Test]
        public void AHandleIsTakenAsOne()
        {

            Assert.That(SessionConfiguration.TryParse(
                            new JObject(new JProperty("contractCertificate", "a1b2c3d4e5f60718")),
                            out var configuration, out var error),
                        Is.True, error);

            Assert.That(configuration!.ContractCertificate, Is.EqualTo("a1b2c3d4e5f60718"));

        }

        #endregion

        #region The bus: t1sTransport, t1sBus, t1sInterface, t1sWeight

        [Test]
        public void TheBusIsWrittenAndReadBack()
        {

            var written = new SessionConfiguration(
                              T1SBus:        "239.151.18.1:2354",
                              T1STransport:  T1STransportKind.AfPacket,
                              T1SInterface:  "eth1",
                              T1SWeight:     5
                          ).ToJSON();

            Assert.That(written.Value<String>("t1sTransport"),  Is.EqualTo("afpacket"));
            Assert.That(written.Value<String>("t1sBus"),        Is.EqualTo("239.151.18.1:2354"));
            Assert.That(written.Value<String>("t1sInterface"),  Is.EqualTo("eth1"));
            Assert.That(written.Value<Int32> ("t1sWeight"),     Is.EqualTo(5));

            Assert.That(SessionConfiguration.TryParse(written, out var read, out var error), Is.True, error);

            Assert.That(read!.T1STransport,          Is.EqualTo(T1STransportKind.AfPacket));
            Assert.That(read.T1STransportInEffect,   Is.EqualTo(T1STransportKind.AfPacket));
            Assert.That(read.T1SBusEndpoint,         Is.Not.Null);
            Assert.That(read.T1SBusEndpoint!.Port,   Is.EqualTo(2354));
            Assert.That(read.T1SInterface,           Is.EqualTo("eth1"));
            Assert.That(read.T1SWeight,              Is.EqualTo(5));
            Assert.That(read.T1SWeightInEffect,      Is.EqualTo(5));

        }

        [Test]
        public void NothingSaidMeansNoBus()
        {

            Assert.That(SessionConfiguration.TryParse(new JObject(), out var read, out var error), Is.True, error);

            Assert.That(read!.T1STransport,          Is.Null);
            Assert.That(read.T1STransportInEffect,   Is.EqualTo(T1STransportKind.None));
            Assert.That(read.T1SWeightInEffect,      Is.EqualTo(SessionConfiguration.DefaultT1SWeight));
            Assert.That(read.T1SBusEndpoint,         Is.Null);

        }

        [Test]
        public void ABusAloneMeansTheEmulatedMedium()
        {

            // A group is a thing only the emulated medium has, so naming one
            // says which medium without a second field - which is what a bench
            // that only knows its group is entitled to.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("t1sBus", "239.151.18.1:16118")),
                                                      out var read, out var error),
                        Is.True, error);

            Assert.That(read!.T1STransport,          Is.Null, "the file did not say");
            Assert.That(read.T1STransportInEffect,   Is.EqualTo(T1STransportKind.UDP));

        }

        [Test]
        [TestCase("none",      T1STransportKind.None)]
        [TestCase("Auto",      T1STransportKind.Auto)]
        [TestCase("AF_PACKET", T1STransportKind.AfPacket)]
        [TestCase("udp",       T1STransportKind.UDP)]
        public void EveryTransportWordIsRead(String Written, T1STransportKind Expected)
        {

            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("t1sTransport", Written)),
                                                      out var read, out var error),
                        Is.True, error);

            Assert.That(read!.T1STransport,  Is.EqualTo(Expected));

        }

        [Test]
        public void AnUnknownTransportIsRefusedByName()
        {

            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("t1sTransport", "pcap")),
                                                      out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("session.t1sTransport"));
            Assert.That(error, Does.Contain("afpacket"));

        }

        [Test]
        [TestCase("127.0.0.1:2354",         "not a group")]
        [TestCase("239.151.18.1",           "no port")]
        [TestCase("[ff02::1]:2354",         "IPv6")]
        [TestCase("bus.example.org:2354",   "a name")]
        [TestCase("239.151.18.1:0",         "port zero")]
        public void ABusThatIsNotAnIPv4GroupIsRefused(String Written, String Why)
        {

            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("t1sBus", Written)),
                                                      out _, out var error),
                        Is.False, Why);

            Assert.That(error, Does.Contain("session.t1sBus"));
            Assert.That(error, Does.Contain("multicast"));

        }

        [Test]
        [TestCase(0)]
        [TestCase(9)]
        public void AWeightOutsideTheBusIsRefused(Int32 Written)
        {

            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("t1sWeight", Written)),
                                                      out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("session.t1sWeight"));

        }

        [Test]
        public void AnInterfaceOfSpacesIsNothingNamed()
        {

            // The reader's convention for every string here: blank is the
            // same as absent, and absent takes the default - the V2G
            // interface for an adapter, the operating system's pick for udp.
            Assert.That(SessionConfiguration.TryParse(new JObject(new JProperty("t1sInterface", "   ")),
                                                      out var read, out var error),
                        Is.True, error);

            Assert.That(read!.T1SInterface, Is.Null);

        }

        [Test]
        public void TheBusCanBeTakenBack()
        {

            // An explicit null on every one of the four is how a CCS bench is
            // made of an MCS one again.
            var json = new JObject(
                           new JProperty("t1sTransport",  JValue.CreateNull()),
                           new JProperty("t1sBus",        JValue.CreateNull()),
                           new JProperty("t1sInterface",  JValue.CreateNull()),
                           new JProperty("t1sWeight",     JValue.CreateNull())
                       );

            Assert.That(SessionConfiguration.TryParse(json, out var read, out var error), Is.True, error);

            Assert.That(read!.Cleared, Is.SupersetOf(new[] { "t1sTransport", "t1sBus", "t1sInterface", "t1sWeight" }));

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
                                 VehicleCertificate:            "a1b2c3d4e5f60718",
                                 ContractCertificate:           "b1b2c3d4e5f60718",
                                 OEMCertificate:                "c1b2c3d4e5f60718",
                                 TariffCertificate:             "d1b2c3d4e5f60718",
                                 TargetEnergy_kWh:              10,
                                 MaxChargingTime:               TimeSpan.FromMinutes(10),
                                 DepartureIn:                   TimeSpan.FromMinutes(20),
                                 MinimumStateOfCharge_percent:  50,
                                 Renegotiate:                   false,
                                 SLACPeer:                      "127.0.0.1:9000",
                                 T1SBus:                        "239.151.18.1:2354",
                                 T1STransport:                  T1STransportKind.UDP,
                                 T1SInterface:                  "eth1",
                                 T1SWeight:                     3
                             ).ToJSON();

            foreach (var field in SessionConfiguration.Clearable)
                Assert.That(everything.ContainsKey(field), Is.True,
                            $"'{field}' is listed as clearable but is never written.");

        }

        #endregion

    }

}
