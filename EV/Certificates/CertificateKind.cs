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

namespace cloud.charging.open.EV.Certificates
{

    /// <summary>
    /// What a certificate in this vehicle's store is <i>for</i>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seven kinds, in two groups that behave differently in every respect that
    /// matters. A <b>trust anchor</b> is a public certificate that says which
    /// chains this vehicle believes; there may be any number of them active at
    /// once, and none of them is ever chosen for a session. A <b>credential</b>
    /// is something this vehicle presents or verifies with, carries a private
    /// key in every case but one, and is chosen - exactly one per role - when a
    /// session starts.
    /// </para>
    /// <para>
    /// The three roots are kept apart rather than pooled, because they answer
    /// three different questions and pooling them answers all three with the
    /// widest of them. An OEM root vouches for what a vehicle was born with; a
    /// Mobility Operator root vouches for who pays; a V2G root vouches for the
    /// station at the other end of the cable. One bag of roots would let an OEM
    /// root vouch for a contract, which is not a subtlety - it is the whole
    /// difference between a vehicle that checks who is charging it and one that
    /// checks that somebody signed something.
    /// </para>
    /// <para>
    /// The names follow the CharIN V2G second-generation PKI Certificate
    /// Policy, as they do in <see cref="ISO15118.VehicleCredentials"/> and for
    /// the same reason: ISO 15118-20's own naming is not consistent with
    /// itself, and the Policy's is.
    /// </para>
    /// </remarks>
    public enum CertificateKind
    {

        /// <summary>
        /// A <b>V2G root</b>: what a station's certificate has to chain to.
        /// </summary>
        /// <remarks>
        /// The one this vehicle has always had, under the older name
        /// "trust roots". It is checked at the TLS handshake, against the
        /// certificate the station presents.
        /// </remarks>
        V2GRoot,

        /// <summary>
        /// A <b>Mobility Operator root</b>: what a contract certificate has to
        /// chain to.
        /// </summary>
        /// <remarks>
        /// Checked twice, and the second time is the one that earns its keep.
        /// Once against the contract certificate this vehicle carries, when it
        /// is loaded - a contract nobody vouches for is worth knowing about
        /// before the session rather than after it. And once against the
        /// contract certificate an ISO 15118-20 station <i>issues</i> during
        /// CertificateInstallation, which arrives over the wire from a party
        /// this vehicle has no other reason to believe.
        /// </remarks>
        MORoot,

        /// <summary>
        /// An <b>OEM root</b>: what an OEM provisioning certificate has to
        /// chain to.
        /// </summary>
        /// <remarks>
        /// Checked against the OEM provisioning certificate this vehicle
        /// carries. A vehicle validating its own birth certificate sounds
        /// circular and is not: the OEM certificate is the identity a station
        /// is asked to issue a contract against, so a vehicle that cannot say
        /// which OEM vouched for it is about to ask for a contract it cannot
        /// justify.
        /// </remarks>
        OEMRoot,

        /// <summary>
        /// The <b>Vehicle</b> certificate: who this vehicle is.
        /// </summary>
        /// <remarks>
        /// Presented in the TLS handshake, and for ISO 15118-20 what a
        /// station's resume binding is computed over.
        /// </remarks>
        Vehicle,

        /// <summary>
        /// A <b>contract</b> certificate: who pays.
        /// </summary>
        /// <remarks>
        /// The one kind a vehicle plausibly holds several of - one per
        /// contract, and ISO 15118-2 and -20 need not be the same one - which
        /// is most of the reason this store exists at all.
        /// </remarks>
        Contract,

        /// <summary>
        /// The <b>OEM provisioning</b> certificate: what the vehicle was born
        /// with.
        /// </summary>
        /// <remarks>
        /// Its key has to be P-521, because the contract key a -20 station
        /// sends back is unwrapped by an ECDH against the station's ephemeral
        /// secp521r1 key. A P-256 one takes part in an exchange it cannot
        /// finish, which is why the store says so at import rather than letting
        /// it fail as an undecryptable response.
        /// </remarks>
        OEMProvisioning,

        /// <summary>
        /// The public key a station's signed tariff is checked against.
        /// </summary>
        /// <remarks>
        /// The only credential with no private key: the signing half lives at
        /// the station, and this side only ever verifies.
        /// </remarks>
        TariffVerification

    }


    /// <summary>
    /// What each kind of certificate is called, where it is kept, and what a
    /// file has to be to count as one.
    /// </summary>
    public static class CertificateKindExtensions
    {

        #region IsTrustAnchor(this Kind)

        /// <summary>
        /// Whether this kind is a root somebody chains to, rather than
        /// something this vehicle presents.
        /// </summary>
        public static Boolean IsTrustAnchor(this CertificateKind Kind)

            => Kind is CertificateKind.V2GRoot
                    or CertificateKind.MORoot
                    or CertificateKind.OEMRoot;

        #endregion

        #region NeedsPrivateKey(this Kind)

        /// <summary>
        /// Whether a file of this kind is only useful with the private key in
        /// it.
        /// </summary>
        /// <remarks>
        /// Roots must <i>not</i> carry one - a trust anchor with a private key
        /// is somebody's CA key in the wrong place - and the tariff
        /// verification key does not need one. Everything else is an identity
        /// this vehicle proves, and cannot prove without the key.
        /// </remarks>
        public static Boolean NeedsPrivateKey(this CertificateKind Kind)

            => Kind is CertificateKind.Vehicle
                    or CertificateKind.Contract
                    or CertificateKind.OEMProvisioning;

        #endregion

