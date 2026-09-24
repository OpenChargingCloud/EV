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
using cloud.charging.open.protocols.WWCP.node;
using cloud.charging.open.protocols.WWCP.node.logging;
using NUnit.Framework;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What may go into the certificate store, what may not, and what survives
    /// a restart.
    /// </summary>
    /// <remarks>
    /// Every certificate here is minted in the test rather than committed to
    /// the repository. A fixture certificate expires, and a test suite that
    /// starts failing on a Tuesday for that reason teaches everybody to ignore
    /// it.
    /// </remarks>
    public class CertificateStoreTests
    {

        #region Data

        private String    directory = "";
        private EventLog  log       = new ();

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory  = Path.Combine(Path.GetTempPath(), "ev-certificate-store-" + Guid.NewGuid().ToString("N")[..12]);
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


        #region (helpers) Root(Name) / Leaf(Name, Issuer) / Pkcs12(Certificate, Password)

        /// <summary>
        /// A self-signed certificate, which is what a trust anchor is.
        /// </summary>
        private static X509Certificate2 Root(String    Name,
                                             Int32     ValidForDays   = 3650,
                                             Int32     StartsInDays   = -1)
        {

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP521);

            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA512);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            return request.CreateSelfSigned(
                       DateTimeOffset.UtcNow.AddDays(StartsInDays),
                       DateTimeOffset.UtcNow.AddDays(StartsInDays + ValidForDays)
                   );

        }

        /// <summary>
        /// A certificate somebody else signed, with its own private key.
        /// </summary>
        private static X509Certificate2 Leaf(String            Name,
                                             X509Certificate2  Issuer)
        {

            var key = ECDsa.Create(ECCurve.NamedCurves.nistP521);

            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA512);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            var signed = request.Create(
                             Issuer,
                             DateTimeOffset.UtcNow.AddDays(-1),
                             DateTimeOffset.UtcNow.AddDays(365),
                             Guid.NewGuid().ToByteArray()
                         );

            return signed.CopyWithPrivateKey(key);

        }

        /// <summary>The bytes of a PKCS#12, with or without a password.</summary>
        private static Byte[] Pkcs12(X509Certificate2  Certificate,
                                     String?           Password = null)

            => Certificate.Export(X509ContentType.Pkcs12, Password)
                   ?? throw new InvalidOperationException("Could not export a PKCS#12.");

        /// <summary>The bytes of a PEM certificate.</summary>
        private static Byte[] Pem(X509Certificate2 Certificate)

            => System.Text.Encoding.ASCII.GetBytes(Certificate.ExportCertificatePem());

        #endregion



        #region (helpers) PemWithKey(Key, Certificates) / EncryptedPemWithKey(...)

        /// <summary>One PEM holding certificates and an unencrypted key, the way most tools write one.</summary>
        private static Byte[] PemWithKey(ECDsa                     Key,
                                         String                    KeyLabel,
                                         params X509Certificate2[] Certificates)
        {

            var text = String.Concat(Certificates.Select(one => one.ExportCertificatePem() + Environment.NewLine));

            var der  = KeyLabel == "EC PRIVATE KEY"
                           ? Key.ExportECPrivateKey()
                           : Key.ExportPkcs8PrivateKey();

            return System.Text.Encoding.ASCII.GetBytes(text + new String(PemEncoding.Write(KeyLabel, der)) + Environment.NewLine);

        }

        /// <summary>The same, with the key encrypted under a password.</summary>
        private static Byte[] EncryptedPemWithKey(ECDsa             Key,
                                                  String            Password,
                                                  X509Certificate2  Certificate)
        {

            var der = Key.ExportEncryptedPkcs8PrivateKey(
                          Password,
                          new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)
                      );

            return System.Text.Encoding.ASCII.GetBytes(
                       Certificate.ExportCertificatePem() + Environment.NewLine +
                       new String(PemEncoding.Write("ENCRYPTED PRIVATE KEY", der)) + Environment.NewLine
                   );

        }

        /// <summary>A certificate and the key that goes with it, kept apart so a PEM can be built by hand.</summary>
        private static (X509Certificate2 Certificate, ECDsa Key) LeafWithKey(String            Name,
                                                                             X509Certificate2  Issuer)
        {

            var key     = ECDsa.Create(ECCurve.NamedCurves.nistP521);
            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA512);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            var signed = request.Create(
                             Issuer,
                             DateTimeOffset.UtcNow.AddDays(-1),
                             DateTimeOffset.UtcNow.AddDays(365),
                             Guid.NewGuid().ToByteArray()
                         );

            return (signed, key);

        }

        #endregion

        #region APemMayCarryItsKey()

        [Test]
        [TestCase("EC PRIVATE KEY")]
        [TestCase("PRIVATE KEY")]
        public void APemMayCarryItsKey(String KeyLabel)
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An MO Root");
            var (leaf, key) = LeafWithKey("A Contract", root);

            using (leaf)
            using (key)
            {

                Assert.That(store.Import(PemWithKey(key, KeyLabel, leaf), CertificateKind.Contract, null, null,
                                         out var entry, out var error),
                            Is.True, error);

                Assert.Multiple(() => {
                    Assert.That(entry!.HasPrivateKey,  Is.True, "the key in the file is the whole point");
                    Assert.That(entry!.Label,          Is.EqualTo("A Contract"));
                    Assert.That(entry!.KeyAlgorithm,   Is.EqualTo("ECDSA P-521"));
                });

                // And it is still there after a restart, which is what says the
                // key survived being written out and read back.
                var again = new CertificateStore(directory, log);
                again.Reload();

                Assert.That(again.Get(entry!.Id)?.HasPrivateKey, Is.True);

            }

        }

        #endregion

        #region AnEncryptedKeyInAPemIsOpenedWithItsPassword()

        [Test]
        public void AnEncryptedKeyInAPemIsOpenedWithItsPassword()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An MO Root");
            var (leaf, key) = LeafWithKey("A Contract", root);

            using (leaf)
            using (key)
            {

                var pem = EncryptedPemWithKey(key, "opensesame", leaf);

                Assert.That(store.Import(pem, CertificateKind.Contract, "opensesame", null, out var entry, out var error),
                            Is.True, error);

                Assert.That(entry!.HasPrivateKey, Is.True);

            }

        }

        #endregion

        #region AnEncryptedKeyWithoutItsPasswordSaysSo()

        [Test]
        public void AnEncryptedKeyWithoutItsPasswordSaysSo()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An MO Root");
            var (leaf, key) = LeafWithKey("A Contract", root);

            using (leaf)
            using (key)
            {

                var pem = EncryptedPemWithKey(key, "opensesame", leaf);

                Assert.That(store.Import(pem, CertificateKind.Contract, null, null, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("password"));

                Assert.That(store.Import(pem, CertificateKind.Contract, "wrong", null, out _, out var error2), Is.False);
                Assert.That(error2, Does.Contain("password"));

            }

        }

        #endregion

        #region TheLeafIsFoundWhereverItWasWritten()

        [Test]
        public void TheLeafIsFoundWhereverItWasWritten()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An MO Root");
            var (leaf, key) = LeafWithKey("A Contract", root);

            using (leaf)
            using (key)
            {

                // Root first, leaf second - which is how plenty of tools write a
                // chain, and the opposite of what "take the first one" assumes.
                Assert.That(store.Import(PemWithKey(key, "PRIVATE KEY", root, leaf),
                                         CertificateKind.Contract, null, null,
                                         out var entry, out var error),
                            Is.True, error);

                Assert.Multiple(() => {
                    Assert.That(entry!.Label,          Is.EqualTo("A Contract"), "the one holding the key is the leaf");
                    Assert.That(entry!.HasPrivateKey,  Is.True);
                    Assert.That(entry!.ChainLength,    Is.EqualTo(1), "and the root travelled with it as a sub-CA");
                });

            }

        }

        #endregion

        #region AKeyThatBelongsToNothingInTheFileIsRefused()

        [Test]
        public void AKeyThatBelongsToNothingInTheFileIsRefused()
        {

            var store = new CertificateStore(directory, log);

            using var root      = Root("An MO Root");
            var (leaf, _)       = LeafWithKey("A Contract", root);
            using var stranger  = ECDsa.Create(ECCurve.NamedCurves.nistP521);

            using (leaf)
            {

                Assert.That(store.Import(PemWithKey(stranger, "PRIVATE KEY", leaf),
                                         CertificateKind.Contract, null, null,
                                         out _, out var error),
                            Is.False, "a key for a certificate that is not in the file is not a credential");

                Assert.That(error, Does.Contain("belongs to none"));

            }

        }

        #endregion

        #region APemWithoutAKeyIsStillARoot()

        [Test]
        public void APemWithoutAKeyIsStillARoot()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            // The pairing must not have changed what a plain certificate does.
            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out var entry, out var error),
                        Is.True, error);

            Assert.That(entry!.HasPrivateKey, Is.False);

        }

        #endregion

        #region ARootGoesIn()

        [Test]
        public void ARootGoesIn()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out var entry, out var error),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(entry!.Kind,           Is.EqualTo(CertificateKind.V2GRoot));
                Assert.That(entry!.Label,          Is.EqualTo("A V2G Root"));
                Assert.That(entry!.IsActive,       Is.True,  "a certificate somebody just imported is switched on");
                Assert.That(entry!.HasPrivateKey,  Is.False, "a trust anchor has no private key");
                Assert.That(entry!.Id.Length,      Is.EqualTo(CertificateEntry.IdLength));
                Assert.That(entry!.Thumbprint,     Does.StartWith(entry!.Id), "the handle is the head of the fingerprint");
                Assert.That(File.Exists(store.FullPath(entry!)), Is.True, "the file was copied in");
            });

        }

        #endregion

        #region TheSameCertificateTwiceIsOneEntry()

        [Test]
        public void TheSameCertificateTwiceIsOneEntry()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null,     out var first,  out var error1), Is.True, error1);
            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, "Renamed", out var second, out var error2), Is.True, error2);

            Assert.Multiple(() => {
                Assert.That(second!.Id,                  Is.EqualTo(first!.Id));
                Assert.That(store.Entries.Count,         Is.EqualTo(1), "importing the same file twice is not two certificates");
                Assert.That(second!.Label,               Is.EqualTo("Renamed"), "and the second import may rename it");
            });

        }

        #endregion

        #region ASubCAIsNotARoot()

        [Test]
        public void ASubCAIsNotARoot()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");
            using var sub  = Leaf("A Sub-CA", root);

            // Refused rather than accepted-and-useless: CustomRootTrust needs the
            // chain to end in a self-signed certificate that is in the store, so a
            // sub-CA imported as a root would simply never anchor anything.
            Assert.That(store.Import(Pem(sub), CertificateKind.V2GRoot, null, null, out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("sub-CA"));

        }

        #endregion

        #region ATrustAnchorMayNotCarryAPrivateKey()

        [Test]
        public void ATrustAnchorMayNotCarryAPrivateKey()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(store.Import(Pkcs12(root), CertificateKind.V2GRoot, null, null, out _, out var error),
                        Is.False, "a CA key in a trust store is somebody's CA key in the wrong place");

            Assert.That(error, Does.Contain("private key"));

        }

        #endregion

        #region ACredentialWithoutItsKeyIsRefused()

        [Test]
        public void ACredentialWithoutItsKeyIsRefused()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An OEM Root");
            using var leaf = Leaf("A Vehicle", root);

            Assert.That(store.Import(Pem(leaf), CertificateKind.Vehicle, null, null, out _, out var error),
                        Is.False, "a certificate that cannot be proven cannot be presented");

            Assert.That(error, Does.Contain("private key"));

        }

        #endregion

        #region AProtectedPkcs12IsStoredWithoutItsPassword()

        [Test]
        public void AProtectedPkcs12IsStoredWithoutItsPassword()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An OEM Root");
            using var leaf = Leaf("A Vehicle", root);

            Assert.That(store.Import(Pkcs12(leaf, "opensesame"), CertificateKind.Vehicle, "opensesame", null,
                                     out var entry, out var error),
                        Is.True, error);

            Assert.That(entry!.HasPrivateKey, Is.True);

            // The whole point of the decision: what is on disk opens with no
            // password at all, which is what the session's loaders then do.
            var reread = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                             store.FullPath(entry!),
                             (String?) null,
                             X509KeyStorageFlags.EphemeralKeySet
                         );

            Assert.Multiple(() => {
                Assert.That(reread.Count,                     Is.GreaterThan(0));
                Assert.That(reread[0].HasPrivateKey,          Is.True, "the key came along");
                Assert.That(CertificateEntry.ThumbprintOf(reread[0]), Is.EqualTo(entry!.Thumbprint));
            });

        }

        #endregion

        #region TheWrongPasswordIsSaidToBeThat()

        [Test]
        public void TheWrongPasswordIsSaidToBeThat()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("An OEM Root");
            using var leaf = Leaf("A Vehicle", root);

            Assert.That(store.Import(Pkcs12(leaf, "opensesame"), CertificateKind.Vehicle, "wrong", null,
                                     out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("password"));

        }

        #endregion

        #region SwitchingOffKeepsItAndDeletingDoesNot()

        [Test]
        public void SwitchingOffKeepsItAndDeletingDoesNot()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out var entry, out var error),
                        Is.True, error);

            var path = store.FullPath(entry!);

            Assert.That(store.SetActive(entry!.Id, false, out var off, out var error2), Is.True, error2);

            Assert.Multiple(() => {
                Assert.That(off!.IsActive,        Is.False);
                Assert.That(off!.IsUsable,        Is.False, "a certificate that is switched off is not used");
                Assert.That(File.Exists(path),    Is.True,  "and switching off does not take the file away");
                Assert.That(store.Entries.Count,  Is.EqualTo(1));
            });

            Assert.That(store.Remove(entry!.Id, out var error3), Is.True, error3);

            Assert.Multiple(() => {
                Assert.That(store.Entries,      Is.Empty);
                Assert.That(File.Exists(path),  Is.False, "deleting takes the file with it - a key left behind would be believed gone");
            });

        }

        #endregion

        #region AnExpiredCertificateIsActiveAndNotUsable()

        [Test]
        public void AnExpiredCertificateIsActiveAndNotUsable()
        {

            var store = new CertificateStore(directory, log);

            // Valid for a day, starting ten days ago: over.
            using var root = Root("An Old Root", ValidForDays: 1, StartsInDays: -10);

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out var entry, out var error),
                        Is.True, error, "an expired certificate may be imported - knowing it is there is the point");

            Assert.Multiple(() => {
                Assert.That(entry!.IsActive,  Is.True,  "somebody switches a certificate on; time switches it off");
                Assert.That(entry!.IsExpired, Is.True);
                Assert.That(entry!.IsUsable,  Is.False);
            });

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

        #region OneCertificateHasOnePurpose()

        [Test]
        public void OneCertificateHasOnePurpose()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A Root");

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out _, out var error), Is.True, error);

            Assert.That(store.Import(Pem(root), CertificateKind.MORoot, null, null, out _, out var error2), Is.False,
                        "the same certificate cannot be two trust anchors at once");

            Assert.That(error2, Does.Contain("v2gRoot"));

        }

        #endregion

        #region AFileSomebodyCopiedInIsAdopted()

        [Test]
        public void AFileSomebodyCopiedInIsAdopted()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A Copied Root");

            // Put there by hand, which on a machine somebody already has a
            // shell on is the shorter way to install a certificate.
            File.WriteAllBytes(
                Path.Combine(directory, "roots", "v2g", "copied.pem"),
                Pem(root)
            );

            store.Reload();

            var adopted = store.ByKind(CertificateKind.V2GRoot);

            Assert.Multiple(() => {
                Assert.That(adopted,               Has.Exactly(1).Items);
                Assert.That(adopted[0].Label,      Is.EqualTo("A Copied Root"));
                Assert.That(adopted[0].IsActive,   Is.True, "somebody put it there, so it is taken as meant");
                Assert.That(adopted[0].FileName,   Is.EqualTo("roots/v2g/copied.pem"));
            });

        }

        #endregion

        #region AFileThatWentAwayIsDropped()

        [Test]
        public void AFileThatWentAwayIsDropped()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, null, out var entry, out var error),
                        Is.True, error);

            File.Delete(store.FullPath(entry!));

            store.Reload();

            Assert.That(store.Entries, Is.Empty,
                        "an entry pointing at a missing file would fail at a handshake instead of here");

        }

        #endregion

        #region LabelAndSwitchSurviveARestart()

        [Test]
        public void LabelAndSwitchSurviveARestart()
        {

            var first = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(first.Import(Pem(root), CertificateKind.V2GRoot, null, "What we call it", out var entry, out var error),
                        Is.True, error);

            Assert.That(first.SetActive(entry!.Id, false, out _, out var error2), Is.True, error2);

            // A second store over the same directory is what the next start is.
            var second = new CertificateStore(directory, log);
            second.Reload();

            var again = second.Get(entry!.Id);

            Assert.Multiple(() => {
                Assert.That(again,            Is.Not.Null);
                Assert.That(again!.Label,     Is.EqualTo("What we call it"), "the index remembers what a file cannot say");
                Assert.That(again!.IsActive,  Is.False);
            });

        }

        #endregion

        #region ALabelTakenBackIsTheCertificatesOwnName()

        [Test]
        public void ALabelTakenBackIsTheCertificatesOwnName()
        {

            var store = new CertificateStore(directory, log);

            using var root = Root("A V2G Root");

            Assert.That(store.Import(Pem(root), CertificateKind.V2GRoot, null, "Something else", out var entry, out var error),
                        Is.True, error);

            Assert.That(store.Relabel(entry!.Id, "  ", out var back, out var error2), Is.True, error2);

            Assert.That(back!.Label, Is.EqualTo("A V2G Root"),
                        "an emptied label is the certificate's own name again, not an empty one");

        }

        #endregion

        #region SomethingThatIsNotACertificateIsSaidToBeThat()

        [Test]
        public void SomethingThatIsNotACertificateIsSaidToBeThat()
        {

            var store = new CertificateStore(directory, log);

            Assert.That(store.Import("hello, this is not a certificate"u8.ToArray(),
                                     CertificateKind.V2GRoot, null, null, out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("certificate"));

            Assert.That(store.Import([], CertificateKind.V2GRoot, null, null, out _, out var error2), Is.False);
            Assert.That(error2, Does.Contain("nothing"));

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
