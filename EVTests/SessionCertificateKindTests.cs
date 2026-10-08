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
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// A session that names a certificate of another kind than its slot wants
    /// is refused where that is set, in a sentence that says what the
    /// certificate is and what the slot wants.
    /// </summary>
    public class SessionCertificateKindTests
    {

        #region Data

        private String  directory  = "";
        private EV?     vehicle;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public async Task Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-session-certificate-kind-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

            var file   = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, """{ "nts": { "enabled": false } }""");

            vehicle    = await TestPorts.StartedOnFreshPorts(() => new EV(
                             HTTPPort:          IPPort.Parse(TestPorts.Free()),
                             AccountsPath:      Path.Combine(directory, "accounts"),
                             ConfigFile:        new WWCPConfigFile(file),
                             CertificatesPath:  Path.Combine(directory, "certificates"),
                             LogToConsole:      false,
                             BridgeDebugLog:    false
                         ));

        }

        [TearDown]
        public async Task TearDown()
        {

            if (vehicle is not null)
                await vehicle.DisposeAsync();

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // A temporary directory that outlives one test run is not worth
                // failing the run over.
            }

        }

        #endregion


        #region (helper) Root(Name)

        /// <summary>
        /// A self-signed root as PEM, valid from yesterday for a month.
        /// </summary>
        private static Byte[] Root(String Name)
        {

            using var key   = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request     = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            using var root  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                       DateTimeOffset.UtcNow.AddDays(30));

            return System.Text.Encoding.ASCII.GetBytes(root.ExportCertificatePem());

        }

        #endregion


        #region ACertificateOfAnotherKindIsRefusedSayingWhatEachIs(Field, Wanted)

        /// <summary>
        /// An OEM root, named for a slot that wants a certificate of another
        /// kind: the refusal says each with the article its name is said with.
        /// With "a" and the name a kind has on the command line, it said
        /// "'...' is a oemRoot and this names a contract", and for the OEM
        /// provisioning certificate's slot "a oemProvisioning".
        /// </summary>
        [TestCase("vehicleCertificate",   "a vehicle certificate")]
        [TestCase("contractCertificate",  "a contract certificate")]
        [TestCase("oemCertificate",       "an OEM provisioning certificate")]
        [TestCase("tariffCertificate",    "a tariff certificate")]
        public void ACertificateOfAnotherKindIsRefusedSayingWhatEachIs(String  Field,
                                                                       String  Wanted)
        {

            Assert.That(vehicle!.Certificates.Import(Root("EV test root"), CertificateKind.OEMRoot, null, "EV test root",
                                                     out var entry, out var error),
                        Is.True, error);

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { [Field] = entry!.Id }, out var said),
                        Is.False, "a certificate of another kind was taken");

            Assert.That(said, Is.EqualTo($"'session.{Field}': 'EV test root' is an OEM root and this names {Wanted}."));

        }

        #endregion

        #region (helper) Credential(Name)

        /// <summary>
        /// A self-signed certificate that is no CA, with its private key after
        /// it, as PEM - valid from yesterday for a month.
        /// </summary>
        private static Byte[] Credential(String Name)
        {

            using var key         = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request           = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            using var credential  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                             DateTimeOffset.UtcNow.AddDays(30));

            return System.Text.Encoding.ASCII.GetBytes(credential.ExportCertificatePem() + "\n" +
                                                       key.ExportPkcs8PrivateKeyPem()     + "\n");

        }

        #endregion


        #region ACertificateKeptAsSeveralKindsIsChosenAsTheKindItsSlotWants()

        /// <summary>
        /// One certificate kept as a vehicle and as a contract certificate,
        /// chosen for the contract's slot. The store hands it out by its
        /// handle alone as the first kind it is kept as, the vehicle
        /// certificate: asked for that way, it was refused as "a vehicle
        /// certificate and this names a contract certificate", and the session
        /// configuration said it was not usable there.
        /// </summary>
        [Test]
        public void ACertificateKeptAsSeveralKindsIsChosenAsTheKindItsSlotWants()
        {

            Assert.That(vehicle!.Certificates.Import(Credential("EV test credential"), null, "EV test credential",
                                                     [ new CertificateRegistration(CertificateKind.Vehicle),
                                                       new CertificateRegistration(CertificateKind.Contract) ],
                                                     out var entries, out var error, out _),
                        Is.True, error);

            var id = entries![0].Id;

            Assert.That(vehicle.Certificates.Get(id)!.Kind, Is.EqualTo(CertificateKind.Vehicle), "the first kind it is kept as");

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { ["contractCertificate"] = id }, out var said),
                        Is.True, said);

            var chosen = vehicle.SessionConfigurationJSON()["certificates"]!["contractCertificate"]!;

            Assert.That(chosen["id"]!.    Value<String>(),  Is.EqualTo(id));
            Assert.That(chosen["usable"]!.Value<Boolean>(), Is.True, "usable as the contract certificate it is kept as");

        }

        #endregion

        #region ACertificateKeptAsAnotherKindOnlyIsRefusedAndNotUsable()

        /// <summary>
        /// The same, chosen for the contract's slot and then taken out of the
        /// store as a contract certificate, so that it is kept as a vehicle
        /// certificate only: refused for that slot, saying what it is, and
        /// the setting already made is told as not usable there.
        /// </summary>
        [Test]
        public void ACertificateKeptAsAnotherKindOnlyIsRefusedAndNotUsable()
        {

            Assert.That(vehicle!.Certificates.Import(Credential("EV test credential"), null, "EV test credential",
                                                     [ new CertificateRegistration(CertificateKind.Vehicle),
                                                       new CertificateRegistration(CertificateKind.Contract) ],
                                                     out var entries, out var error, out _),
                        Is.True, error);

            var id = entries![0].Id;

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { ["contractCertificate"] = id }, out var said),
                        Is.True, said);

            Assert.That(vehicle.Certificates.Remove(id, CertificateKind.Contract, out var removeError),
                        Is.True, removeError);

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { ["contractCertificate"] = id }, out said),
                        Is.False, "a certificate no longer kept as a contract certificate was taken");

            Assert.That(said, Is.EqualTo("'session.contractCertificate': 'EV test credential' is a vehicle certificate " +
                                         "and this names a contract certificate."));

            var chosen = vehicle.SessionConfigurationJSON()["certificates"]!["contractCertificate"]!;

            Assert.That(chosen["missing"]!.Value<Boolean>(), Is.False, "it is still in the store");
            Assert.That(chosen["usable"]!. Value<Boolean>(), Is.False, "but not as a contract certificate");

        }

        #endregion

        #region ACertificateChosenAsOneKindGoesAsAnotherAndIsMarkedAsTheOneChosen()

        /// <summary>
        /// One certificate kept as a vehicle and as a contract certificate,
        /// chosen for the contract's slot: the store says it is chosen as the
        /// contract certificate, and it may be taken out as the vehicle
        /// certificate but not as the contract certificate. Asked by its handle
        /// alone, it was refused as either - and marked chosen in both rows.
        /// </summary>
        [Test]
        public async Task ACertificateChosenAsOneKindGoesAsAnotherAndIsMarkedAsTheOneChosen()
        {

            Assert.That(vehicle!.Certificates.Import(Credential("EV test credential"), null, "EV test credential",
                                                     [ new CertificateRegistration(CertificateKind.Vehicle),
                                                       new CertificateRegistration(CertificateKind.Contract) ],
                                                     out var entries, out var error, out _),
                        Is.True, error);

            var id = entries![0].Id;

            Assert.That(vehicle.TryUpdateSessionConfiguration(new JObject { ["contractCertificate"] = id }, out var said),
                        Is.True, said);

            using var client = new HttpClient {
                                   BaseAddress  = new Uri($"http://127.0.0.1:{vehicle.HTTPPort}/"),
                                   Timeout      = TimeSpan.FromSeconds(30)
                               };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                             "Basic",
                                                             Convert.ToBase64String(Encoding.UTF8.GetBytes($"root:{vehicle.GeneratedPassword}"))
                                                         );

            var store    = JObject.Parse(await client.GetStringAsync("api/v1/certificates"));
            var chosen   = store["chosen"]!;

            using var asContract  = await client.DeleteAsync($"api/v1/certificates/{id}?kind=contract");
            using var asVehicle   = await client.DeleteAsync($"api/v1/certificates/{id}?kind=vehicle");

            Assert.Multiple(() => {

                Assert.That(chosen["contractCertificate"]!["id"]!.  Value<String>(),  Is.EqualTo(id));
                Assert.That(chosen["contractCertificate"]!["kind"]!.Value<String>(),  Is.EqualTo("contract"), "chosen as the kind its slot wants");
                Assert.That(chosen["vehicleCertificate"]!.Type,                        Is.EqualTo(JTokenType.Null));

                Assert.That(asContract.StatusCode,  Is.EqualTo(HttpStatusCode.Conflict), "what the session names");
                Assert.That(asVehicle. StatusCode,  Is.EqualTo(HttpStatusCode.OK),       "not what the session names");

                Assert.That(vehicle.Certificates.Get(id, CertificateKind.Vehicle),   Is.Null,     "gone as the vehicle certificate");
                Assert.That(vehicle.Certificates.Get(id, CertificateKind.Contract),  Is.Not.Null, "kept as the contract certificate");

            });

        }

        #endregion

    }

}
