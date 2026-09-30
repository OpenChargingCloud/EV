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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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

    }

}
