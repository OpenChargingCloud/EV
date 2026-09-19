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

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.EV.Certificates
{

    /// <summary>
    /// One certificate in this vehicle's store: what it is, what is in it, and
    /// whether this vehicle is currently using it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here but <see cref="Label"/> and <see cref="IsActive"/> is
    /// read out of the certificate itself and is therefore not somebody's
    /// opinion: a store whose index disagrees with its files is a store that
    /// has to be believed about the wrong one. Those two are the only fields
    /// the index is the authority on, and they are the reason the index exists
    /// rather than the directory listing being the whole truth.
    /// </para>
    /// <para>
    /// <see cref="Id"/> and <see cref="Thumbprint"/> are not the same thing and
    /// are not used for the same thing. The thumbprint is the certificate's
    /// SHA-256 fingerprint in full, which is what somebody compares against
    /// what a certificate authority told them. The id is the first 16 hex
    /// digits of it, which is what a configuration file and a URL carry - short
    /// enough to read out loud, and checked for collision at import so that two
    /// entries can never answer to one handle. A truncated fingerprint is a
    /// handle and never evidence, which is why both are kept.
    /// </para>
    /// </remarks>
    /// <param name="Id">The short handle this entry is addressed by.</param>
    /// <param name="Kind">What this certificate is for.</param>
    /// <param name="FileName">Where it is kept, relative to the store.</param>
    /// <param name="Label">What somebody calls it; the common name unless they said otherwise.</param>
    /// <param name="Subject">Who it is about.</param>
    /// <param name="Issuer">Who signed it.</param>
    /// <param name="SerialNumber">Its serial, as the issuer assigned it.</param>
    /// <param name="Thumbprint">Its SHA-256 fingerprint, in full.</param>
    /// <param name="NotBefore">When it starts being valid.</param>
    /// <param name="NotAfter">When it stops.</param>
    /// <param name="KeyAlgorithm">What kind of key it carries, e.g. "ECDSA P-521".</param>
    /// <param name="HasPrivateKey">Whether the stored file carries the private key as well.</param>
    /// <param name="ChainLength">How many further certificates travel with it, e.g. its sub-CAs.</param>
    /// <param name="IsActive">Whether this vehicle is currently using it.</param>
    /// <param name="ImportedAt">When it was put here.</param>
    public sealed record CertificateEntry(String           Id,
                                          CertificateKind  Kind,
                                          String           FileName,
                                          String           Label,
                                          String           Subject,
                                          String           Issuer,
                                          String           SerialNumber,
                                          String           Thumbprint,
                                          DateTimeOffset   NotBefore,
                                          DateTimeOffset   NotAfter,
                                          String           KeyAlgorithm,
                                          Boolean          HasPrivateKey,
                                          Int32            ChainLength,
                                          Boolean          IsActive,
                                          DateTimeOffset   ImportedAt)
    {

        #region Data

        /// <summary>
        /// How many hex digits of the fingerprint an id is.
        /// </summary>
        /// <remarks>
        /// Sixteen, which is 64 bits: far too many for two certificates in one
        /// vehicle's store to collide by accident, and short enough to appear
        /// in a configuration file without wrapping. Import checks anyway - see
        /// <see cref="CertificateStore"/> - because "by accident" is not the
        /// only way two things collide.
        /// </remarks>
        public const Int32  IdLength    = 16;

        /// <summary>
        /// The longest a label may be written.
        /// </summary>
        public const Int32  MaxLabelLength  = 120;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this certificate's validity has not started yet.
        /// </summary>
        public Boolean IsNotYetValid
            => NotBefore > DateTimeOffset.UtcNow;

        /// <summary>
        /// Whether this certificate has expired.
        /// </summary>
        public Boolean IsExpired
            => NotAfter < DateTimeOffset.UtcNow;

        /// <summary>
        /// Whether this certificate could be used right now: active, and within
        /// its own validity.
        /// </summary>
        /// <remarks>
        /// Active and usable are kept apart on purpose. Somebody switches a
        /// certificate on; time switches it off. A page that showed only the
        /// one would have to explain why an active certificate did nothing.
        /// </remarks>
        public Boolean IsUsable
            => IsActive && !IsExpired && !IsNotYetValid;

        #endregion


        #region (static) From(Certificate, Kind, FileName, Label, ChainLength, IsActive, ImportedAt)

        /// <summary>
        /// An entry describing a certificate that has just been read.
        /// </summary>
        public static CertificateEntry From(X509Certificate2  Certificate,
                                            CertificateKind   Kind,
                                            String            FileName,
                                            String?           Label,
                                            Int32             ChainLength,
                                            Boolean           IsActive,
                                            DateTimeOffset?   ImportedAt   = null)
        {

            var thumbprint = ThumbprintOf(Certificate);

            return new CertificateEntry(
                       thumbprint[..IdLength],
                       Kind,
                       FileName,
                       Label?.Trim() is { Length: > 0 } given
                           ? given
                           : CommonNameOf(Certificate),
                       Certificate.Subject,
                       Certificate.Issuer,
                       Certificate.SerialNumber,
                       thumbprint,
                       Certificate.NotBefore.ToUniversalTime(),
                       Certificate.NotAfter. ToUniversalTime(),
                       KeyAlgorithmOf(Certificate),
                       Certificate.HasPrivateKey,
                       ChainLength,
                       IsActive,
                       ImportedAt ?? DateTimeOffset.UtcNow
                   );

        }

        #endregion

        #region (static) ThumbprintOf(Certificate)

        /// <summary>
        /// A certificate's SHA-256 fingerprint, in lower-case hexadecimal.
        /// </summary>
        /// <remarks>
        /// SHA-256 rather than <see cref="X509Certificate2.Thumbprint"/>, which
        /// is SHA-1 and has no business identifying anything in 2026.
        /// </remarks>
        public static String ThumbprintOf(X509Certificate2 Certificate)

            => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

        #endregion

        #region (static) CommonNameOf(Certificate)

        /// <summary>
        /// What to call a certificate nobody has named: its common name, or its
        /// whole subject where it has none.
        /// </summary>
        public static String CommonNameOf(X509Certificate2 Certificate)
        {

            var common = Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            return common is { Length: > 0 }
                       ? common
                       : Certificate.Subject;

        }

        #endregion

        #region (static) KeyAlgorithmOf(Certificate)

        /// <summary>
        /// What kind of key a certificate carries, written the way somebody
        /// comparing it against a specification would write it.
        /// </summary>
        /// <remarks>
        /// The curve is named rather than only the size, because ISO 15118-20's
        /// requirement is a curve - secp521r1 - and "521-bit EC" is one
        /// reasonable reading away from it.
        /// </remarks>
        public static String KeyAlgorithmOf(X509Certificate2 Certificate)
        {

            using var ecdsa = Certificate.GetECDsaPublicKey();

            if (ecdsa is not null)
            {

                var curve = ecdsa.KeySize switch {
                                256  => "P-256",
                                384  => "P-384",
                                521  => "P-521",
                                _    => $"{ecdsa.KeySize}-bit"
                            };

                return $"ECDSA {curve}";

            }

            using var rsa = Certificate.GetRSAPublicKey();

            return rsa is not null
                       ? $"RSA {rsa.KeySize}-bit"
                       : Certificate.PublicKey.Oid.FriendlyName ?? Certificate.PublicKey.Oid.Value ?? "unknown";

        }

        #endregion


        #region (static) TryParse(JSON, out Entry, out Error)

        /// <summary>
        /// One entry as the index file holds it, or the one sentence that says
        /// what is wrong with it.
        /// </summary>
        /// <remarks>
        /// Only <see cref="Label"/> and <see cref="IsActive"/> are actually
        /// taken from here by <see cref="CertificateStore"/>; the rest is read
        /// again from the file it names. It is all parsed anyway so that a
        /// damaged index is refused as a whole rather than half-believed.
        /// </remarks>
        public static Boolean TryParse(JObject                                     JSON,
                                       [NotNullWhen(true)]  out CertificateEntry?  Entry,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Entry  = null;
            Error  = null;

            var id = JSON.Value<String>("id")?.Trim();

            if (id is null or { Length: 0 })
            {
                Error = "a certificate entry without an 'id'.";
                return false;
            }

            if (!CertificateKindExtensions.TryParseKind(JSON.Value<String>("kind"), out var kind))
            {
                Error = $"the certificate '{id}' has an unknown 'kind'.";
                return false;
            }

            var fileName = JSON.Value<String>("fileName")?.Trim();

            if (fileName is null or { Length: 0 })
            {
                Error = $"the certificate '{id}' has no 'fileName'.";
                return false;
            }

            Entry = new CertificateEntry(
                        id,
                        kind,
                        fileName,
                        JSON.Value<String>("label")        ?? "",
                        JSON.Value<String>("subject")      ?? "",
                        JSON.Value<String>("issuer")       ?? "",
                        JSON.Value<String>("serialNumber") ?? "",
                        JSON.Value<String>("thumbprint")   ?? "",
                        Moment(JSON, "notBefore") ?? DateTimeOffset.MinValue,
                        Moment(JSON, "notAfter")  ?? DateTimeOffset.MaxValue,
                        JSON.Value<String>("keyAlgorithm") ?? "",
                        JSON.Value<Boolean?>("hasPrivateKey") ?? false,
                        JSON.Value<Int32?>("chainLength")     ?? 0,
                        JSON.Value<Boolean?>("active")        ?? true,
                        Moment(JSON, "importedAt") ?? DateTimeOffset.UtcNow
                    );

            return true;

        }

        #endregion

        #region (private static) Moment(JSON, Name)

        /// <summary>
        /// One moment out of the index, whichever way the JSON reader handled it.
        /// </summary>
        /// <remarks>
        /// Newtonsoft turns an ISO 8601 string into a <see cref="DateTime"/> token while it is parsing, and
        /// asking that token for a <see cref="DateTimeOffset"/> throws rather than converting - which lost a
        /// whole index, and with it every label and every on/off, to one cast. So the token is taken as what
        /// it is and turned here, and a date this cannot read is absent rather than fatal.
        /// </remarks>
        private static DateTimeOffset? Moment(JObject  JSON,
                                              String   Name)
        {

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Date)
            {

                var value = token.ToObject<Object>();

                return value switch {
                           DateTime       moment  => new DateTimeOffset(moment.ToUniversalTime(), TimeSpan.Zero),
                           DateTimeOffset moment  => moment,
                           _                      => null
                       };

            }

            return DateTimeOffset.TryParse(token.Value<String>(),
                                           System.Globalization.CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.AdjustToUniversal |
                                           System.Globalization.DateTimeStyles.AssumeUniversal,
                                           out var parsed)
                       ? parsed
                       : null;

        }

        #endregion

        #region ToJSON(WithDiagnostics = false)

        /// <summary>
        /// This entry as the index file holds it, and as the JSON API hands it
        /// out.
        /// </summary>
        /// <param name="WithDiagnostics">
        /// Also say what follows from the clock - expired, not yet valid,
        /// usable. Written for a page and not for the file, because a stored
        /// document that says "valid" is wrong the day after it is written.
        /// </param>
        public JObject ToJSON(Boolean WithDiagnostics = false)
        {

            var json = new JObject(
                           new JProperty("id",            Id),
                           new JProperty("kind",          Kind.AsText()),
                           new JProperty("fileName",      FileName),
                           new JProperty("label",         Label),
                           new JProperty("subject",       Subject),
                           new JProperty("issuer",        Issuer),
                           new JProperty("serialNumber",  SerialNumber),
                           new JProperty("thumbprint",    Thumbprint),
                           new JProperty("notBefore",     NotBefore.UtcDateTime),
                           new JProperty("notAfter",      NotAfter. UtcDateTime),
                           new JProperty("keyAlgorithm",  KeyAlgorithm),
                           new JProperty("hasPrivateKey", HasPrivateKey),
                           new JProperty("chainLength",   ChainLength),
                           new JProperty("active",        IsActive),
                           new JProperty("importedAt",    ImportedAt.UtcDateTime)
                       );

            if (WithDiagnostics)
            {
                json.Add("expired",      IsExpired);
                json.Add("notYetValid",  IsNotYetValid);
                json.Add("usable",       IsUsable);
                json.Add("description",  Kind.Describe());
            }

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Label} ({Kind.AsText()}, {Id}){(IsActive ? "" : ", inactive")}{(IsExpired ? ", expired" : "")}";

        #endregion

    }

}
