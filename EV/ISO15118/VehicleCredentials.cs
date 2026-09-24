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

using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;

using cloud.charging.open.protocols.ISO15118.Security;
using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso20;
using cloud.charging.open.protocols.ISO15118.Transport.BouncyCastle;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.EV.ISO15118
{

    /// <summary>
    /// The certificates a vehicle carries, turned from files on disk into the shapes a session needs.
    /// </summary>
    /// <remarks>
    /// The files arrive already chosen: <see cref="Certificates.CertificateStore"/> holds them, a session
    /// setting names one per role by its handle, and what reaches these methods is the path that handle
    /// resolved to. They are not interchangeable, and mixing them up produces failures that read like
    /// protocol bugs:
    ///
    /// <list type="bullet">
    ///   <item><b>Vehicle</b> - who this vehicle is. Presented in the TLS handshake, and for ISO 15118-20
    ///         what a station's resume binding is computed over.</item>
    ///   <item><b>contract</b> - who pays. Signs the authorization in Plug &amp; Charge instead of paying
    ///         externally.</item>
    ///   <item><b>OEM provisioning</b> - what the vehicle was born with, and the only identity it has
    ///         before it holds a contract.</item>
    ///   <item><b>tariff verification</b> - the public half of the pair a station signs its tariff with.
    ///         The only one with no private key, because this side only ever verifies.</item>
    /// </list>
    ///
    /// Each of the first three is also checked against the roots this vehicle believes, where it holds any
    /// of the right kind - see <see cref="Check"/>. That is this vehicle checking its own credentials, and
    /// it catches the certificate issued under a hierarchy this vehicle is not part of, the one whose
    /// sub-CA did not travel with it, and the one that was quietly replaced.
    ///
    /// The names come from the CharIN V2G second-generation PKI Certificate Policy rather than from
    /// ISO 15118 directly, which is deliberate: the certificates are ISO 15118's, and the Policy is
    /// explicit that ISO 15118-20's own naming is not consistent.
    ///
    /// Every failure here is an <see cref="ArgumentException"/> naming the setting it came from, because
    /// the alternative - a file that turns out to be the wrong kind halfway through a handshake - shows up
    /// as a station that appears to have hung up. No password is taken: the store opens a PKCS#12 once, at
    /// import, and keeps what it holds without one.
    /// </remarks>
    public static class VehicleCredentials
    {

        #region LoadContract(Path, MORoots, Log)

        /// <summary>
        /// The Plug &amp; Charge <b>contract</b> credentials: who pays.
        /// </summary>
        /// <param name="MORoots">
        /// What this contract has to chain to, or null where this vehicle has been told no Mobility
        /// Operator roots.
        /// </param>
        public static PncEvccOptions LoadContract(String              Path,
                                                  V2GChainValidator?  MORoots,
                                                  EventLog            Log)
        {

            var (leaf, subCertificates, key, subject) = Credentials.LoadChain(Path, null, "session.contractCertificate");

            Log.Info($"Plug & Charge: contract certificate {subject} (+{subCertificates.Length} sub-CA(s)), {key.KeySize}-bit EC.",
                     "15118", "pnc");

            Check(leaf, subCertificates, MORoots,
                  "Plug & Charge", "the contract certificate", "Mobility Operator", "moRoot",
                  "session.contractCertificate", Log, "pnc");

            return new PncEvccOptions(leaf, subCertificates, key);

        }

        #endregion

        #region LoadOEM(Path, OEMRoots, Log)

        /// <summary>
        /// The <b>OEM provisioning</b> credentials: what the vehicle was born with. With these the
        /// ISO 15118-20 session asks the station to issue a contract certificate before authorizing, and
        /// unwraps the private key it sends back.
        /// </summary>
        /// <remarks>
        /// The key has to be <b>P-521</b>: the unwrap is an ECDH against the station's ephemeral secp521r1
        /// key, so a -2-era P-256 OEM certificate takes part in an exchange it cannot finish. The station
        /// answers such a request with a well-formed response the vehicle then cannot decrypt, which is why
        /// the curve is checked here - where it can still be explained - rather than at the failure.
        /// </remarks>
        /// <param name="OEMRoots">
        /// What this provisioning chain has to end in, or null where this vehicle has been told no OEM
        /// roots.
        /// </param>
        public static CertInstallEvccOptions LoadOEM(String              Path,
                                                     V2GChainValidator?  OEMRoots,
                                                     EventLog            Log)
        {

            var (leaf, subCertificates, key, subject) = Credentials.LoadChain(Path, null, "session.oemCertificate", exportable: true);

            if (key.KeySize != 521)
                Log.Warning($"CertificateInstallation: the OEM key is {key.KeySize}-bit, and ISO 15118-20 contract " +
                             "provisioning agrees on secp521r1. The station's response will be well-formed and " +
                             "undecryptable for this vehicle.",
                            "15118", "pnc");

            // The same private key twice: once to sign the request, once as an
            // ECDH handle to unwrap the issued contract key. Re-imported rather
            // than cast, because ECDsa and ECDiffieHellman are separate handle
            // types over one key pair.
            var agreement = ECDiffieHellman.Create();
            agreement.ImportECPrivateKey(key.ExportECPrivateKey(), out _);

            Log.Info($"CertificateInstallation: OEM certificate {subject} (+{subCertificates.Length} sub-CA(s)), {key.KeySize}-bit EC.",
                     "15118", "pnc");

            Check(leaf, subCertificates, OEMRoots,
                  "CertificateInstallation", "the OEM provisioning certificate", "OEM", "oemRoot",
                  "session.oemCertificate", Log, "pnc");

            return new CertInstallEvccOptions(leaf, subCertificates, key, agreement);

        }

        #endregion

        #region (private static) Check(Leaf, SubCertificates, Roots, Area, What, RootKind, KindName, Setting, Log, Tag)

        /// <summary>
        /// Whether a chain this vehicle carries ends where it should, said out loud either way.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Refused when there are roots and it does not chain; permitted with a warning when there are
        /// none.</b> The asymmetry is the same one the TLS side has always had, and for the same reason: a
        /// vehicle that was never told what to believe cannot form an opinion, and pretending otherwise
        /// would turn "nothing configured" into "everything rejected" - which fails identically to a real
        /// rejection and means something entirely different.
        /// </para>
        /// <para>
        /// This is the vehicle checking its own credentials, which is worth being clear about. It catches
        /// the certificate that was issued under a hierarchy this vehicle is not actually part of, the one
        /// whose sub-CA did not travel with it, and the one that was quietly replaced - all of which
        /// otherwise surface as a station refusing an authorization for reasons it does not explain.
        /// </para>
        /// </remarks>
        private static void Check(Byte[]              Leaf,
                                  Byte[][]            SubCertificates,
                                  V2GChainValidator?  Roots,
                                  String              Area,
                                  String              What,
                                  String              RootKind,
                                  String              KindName,
                                  String              Setting,
                                  EventLog            Log,
                                  String              Tag)
        {

            if (Roots is null)
            {
                Log.Warning($"{Area}: nothing vouches for {What} - this vehicle holds no {RootKind} root. " +
                            $"Import one as \"{KindName}\" to have the chain checked.",
                            "15118", Tag);
                return;
            }

            var result = Roots.Validate(Leaf, SubCertificates);

            if (!result.Ok)
                throw new ArgumentException(
                          $"{Setting}: {What} does not chain to any {RootKind} root this vehicle holds - " +
                          $"{result.Reason}. Import the right root, or choose another certificate.");

            Log.Info($"{Area}: {What} chains to {result.Anchor}.", "15118", Tag);

        }

        #endregion

        #region LoadTariffVerifyKey(Path, Log)

        /// <summary>
        /// The public key a station's signed SalesTariff or AbsolutePriceSchedule is checked against. The
        /// signing half of the same pair lives at the station.
        /// </summary>
        public static ECDsa LoadTariffVerifyKey(String     Path,
                                                EventLog   Log)
        {

            var (key, subject) = Credentials.LoadEcdsaKey(Path, null, wantPrivate: false, "session.tariffCertificate");

            Log.Info($"Tariff: verifying against {subject}, {key.KeySize}-bit EC.", "15118", "tariff");

            return key;

        }

        #endregion

        #region BouncyCastleOptions(VehicleCertificate, PKIDirectory, Log)

        /// <summary>
        /// What the BouncyCastle backend needs: this vehicle's own chain and key, and - where there is
        /// something to pin against - the station's leaf.
        /// </summary>
        /// <remarks>
        /// Two ways in, and the difference matters. A PKI directory is the development loopback, where a
        /// station minted this vehicle's chain and this side only reads it back; a Vehicle certificate is a
        /// run against a station whose PKI is not ours, where the vehicle brings its own identity.
        ///
        /// The peer check is the part worth understanding. With a PKI directory the station's leaf is
        /// pinned byte for byte from <c>secc.leaf.der</c>; without one there is nothing to pin against, so
        /// any peer is accepted and this says so out loud. Pinning invented here would be a pin against
        /// nothing.
        ///
        /// Pinning and chaining are not alternatives and can both be on: one says "this exact station", the
        /// other "a station some V2G root vouches for". The trust roots are added by the caller, which is
        /// the only place that knows whether any were configured.
        /// </remarks>
        public static BcTlsOptions BouncyCastleOptions(String?    VehicleCertificate,
                                                       String?    PKIDirectory,
                                                       EventLog   Log)
        {

            #region The vehicle brings its own chain

            if (VehicleCertificate is not null)
            {

                var own       = Credentials.LoadForBouncyCastle(VehicleCertificate, null, "session.vehicleCertificate");
                var seccLeaf  = PinnedStationLeaf(PKIDirectory);

                if (seccLeaf is not null)
                    Log.Info("TLS: presenting this vehicle's own chain, and pinning the station's leaf from the PKI directory.",
                             "15118", "tls");
                else
                    Log.Warning("TLS: presenting this vehicle's own chain and accepting ANY station certificate - there is " +
                                "nothing to pin against without a PKI directory, and no trust roots were given.",
                                "15118", "tls");

                return new BcTlsOptions {
                           OwnCredentials    = own,
                           ValidatePeerLeaf  = seccLeaf is null
                                                   ? null
                                                   : actual => seccLeaf.AsSpan().SequenceEqual(actual)
                       };

            }

            #endregion

            #region Or reads the one a station minted for it

            if (PKIDirectory is null)
                throw new ArgumentException(
                          "The BouncyCastle TLS backend needs this vehicle's own credentials: either a PKI directory " +
                          "(the development hierarchy a station minted) or a Vehicle certificate.");

            Byte[] Read(String name)
            {

                var path = System.IO.Path.Combine(PKIDirectory, name);

                return File.Exists(path)
                           ? File.ReadAllBytes(path)
                           : throw new FileNotFoundException(
                                 $"The PKI directory '{PKIDirectory}' has no '{name}'. A station mints this material: " +
                                  "start one with the same directory first.",
                                 path);

            }

            var credentials = new BcTlsCredentials(
                                  [ Read("vehicle.0.der"), Read("vehicle.1.der"), Read("vehicle.2.der") ],
                                  PrivateKeyFactory.CreateKey(Read("vehicle.key")),
                                  SignatureScheme.ecdsa_secp521r1_sha512
                              );

            var pinned = Read("secc.leaf.der");

            Log.Info($"TLS: reading this vehicle's chain out of '{PKIDirectory}', and pinning the station's leaf.",
                     "15118", "tls");

            return new BcTlsOptions {
                       OwnCredentials    = credentials,
                       ValidatePeerLeaf  = actual => pinned.AsSpan().SequenceEqual(actual)
                   };

            #endregion

        }

        #endregion

        #region (private static) PinnedStationLeaf(PKIDirectory)

        /// <summary>
        /// The station's leaf to pin, where the given directory holds one.
        /// </summary>
        /// <remarks>
        /// Absent rather than an error: a vehicle bringing its own chain may be pointed at a directory for
        /// the pin alone, and a directory without one is simply a run with nothing to pin - which is said
        /// where it is decided rather than thrown here.
        /// </remarks>
        private static Byte[]? PinnedStationLeaf(String? PKIDirectory)
        {

            if (PKIDirectory is null)
                return null;

            var candidate = System.IO.Path.Combine(PKIDirectory, "secc.leaf.der");

            return File.Exists(candidate)
                       ? File.ReadAllBytes(candidate)
                       : null;

        }

        #endregion

    }

}
