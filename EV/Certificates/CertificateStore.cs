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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.Security;

using cloud.charging.open.EV.Logging;

#endregion

namespace cloud.charging.open.EV.Certificates
{

    /// <summary>
    /// Every certificate this vehicle has been given: the roots it believes and
    /// the credentials it presents, on disk beside its accounts, and switchable
    /// one by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The directory is the store and the index is its memory.</b> Each
    /// certificate is a file below <c>&lt;store&gt;/</c> in a directory named
    /// after its kind, and <c>index.json</c> beside them records the two things
    /// a file cannot say about itself: what somebody calls it, and whether it is
    /// switched on. So the store survives being copied to another machine, and a
    /// lost index costs labels and switches rather than certificates.
    /// </para>
    /// <para>
    /// <b>Both directions are supported, and that is the point.</b> Certificates
    /// arrive by import, which copies the file in; and certificates that are
    /// already in the directory - put there by hand, restored from a backup,
    /// carried over from another vehicle - are picked up at every
    /// <see cref="Reload"/> and adopted. A file somebody dropped in is taken as
    /// meant, switched on, and named in the log, because a store that silently
    /// ignored it would be a store whose directory listing lies.
    /// </para>
    /// <para>
    /// <b>Private keys are stored unencrypted, which is a decision and not an
    /// oversight.</b> A PKCS#12 is opened with whatever password it arrived
    /// under and written back without one, so that any number of certificates
    /// per role work without any number of passwords to carry. What guards them
    /// is the file system: the store directory is created for its owner alone.
    /// Anybody who can read it can take this vehicle's identity and its
    /// contract, so it belongs on a machine whose users are all trusted with
    /// exactly that - which <see cref="WarnAboutStoredKeys"/> says once at every
    /// start.
    /// </para>
    /// <para>
    /// Everything that changes the store takes <see cref="storeLock"/> and
    /// writes the index before returning, so a vehicle that is killed between
    /// two requests comes back with what it last confirmed rather than with
    /// half of it.
    /// </para>
    /// </remarks>
    public sealed class CertificateStore
    {

        #region Data

        /// <summary>
        /// The index file, beside the certificates it is about.
        /// </summary>
        public const String  IndexFileName  = "index.json";

        /// <summary>
        /// What a file has to end in to be picked up as a certificate when the
        /// store is read.
        /// </summary>
        /// <remarks>
        /// The trust anchor extensions come from <c>TrustRoots.Load</c>, which
        /// is what eventually reads them, plus PKCS#12 for the credentials.
        /// A file with any other name is left alone rather than refused: a
        /// store directory is somewhere people also keep a README.
        /// </remarks>
        private static readonly String[] readableExtensions = [ ".pem", ".crt", ".cer", ".der", ".p12", ".pfx" ];

        private readonly SemaphoreSlim                        storeLock  = new (1, 1);
        private readonly Dictionary<String, CertificateEntry>  entries    = [];
        private readonly EventLog                              log;

        #endregion

        #region Properties

        /// <summary>
        /// Where this store keeps its certificates.
        /// </summary>
        public String Directory { get; }

        /// <summary>
        /// Everything in the store, roots before credentials and each group by
        /// label.
        /// </summary>
        public IReadOnlyList<CertificateEntry> Entries
        {
            get
            {

                storeLock.Wait();

                try
                {
                    return [.. entries.Values.
                                   OrderBy(entry => entry.Kind.SortOrder()).
                                   ThenBy (entry => entry.Label, StringComparer.OrdinalIgnoreCase)];
                }
                finally
                {
                    storeLock.Release();
                }

            }
        }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// A store over the given directory, which is created where it does not
        /// exist.
        /// </summary>
        /// <param name="Directory">Where the certificates live.</param>
        /// <param name="Log">Where to say what was found, adopted and dropped.</param>
        public CertificateStore(String    Directory,
                                EventLog  Log)
        {

            this.Directory  = Path.GetFullPath(Directory);
            this.log        = Log;

            CreateDirectories();

        }

        #endregion


        #region Reload()