        #region Directory(this Kind)

        /// <summary>
        /// Where certificates of this kind live below the store, as a relative
        /// path.
        /// </summary>
        /// <remarks>
        /// The roots sit together under one directory because they are one
        /// thought - "what this vehicle believes" - and because a store
        /// somebody looks at with a file manager should show that at a glance.
        /// </remarks>
        public static String Directory(this CertificateKind Kind)

            => Kind switch {
                   CertificateKind.V2GRoot             => "roots/v2g",
                   CertificateKind.MORoot              => "roots/mo",
                   CertificateKind.OEMRoot             => "roots/oem",
                   CertificateKind.Vehicle             => "vehicle",
                   CertificateKind.Contract            => "contract",
                   CertificateKind.OEMProvisioning     => "oem",
                   CertificateKind.TariffVerification  => "tariff",
                   _                                   => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown certificate kind.")
               };

        #endregion

        #region Extension(this Kind)

        /// <summary>
        /// What a stored file of this kind is called at the end.
        /// </summary>
        /// <remarks>
        /// <para>
        /// PEM for a trust anchor, PKCS#12 for everything this vehicle
        /// presents or verifies with. Not a preference but what the readers on
        /// the other side already accept: <c>TrustRoots.Load</c> scans a
        /// directory for PEM and DER and deliberately ignores PKCS#12, because
        /// a trust anchor has no business carrying a private key; and
        /// <c>Credentials</c> reads PKCS#12 and nothing else.
        /// </para>
        /// <para>
        /// So the tariff verification key is kept as PKCS#12 although it has no
        /// private half - the file is a bag of certificates either way, and a
        /// store that wrote it as PEM would be a store whose files its own
        /// loader could not open.
        /// </para>
        /// </remarks>
        public static String Extension(this CertificateKind Kind)

            => Kind.IsTrustAnchor()
                   ? ".pem"
                   : ".p12";

        #endregion

        #region AsText(this Kind) / TryParseKind(Text, out Kind)

        /// <summary>
        /// This kind as it is written in the configuration file and on the
        /// JSON API.
        /// </summary>
        public static String AsText(this CertificateKind Kind)

            => Kind switch {
                   CertificateKind.V2GRoot             => "v2gRoot",
                   CertificateKind.MORoot              => "moRoot",
                   CertificateKind.OEMRoot             => "oemRoot",
                   CertificateKind.Vehicle             => "vehicle",
                   CertificateKind.Contract            => "contract",
                   CertificateKind.OEMProvisioning     => "oemProvisioning",
                   CertificateKind.TariffVerification  => "tariffVerification",
                   _                                   => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown certificate kind.")
               };

        /// <summary>
        /// A kind as somebody wrote it, or nothing.
        /// </summary>
        /// <remarks>
        /// Case-insensitive, because the difference between "oemRoot" and
        /// "oemroot" is not a difference anybody means.
        /// </remarks>
        public static Boolean TryParseKind(String? Text, out CertificateKind Kind)
        {

            foreach (var candidate in All)
            {
                if (String.Equals(candidate.AsText(), Text?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Kind = candidate;
                    return true;
                }
            }

            Kind = default;
            return false;

        }

        #endregion

        #region Describe(this Kind)

        /// <summary>
        /// One line saying what this kind is for, for a log entry or a page
        /// that has to name it to somebody who has not read ISO 15118.
        /// </summary>
        public static String Describe(this CertificateKind Kind)

            => Kind switch {
                   CertificateKind.V2GRoot             => "V2G root - what a station's certificate must chain to",
                   CertificateKind.MORoot              => "Mobility Operator root - what a contract certificate must chain to",
                   CertificateKind.OEMRoot             => "OEM root - what an OEM provisioning certificate must chain to",
                   CertificateKind.Vehicle             => "Vehicle certificate - who this vehicle is",
                   CertificateKind.Contract            => "contract certificate - who pays",
                   CertificateKind.OEMProvisioning     => "OEM provisioning certificate - what this vehicle was born with",
                   CertificateKind.TariffVerification  => "tariff certificate - what a station's signed tariff is checked with",
                   _                                   => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown certificate kind.")
               };

        #endregion

        #region SortOrder(this Kind)

        /// <summary>
        /// Where this kind comes in the order everything is shown and written
        /// in: what this vehicle believes first, what it presents second.
        /// </summary>
        /// <remarks>
        /// Spelled out rather than taken from the enum's own numbering, so that
        /// somebody adding a kind in the middle of the enum does not silently
        /// reorder every page and every index file.
        /// </remarks>
        public static Int32 SortOrder(this CertificateKind Kind)

            => Kind switch {
                   CertificateKind.V2GRoot             => 0,
                   CertificateKind.MORoot              => 1,
                   CertificateKind.OEMRoot             => 2,
                   CertificateKind.Vehicle             => 3,
                   CertificateKind.Contract            => 4,
                   CertificateKind.OEMProvisioning     => 5,
                   CertificateKind.TariffVerification  => 6,
                   _                                   => Int32.MaxValue
               };

        #endregion

        #region All

        /// <summary>
        /// Every kind this vehicle knows, in the order a page shows them:
        /// what it believes first, what it presents second.
        /// </summary>
        public static readonly IReadOnlyList<CertificateKind> All = [
            CertificateKind.V2GRoot,
            CertificateKind.MORoot,
            CertificateKind.OEMRoot,
            CertificateKind.Vehicle,
            CertificateKind.Contract,
            CertificateKind.OEMProvisioning,
            CertificateKind.TariffVerification
        ];

        #endregion

    }

}
