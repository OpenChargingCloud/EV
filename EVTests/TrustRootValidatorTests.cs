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

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// The validator a kind of root gives a session: nothing where there are
    /// no usable roots, one per kind, and none for what is not a root.
    /// </summary>
    /// <remarks>
    /// The store is the node's and is tested with it; what is tested here is
    /// what the vehicle makes of the roots in it, which is a chain validator
    /// of ISO 15118's - and so the vehicle's business, not the node's.
    /// </remarks>
    public class TrustRootValidatorTests
    {

        #region Data

        private String    directory = "";
        private EventLog  log       = new ();

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory  = Path.Combine(Path.GetTempPath(), "ev-trust-roots-" + Guid.NewGuid().ToString("N")[..12]);
            log        = new EventLog();

        }

        [TearDown]
        public void TearDown()
        {

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


        #region (helpers) Root(Name, ...) / Pem(Certificate)

        /// <summary>
        /// A self-signed certificate, which is what a trust anchor is.
        /// </summary>
        private static X509Certificate2 Root(String  Name,
                                             Int32   ValidForDays   = 3650,
                                             Int32   StartsInDays   = -1)
        {

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP521);

            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA512);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            return request.CreateSelfSigned(
                       DateTimeOffset.UtcNow.AddDays(StartsInDays),
                       DateTimeOffset.UtcNow.AddDays(StartsInDays + ValidForDays)
                   );

        }

        /// <summary>The bytes of a PEM certificate.</summary>
        private static Byte[] Pem(X509Certificate2 Certificate)

            => System.Text.Encoding.ASCII.GetBytes(Certificate.ExportCertificatePem());

        #endregion


        #region AnExpiredRootVouchesForNothing()

        [Test]
        public void AnExpiredRootVouchesForNothing()
        {

            var store = new CertificateStore(directory, log);

            // Valid for a day, starting ten days ago: over.
            using var root = Root("An Old Root", ValidForDays: 1, StartsInDays: -10);

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out _, out var error),
                        Is.True, error, "an expired certificate may be imported - knowing it is there is the point");

            Assert.That(EV.ValidatorFor(store, CertificateKind.V2GRoot, log), Is.Null,
                        "an expired root vouches for nothing, so there is nothing to validate against");

        }

        #endregion

        #region NoRootsIsNotTheSameAsRootsThatTrustNobody()

        [Test]
        public void NoRootsIsNotTheSameAsRootsThatTrustNobody()
        {

            var store = new CertificateStore(directory, log);

            Assert.That(EV.ValidatorFor(store, CertificateKind.MORoot, log), Is.Null,
                        "null is what 'this vehicle was never told' looks like");

            using var root = Root("An MO Root");

            Assert.That(store.Import(Pem(root), CertificateKind.MORoot, null, null, out _, out var error), Is.True, error);

            Assert.That(EV.ValidatorFor(store, CertificateKind.MORoot, log), Is.Not.Null);

        }

        #endregion

        #region EachKindOfRootIsItsOwnAnswer()

        [Test]
        public void EachKindOfRootIsItsOwnAnswer()
        {

            var store = new CertificateStore(directory, log);

            using var v2g = Root("A V2G Root");
            using var mo  = Root("An MO Root");

            Assert.That(store.Import(Pem(v2g), CertificateKind.V2GRoot, null, null, out _, out var e1), Is.True, e1);
            Assert.That(store.Import(Pem(mo),  CertificateKind.MORoot,  null, null, out _, out var e2), Is.True, e2);

            var forV2G = EV.ValidatorFor(store, CertificateKind.V2GRoot, log);
            var forMO  = EV.ValidatorFor(store, CertificateKind.MORoot,  log);

            // The whole reason the three are kept apart: one bag would let the
            // OEM root vouch for a contract.
            Assert.Multiple(() => {
                Assert.That(forV2G!.RootSubjects, Has.Exactly(1).Items);
                Assert.That(forMO!. RootSubjects, Has.Exactly(1).Items);
                Assert.That(forV2G!.RootSubjects.First(), Does.Contain("A V2G Root"));
                Assert.That(forMO!. RootSubjects.First(), Does.Contain("An MO Root"));
                Assert.That(EV.ValidatorFor(store, CertificateKind.OEMRoot, log), Is.Null);
            });

        }

        #endregion

        #region ARootIsNotAskedToValidateWhatItIsNot()

        [Test]
        public void ARootIsNotAskedToValidateWhatItIsNot()
        {

            var store = new CertificateStore(directory, log);

            // A credential is not a trust anchor and has no validator to give.
            Assert.Throws<ArgumentException>(() => EV.ValidatorFor(store, CertificateKind.Contract, log));

        }

        #endregion

    }

}