        /// <summary>
        /// Read the directory and reconcile it with the index: keep what both
        /// know, adopt what only the directory has, and forget what only the
        /// index has.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The three outcomes are what makes a directory somebody can also use
        /// by hand. A file the index knows keeps its label and its switch. A
        /// file the index has never seen is adopted, switched on and named in
        /// the log - somebody put it there. An entry whose file is gone is
        /// dropped and named in the log, because a store that kept pointing at
        /// a missing certificate would fail at a handshake instead of here.
        /// </para>
        /// <para>
        /// A file that cannot be read as a certificate is named and skipped
        /// rather than thrown: one unreadable file in a directory of good ones
        /// should cost that one file, not the start.
        /// </para>
        /// </remarks>
        public void Reload()
        {

            storeLock.Wait();

            try
            {

                var remembered  = ReadIndex();
                var found       = new Dictionary<String, CertificateEntry>();
                var adopted     = 0;

                foreach (var kind in CertificateKindExtensions.All)
                {

                    var directory = Path.Combine(Directory, kind.Directory().Replace('/', Path.DirectorySeparatorChar));

                    if (!System.IO.Directory.Exists(directory))
                        continue;

                    foreach (var file in System.IO.Directory.EnumerateFiles(directory).
                                                            Where  (file => readableExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).
                                                            OrderBy(file => file, StringComparer.Ordinal))
                    {

                        var relative = RelativeName(kind, Path.GetFileName(file));

                        if (!TryRead(file, out var certificate, out var chainLength, out var problem))
                        {
                            log.Warning($"Certificates: '{relative}' could not be read and was left where it is - {problem}",
                                        "certificates");
                            continue;
                        }

                        using (certificate)
                        {

                            var known = remembered.Values.FirstOrDefault(entry => entry.FileName == relative);

                            var entry = CertificateEntry.From(
                                            certificate,
                                            kind,
                                            relative,
                                            known?.Label,
                                            chainLength,
                                            known?.IsActive ?? true,
                                            known?.ImportedAt
                                        );

                            if (found.ContainsKey(entry.Id))
                            {
                                log.Warning($"Certificates: '{relative}' is the same certificate as one already read and was skipped.",
                                            "certificates");
                                continue;
                            }

                            found.Add(entry.Id, entry);

                            if (known is null)
                            {
                                adopted++;
                                log.Notice($"Certificates: adopted '{relative}' - {entry.Label}, {kind.Describe()}. It is switched on.",
                                           "certificates");
                            }

                        }

                    }

                }

                foreach (var gone in remembered.Values.Where(entry => !found.ContainsKey(entry.Id)))
                    log.Notice($"Certificates: '{gone.FileName}' is no longer there and was dropped from the index.",
                               "certificates");

                entries.Clear();

                foreach (var entry in found)
                    entries.Add(entry.Key, entry.Value);

                WriteIndex();

                log.Info($"Certificates: {entries.Count} in '{Directory}'" +
                         $"{(adopted > 0 ? $", {adopted} of them newly adopted" : "")}" +
                         $" ({Summarise()}).",
                         "certificates");

            }
            finally
            {
                storeLock.Release();
            }

        }

        #endregion

        #region WarnAboutStoredKeys()

        /// <summary>
        /// Say once, at a start, that the private keys in this store are not
        /// encrypted.
        /// </summary>
        /// <remarks>
        /// Said every start rather than once ever, and as a warning rather than
        /// a note, because the fact does not stop being true and the person
        /// reading the console today is not necessarily the one who set the
        /// store up.
        /// </remarks>
        public void WarnAboutStoredKeys()
        {

            var withKeys = Entries.Count(entry => entry.HasPrivateKey);

            if (withKeys > 0)
                log.Warning($"Certificates: {withKeys} private key(s) are stored unencrypted in '{Directory}'. " +
                             "Anybody who can read that directory can take this vehicle's identity and its contract.",
                            "certificates");

        }

        #endregion


        #region Import(Content, Kind, Password, Label, out Entry, out Error)

        /// <summary>
        /// Put a certificate into the store, copying it in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What arrives may be PEM, DER or PKCS#12; what is written is the one
        /// format the reader for that kind accepts - see
        /// <see cref="CertificateKindExtensions.Extension"/>. A PKCS#12 is
        /// opened with <paramref name="Password"/> and written back without
        /// one.
        /// </para>
        /// <para>
        /// Importing the same certificate twice is not an error and not a
        /// duplicate: the id is the fingerprint, so the second import is the
        /// first entry, and the only thing it can change is the label. That is
        /// what makes "import everything in this directory" a safe thing to do
        /// twice.
        /// </para>
        /// <para>
        /// Everything refused here is refused with the reason in the sentence,
        /// because the alternative - a file that turns out to be the wrong kind
        /// halfway through a handshake - shows up as a station that appears to
        /// have hung up.
        /// </para>
        /// </remarks>
        /// <param name="Content">The file as it arrived.</param>
        /// <param name="Kind">What it is to be used for.</param>
        /// <param name="Password">What opens it, where it is a protected PKCS#12.</param>
        /// <param name="Label">What to call it; its common name where this is not given.</param>
        public Boolean Import(Byte[]                                      Content,
                              CertificateKind                             Kind,
                              String?                                     Password,
                              String?                                     Label,
                              [NotNullWhen(true)]  out CertificateEntry?  Entry,
                              [NotNullWhen(false)] out String?            Error)
        {

            Entry  = null;
            Error  = null;

            if (Content.Length == 0)
            {
                Error = "There is nothing in that file.";
                return false;
            }

            if (Label is { Length: > CertificateEntry.MaxLabelLength })
            {
                Error = $"A label may be at most {CertificateEntry.MaxLabelLength} characters long.";
                return false;
            }

            X509Certificate2Collection collection;

            try
            {
                collection = ReadCollection(Content, Password);
            }
            catch (Exception exception)
            {
                Error = exception.Message;
                return false;
            }

            if (collection.Count == 0)
            {
                Error = "That file holds no certificate.";
                return false;
            }

            // The leaf is the one with the private key where there is one, and
            // otherwise the first: the same rule the session's own loaders use,
            // so that what the store calls the leaf is what they will.
            var leaf = collection.FirstOrDefault(certificate => certificate.HasPrivateKey) ?? collection[0];

            if (!Suits(leaf, Kind, out Error))
            {
                Dispose(collection);
                return false;
            }

            storeLock.Wait();

            try
            {

                var thumbprint = CertificateEntry.ThumbprintOf(leaf);
                var id         = thumbprint[..CertificateEntry.IdLength];

                #region The same certificate again is the same entry

                if (entries.TryGetValue(id, out var existing))
                {

                    if (existing.Thumbprint != thumbprint)
                    {
                        Error = $"The handle '{id}' is already taken by a different certificate " +
                                 "({existing.Label}). Remove that one first.";
                        return false;
                    }

                    if (existing.Kind != Kind)
                    {
                        Error = $"That certificate is already in the store as a {existing.Kind.AsText()} " +
                                $"('{existing.Label}'). One certificate has one purpose - remove it first " +
                                 "to put it back as something else.";
                        return false;
                    }

                    if (Label?.Trim() is { Length: > 0 } relabel && relabel != existing.Label)
                    {
                        existing = existing with { Label = relabel };
                        entries[id] = existing;
                        WriteIndex();
                        log.Info($"Certificates: '{existing.FileName}' is now called '{relabel}'.", "certificates");
                    }
                    else
                        log.Info($"Certificates: {existing.Label} was already in the store; nothing changed.", "certificates");

                    Entry = existing;
                    return true;

                }

                #endregion

                var fileName  = RelativeName(Kind, id + Kind.Extension());
                var fullPath  = FullPath(fileName);

                try
                {
                    File.WriteAllBytes(fullPath, Serialise(collection, leaf, Kind));
                    Protect(fullPath);
                }
                catch (Exception exception)
                {
                    Error = $"That certificate could not be written to the store: {exception.Message}";
                    return false;
                }

                Entry = CertificateEntry.From(
                            leaf,
                            Kind,
                            fileName,
                            Label,
                            collection.Count - 1,
                            IsActive: true
                        );

                entries.Add(Entry.Id, Entry);
                WriteIndex();

                log.Notice($"Certificates: imported {Entry.Label} as {Kind.Describe()}, " +
                           $"{Entry.KeyAlgorithm}, valid until {Entry.NotAfter.UtcDateTime:yyyy-MM-dd}. It is switched on.",
                           "certificates");

                // Said at the import as well as at a start, because the start
                // that matters happened before this key existed: a vehicle that
                // only warned at construction would never mention the first
                // private key anybody put on it.
                if (Entry.HasPrivateKey)
                    log.Warning($"Certificates: the private key of {Entry.Label} is stored unencrypted in " +
                                $"'{Directory}'. Anybody who can read that directory can use it.",
                                "certificates");

                return true;

            }
            finally
            {
                storeLock.Release();
                Dispose(collection);
            }

        }

        #endregion

        #region SetActive(Id, Active, out Entry, out Error)

        /// <summary>
        /// Switch one certificate on or off, leaving it where it is.
        /// </summary>
        /// <remarks>
        /// The difference between this and <see cref="Remove"/> is the whole
        /// reason both exist: a certificate somebody is taking out of service
        /// for an afternoon should not have to be imported again afterwards,
        /// and one whose key may have leaked should not merely be switched off.
        /// </remarks>
        public Boolean SetActive(String                                      Id,
                                 Boolean                                     Active,
                                 [NotNullWhen(true)]  out CertificateEntry?  Entry,
                                 [NotNullWhen(false)] out String?            Error)
        {

            Entry  = null;
            Error  = null;

            storeLock.Wait();

            try
            {

                if (!entries.TryGetValue(Id, out var entry))
                {
                    Error = $"There is no certificate '{Id}' in this store.";
                    return false;
                }

                if (entry.IsActive != Active)
                {

                    Entry        = entry with { IsActive = Active };
                    entries[Id]  = Entry;

                    WriteIndex();

                    log.Notice($"Certificates: {Entry.Label} ({Entry.Kind.AsText()}) was switched {(Active ? "on" : "off")}.",
                               "certificates");

                }
                else
                    Entry = entry;

                return true;

            }
            finally
            {
                storeLock.Release();
            }

        }

        #endregion

        #region Relabel(Id, Label, out Entry, out Error)

        /// <summary>
        /// Change what a certificate is called.
        /// </summary>
        public Boolean Relabel(String                                      Id,
                               String?                                     Label,
                               [NotNullWhen(true)]  out CertificateEntry?  Entry,
                               [NotNullWhen(false)] out String?            Error)
        {

            Entry  = null;
            Error  = null;

            if (Label is { Length: > CertificateEntry.MaxLabelLength })
            {
                Error = $"A label may be at most {CertificateEntry.MaxLabelLength} characters long.";
                return false;
            }

            storeLock.Wait();

            try
            {

                if (!entries.TryGetValue(Id, out var entry))
                {
                    Error = $"There is no certificate '{Id}' in this store.";
                    return false;
                }

                // A label taken back is not an empty label: it is the
                // certificate's own name again.
                var settled = Label?.Trim() is { Length: > 0 } given
                                  ? given
                                  : NameFromFile(entry);

                Entry        = entry with { Label = settled };
                entries[Id]  = Entry;

                WriteIndex();

                return true;

            }
            finally
            {
                storeLock.Release();
            }

        }

        #endregion

        #region Remove(Id, out Error)

        /// <summary>
        /// Take a certificate out of the store and delete its file.
        /// </summary>
        /// <remarks>
        /// The file goes. A store whose "delete" left the private key on the
        /// disk would be worse than one with no delete at all, because it would
        /// be believed.
        /// </remarks>
        public Boolean Remove(String                            Id,
                              [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            storeLock.Wait();

            try
            {

                if (!entries.TryGetValue(Id, out var entry))
                {
                    Error = $"There is no certificate '{Id}' in this store.";
                    return false;
                }

                var fullPath = FullPath(entry.FileName);

                try
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
                catch (Exception exception)
                {
                    Error = $"'{entry.FileName}' could not be deleted: {exception.Message}";
                    return false;
                }

                entries.Remove(Id);
                WriteIndex();

                log.Notice($"Certificates: {entry.Label} ({entry.Kind.AsText()}) was deleted from the store.",
                           "certificates");

                return true;

            }
            finally
            {
                storeLock.Release();
            }

        }

        #endregion


        #region Get(Id) / ByKind(Kind) / UsableByKind(Kind)

        /// <summary>
        /// One entry by its handle, or nothing.
        /// </summary>
        public CertificateEntry? Get(String? Id)
        {

            if (Id is null)
                return null;

            storeLock.Wait();

            try
            {
                return entries.GetValueOrDefault(Id);
            }
            finally
            {
                storeLock.Release();
            }

        }

        /// <summary>
        /// Everything of one kind, by label.
        /// </summary>
        public IReadOnlyList<CertificateEntry> ByKind(CertificateKind Kind)

            => [.. Entries.Where(entry => entry.Kind == Kind)];

        /// <summary>
        /// Everything of one kind this vehicle would use right now: switched
        /// on, and inside its own validity.
        /// </summary>
        public IReadOnlyList<CertificateEntry> UsableByKind(CertificateKind Kind)

            => [.. Entries.Where(entry => entry.Kind == Kind && entry.IsUsable)];

        #endregion

        #region FullPath(Entry) / FullPath(FileName)

        /// <summary>
        /// Where one entry's file actually is.
        /// </summary>
        public String FullPath(CertificateEntry Entry)

            => FullPath(Entry.FileName);

        /// <summary>
        /// Where a store-relative name actually is.
        /// </summary>
        public String FullPath(String FileName)

            => Path.Combine(Directory, FileName.Replace('/', Path.DirectorySeparatorChar));

        #endregion

        #region ValidatorFor(Kind)

        /// <summary>
        /// A chain validator over the usable roots of one kind, or nothing
        /// where there are none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing rather than a validator that trusts nobody, and the
        /// difference is the same one <c>TrustRoots.Load</c> makes for the same
        /// reason: "no roots configured" and "roots that vouch for nobody" fail
        /// identically at a handshake and mean entirely different things about
        /// the run. A caller that gets nothing back can say so; one handed an
        /// empty validator can only report a rejection.
        /// </para>
        /// <para>
        /// Expired roots are left out here rather than refused at import. A
        /// root that expires overnight should stop being a trust anchor
        /// overnight, without anybody having to notice.
        /// </para>
        /// </remarks>
        public V2GChainValidator? ValidatorFor(CertificateKind Kind)
        {

            if (!Kind.IsTrustAnchor())
                throw new ArgumentException($"{Kind.AsText()} is not a trust anchor.", nameof(Kind));

            var roots = new X509Certificate2Collection();

            foreach (var entry in UsableByKind(Kind))
            {

                var fullPath = FullPath(entry);

                try
                {
                    roots.Add(X509CertificateLoader.LoadCertificateFromFile(fullPath));
                }
                catch (Exception exception)
                {
                    log.Warning($"Certificates: the {Kind.AsText()} '{entry.Label}' could not be read and is not " +
                                $"vouching for anything - {exception.Message}",
                                "certificates");
                }

            }

            return roots.Count > 0
                       ? new V2GChainValidator(roots)
                       : null;

        }

        #endregion

        #region Summarise()

        /// <summary>
        /// What is in the store, by kind, as one line for a log entry.
        /// </summary>
        private String Summarise()
        {

            var parts = new List<String>();

            foreach (var kind in CertificateKindExtensions.All)
            {

                var all = entries.Values.Where(entry => entry.Kind == kind).ToArray();

                if (all.Length == 0)
                    continue;

                var usable = all.Count(entry => entry.IsUsable);

                parts.Add(usable == all.Length
                              ? $"{all.Length} {kind.AsText()}"
                              : $"{all.Length} {kind.AsText()} ({usable} usable)");

            }

            return parts.Count > 0
                       ? String.Join(", ", parts)
                       : "empty";

        }

        #endregion


        #region (private) CreateDirectories()

        /// <summary>
        /// Make the store and one directory per kind, readable by their owner
        /// alone where the platform has anything to say about it.
        /// </summary>
        private void CreateDirectories()
        {

            System.IO.Directory.CreateDirectory(Directory);
            Protect(Directory);

            foreach (var kind in CertificateKindExtensions.All)
                System.IO.Directory.CreateDirectory(
                    Path.Combine(Directory, kind.Directory().Replace('/', Path.DirectorySeparatorChar))
                );

        }

        #endregion

        #region (private static) Protect(Path)

        /// <summary>
        /// Keep everybody but the owner out, where the platform supports saying
        /// so in one call.
        /// </summary>
        /// <remarks>
        /// Unix only, and silent where it is not available. On Windows a new
        /// directory below the user's profile already inherits an ACL that
        /// grants the user and the administrators and nobody else, and rewriting
        /// that ACL here would be a larger promise than this can keep. The
        /// warning at every start is what covers the difference.
        /// </remarks>
        private static void Protect(String Path)
        {

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {

                var mode = System.IO.Directory.Exists(Path)
                               ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                               : UnixFileMode.UserRead | UnixFileMode.UserWrite;

                File.SetUnixFileMode(Path, mode);

            }
            catch (Exception)
            {
                // A store on a file system with no modes is still a store.
            }

        }

        #endregion

        #region (private static) RelativeName(Kind, FileName)

        /// <summary>
        /// A store-relative name, always with forward slashes so that an index
        /// written on one platform reads on the other.
        /// </summary>
        private static String RelativeName(CertificateKind  Kind,
                                           String           FileName)

            => $"{Kind.Directory()}/{FileName}";

        #endregion

        #region (private static) NameFromFile(Entry)

        /// <summary>
        /// What an entry is called when nobody has named it: its subject's
        /// common name as it was read, falling back to its handle.
        /// </summary>
        private static String NameFromFile(CertificateEntry Entry)
        {

            var common = Entry.Subject.Split(',').
                                       Select (part => part.Trim()).
                                       FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));

            return common is { Length: > 3 }
                       ? common[3..]
                       : Entry.Id;

        }

        #endregion


        #region (private static) ReadCollection(Content, Password)

        /// <summary>
        /// Whatever arrived, as a collection of certificates.
        /// </summary>
        /// <remarks>
        /// PEM first because it is the only one that can be recognised by
        /// looking, then PKCS#12, then a bare DER certificate. The order
        /// matters only for the error somebody sees: a file that is none of
        /// these should be told it is not a certificate, not that its password
        /// was wrong.
        /// </remarks>
        private static X509Certificate2Collection ReadCollection(Byte[]   Content,
                                                                 String?  Password)
        {

            var collection = new X509Certificate2Collection();

            #region PEM, which may hold several

            var asText = LooksLikeText(Content)
                             ? System.Text.Encoding.UTF8.GetString(Content)
                             : null;

            if (asText is not null && asText.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {

                collection.ImportFromPem(asText);

                if (collection.Count == 0)
                    throw new ArgumentException("That file looks like PEM, and holds no certificate this vehicle can read.");

                return collection;

            }

            #endregion

            #region PKCS#12

            try
            {

                // Exportable, because a Vehicle certificate has to be handed to
                // the BouncyCastle backend and an OEM key has to become an ECDH
                // handle - neither of which works on a key loaded without it.
                collection.AddRange(
                    X509CertificateLoader.LoadPkcs12Collection(
                        Content,
                        Password,
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet
                    )
                );

                if (collection.Count > 0)
                    return collection;

            }
            catch (CryptographicException exception)
            {

                // A DER certificate is not a PKCS#12 and fails here first, so
                // this is only an answer once that has been ruled out.
                if (TryReadDer(Content, collection))
                    return collection;

                // And a file that is not DER either was never a candidate for
                // any of this. Saying "wrong password?" about a text file sends
                // somebody looking for a password that does not exist, which is
                // a worse answer than none.
                if (!LooksLikeDer(Content))
                    throw new ArgumentException(
                              "That file is not a certificate this vehicle can read (PEM, DER or PKCS#12).");

                throw new ArgumentException(
                          Password is null
                              ? $"That file could not be read. If it is a password-protected PKCS#12, give the password. ({exception.Message})"
                              : $"That file could not be read - wrong password? ({exception.Message})"
                      );

            }

            #endregion

            if (TryReadDer(Content, collection))
                return collection;

            throw new ArgumentException("That file is not a certificate this vehicle can read (PEM, DER or PKCS#12).");

        }

        #endregion

        #region (private static) TryReadDer(Content, Collection)

        /// <summary>
        /// One bare DER certificate, where that is what it is.
        /// </summary>
        private static Boolean TryReadDer(Byte[]                      Content,
                                          X509Certificate2Collection  Collection)
        {

            try
            {
                Collection.Add(X509CertificateLoader.LoadCertificate(Content));
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }

        }

        #endregion

        #region (private static) LooksLikeDer(Content)

        /// <summary>
        /// Whether a file could be DER at all - that is, whether it begins with
        /// an ASN.1 SEQUENCE.
        /// </summary>
        /// <remarks>
        /// Both a PKCS#12 and a DER certificate do. Used only to decide which
        /// sentence somebody gets back: a file that does not start this way was
        /// never a PKCS#12, so telling them their password might be wrong is
        /// sending them after something that does not exist.
        /// </remarks>
        private static Boolean LooksLikeDer(Byte[] Content)

            => Content.Length > 1 && Content[0] == 0x30;

        #endregion

        #region (private static) LooksLikeText(Content)

        /// <summary>
        /// Whether a file could be PEM at all, which is worth asking before
        /// decoding a megabyte of DER as UTF-8.
        /// </summary>
        private static Boolean LooksLikeText(Byte[] Content)
        {

            foreach (var b in Content.Take(64))
                if (b == 0)
                    return false;

            return true;

        }

        #endregion

        #region (private static) Suits(Leaf, Kind, out Error)

        /// <summary>
        /// Whether a certificate can be what somebody is importing it as.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three checks, each of which stands for a failure that is otherwise
        /// discovered in the middle of a session. A credential without its
        /// private key cannot be presented. A trust anchor that is not
        /// self-signed is not an anchor - <c>X509ChainTrustMode.CustomRootTrust</c>
        /// requires the chain to end in a self-signed certificate that is in the
        /// store, so a Sub-CA imported as a root simply never anchors anything.
        /// And a trust anchor carrying a private key is somebody's CA key in the
        /// wrong place, which is refused rather than quietly stripped.
        /// </para>
        /// <para>
        /// The OEM curve is a warning at import and not a refusal, because
        /// ISO 15118-2 has no such requirement and a P-256 OEM certificate is
        /// perfectly real - it just cannot finish -20's contract provisioning.
        /// The session says so again where it matters.
        /// </para>
        /// </remarks>
        private static Boolean Suits(X509Certificate2                  Leaf,
                                     CertificateKind                   Kind,
                                     [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            if (Kind.NeedsPrivateKey() && !Leaf.HasPrivateKey)
            {
                Error = $"A {Kind.Describe()} has to carry its private key, and that file has none. " +
                         "It is probably a PKCS#12 that needs a password, or the public half of the pair.";
                return false;
            }

            if (Kind.IsTrustAnchor())
            {

                if (Leaf.HasPrivateKey)
                {
                    Error = "That file carries a private key, and a trust anchor must not. " +
                            "Import the certificate on its own.";
                    return false;
                }

                if (!String.Equals(Leaf.Subject, Leaf.Issuer, StringComparison.Ordinal))
                {
                    Error = $"'{CertificateEntry.CommonNameOf(Leaf)}' is signed by somebody else and is therefore " +
                             "a sub-CA rather than a root. Only a self-signed certificate can be a trust anchor; " +
                             "a sub-CA travels with the certificate it signed.";
                    return false;
                }

            }

            return true;

        }

        #endregion

        #region (private static) Serialise(Collection, Leaf, Kind)

        /// <summary>
        /// The bytes to write for one import: PEM for a trust anchor, PKCS#12
        /// without a password for everything else.
        /// </summary>
        /// <remarks>
        /// The leaf is written first so that a reader taking "the first
        /// certificate" gets the one this is about. For a trust anchor there is
        /// only ever the one - the sub-CAs a root arrived with are not anchors
        /// and are dropped rather than promoted.
        /// </remarks>
        private static Byte[] Serialise(X509Certificate2Collection  Collection,
                                        X509Certificate2            Leaf,
                                        CertificateKind             Kind)
        {

            if (Kind.IsTrustAnchor())
                return System.Text.Encoding.ASCII.GetBytes(Leaf.ExportCertificatePem() + Environment.NewLine);

            var ordered = new X509Certificate2Collection(Leaf);

            foreach (var certificate in Collection)
                if (!ReferenceEquals(certificate, Leaf))
                    ordered.Add(certificate);

            return ordered.Export(X509ContentType.Pkcs12, password: (String?) null)
                       ?? throw new CryptographicException("That certificate could not be written as PKCS#12.");

        }

        #endregion

        #region (private static) TryRead(Path, out Certificate, out ChainLength, out Error)

        /// <summary>
        /// One file in the store, as the certificate it is about and how many
        /// others travel with it.
        /// </summary>
        private static Boolean TryRead(String                                  Path,
                                       [NotNullWhen(true)]  out X509Certificate2?  Certificate,
                                       out Int32                               ChainLength,
                                       [NotNullWhen(false)] out String?        Error)
        {

            Certificate  = null;
            ChainLength  = 0;
            Error        = null;

            try
            {

                var collection = ReadCollection(File.ReadAllBytes(Path), Password: null);

                Certificate  = collection.FirstOrDefault(certificate => certificate.HasPrivateKey) ?? collection[0];
                ChainLength  = collection.Count - 1;

                foreach (var other in collection)
                    if (!ReferenceEquals(other, Certificate))
                        other.Dispose();

                return true;

            }
            catch (Exception exception)
            {
                Error = exception.Message;
                return false;
            }

        }

        #endregion

        #region (private static) Dispose(Collection)

        /// <summary>
        /// Let go of everything that was read for one import.
        /// </summary>
        private static void Dispose(X509Certificate2Collection Collection)
        {

            foreach (var certificate in Collection)
                certificate.Dispose();

        }

        #endregion


        #region (private) ReadIndex() / WriteIndex()

        /// <summary>
        /// What the index remembers, by handle. A missing index is an empty
        /// one; a damaged index is named and then treated as empty, which costs
        /// labels and switches and no certificates.
        /// </summary>
        private Dictionary<String, CertificateEntry> ReadIndex()
        {

            var remembered  = new Dictionary<String, CertificateEntry>();
            var path        = Path.Combine(Directory, IndexFileName);

            if (!File.Exists(path))
                return remembered;

            try
            {

                var json = JObject.Parse(File.ReadAllText(path));

                if (json["certificates"] is not JArray array)
                    return remembered;

                foreach (var token in array.OfType<JObject>())
                {

                    if (!CertificateEntry.TryParse(token, out var entry, out var error))
                    {
                        log.Warning($"Certificates: the index has an entry this vehicle could not read - {error}",
                                    "certificates");
                        continue;
                    }

                    remembered[entry.Id] = entry;

                }

            }
            catch (Exception exception)
            {
                log.Warning($"Certificates: '{IndexFileName}' could not be read and the store was taken from its " +
                            $"directory alone, so labels and on/off are back to their defaults - {exception.Message}",
                            "certificates");
            }

            return remembered;

        }

        /// <summary>
        /// Write the index. Called from inside <see cref="storeLock"/> by
        /// everything that changes the store.
        /// </summary>
        private void WriteIndex()
        {

            var path = Path.Combine(Directory, IndexFileName);

            try
            {

                var json = new JObject(
                               new JProperty("certificates",
                                   new JArray(
                                       entries.Values.
                                           OrderBy(entry => entry.Kind.SortOrder()).
                                           ThenBy (entry => entry.Label, StringComparer.OrdinalIgnoreCase).
                                           Select (entry => entry.ToJSON())
                                   ))
                           );

                // Written beside and moved over, so that a vehicle killed mid-write
                // comes back to the index it had rather than to half of a new one.
                var temporary = path + ".new";

                File.WriteAllText(temporary, json.ToString(Newtonsoft.Json.Formatting.Indented));
                File.Move(temporary, path, overwrite: true);

                Protect(path);

            }
            catch (Exception exception)
            {
                log.Error($"Certificates: '{IndexFileName}' could not be written, so labels and on/off will not " +
                          $"survive a restart - {exception.Message}",
                          "certificates");
            }

        }

        #endregion

    }

}
