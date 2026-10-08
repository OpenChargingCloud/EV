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
using System.Globalization;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Norn.NTS;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.ISO15118.SDP.Messages;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.EV.Configuration;
using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.EV
{

    /// <summary>
    /// What the Configuration pages of the web interface read and write.
    /// </summary>
    /// <remarks>
    /// Every change here takes effect at once and is written to the
    /// configuration file, in that order of importance and in the opposite
    /// order of doing: the file is written first, because a change that was
    /// applied but not written down is a change that disappears at the next
    /// start without anybody noticing, and that is the worse of the two
    /// failures. A file that was written but could not be applied is the lesser
    /// one - it says so loudly, and a restart makes it true.
    ///
    /// Nothing here decides who may call it. That is the API's business, and it
    /// asks the node before it calls: see <see cref="VehicleAccess"/> for the
    /// resources and roles of a vehicle.
    /// </remarks>
    public partial class EV
    {

        #region Vehicle

        #region VehicleConfigurationJSON()

        /// <summary>
        /// What this vehicle is, and what its battery wants.
        /// </summary>
        public JObject VehicleConfigurationJSON()

            => new (

                   new JProperty("name",                         VehicleName),
                   new JProperty("vin",                          VIN),

                   new JProperty("battery",                      new JObject(
                       new JProperty("capacityKWh",                  BatteryCapacity_kWh),
                       new JProperty("stateOfChargePercent",         StateOfCharge_percent),
                       new JProperty("targetStateOfChargePercent",   TargetStateOfCharge_percent),
                       new JProperty("maxChargingPowerKW",           MaxChargingPower_kW),
                       new JProperty("taperFromPercent",             TaperFrom_percent)
                   )),

                   new JProperty("limits",                       new JObject(
                       new JProperty("maxCapacityKWh",               VehicleConfiguration.MaxCapacity_kWh),
                       new JProperty("maxPowerKW",                   VehicleConfiguration.MaxPower_kW),
                       new JProperty("maxNameLength",                VehicleConfiguration.MaxNameLength)
                   )),

                   new JProperty("file",                         ConfigFile.Path)

               );

        #endregion

        #region TryUpdateVehicleConfiguration(JSON, out Error [, out NotSaved])

        /// <summary>
        /// Change what this vehicle says about itself, at once and for the
        /// next session it runs.
        /// </summary>
        /// <remarks>
        /// None of this reaches a station until the next session starts, and
        /// that is the whole reason it may be changed while one is running: a
        /// battery figure edited mid-session would otherwise turn into a
        /// vehicle that asked for one thing and then metered another.
        /// </remarks>
        public Boolean TryUpdateVehicleConfiguration(JObject                           JSON,
                                                     [NotNullWhen(false)] out String?  Error)

            => TryUpdateVehicleConfiguration(JSON, out Error, out _);

        /// <summary>
        /// Change what this vehicle says about itself - and say whether a
        /// refusal was the file's rather than the change's.
        /// </summary>
        /// <param name="JSON">What the page sent, in the shape of the "vehicle" section.</param>
        /// <param name="Error">Why nothing was changed.</param>
        /// <param name="NotSaved">True where the configuration file could not be read or written: nothing about the change was wrong, and nothing was changed.</param>
        public Boolean TryUpdateVehicleConfiguration(JObject                           JSON,
                                                     [NotNullWhen(false)] out String?  Error,
                                                     out Boolean                       NotSaved)
        {

            NotSaved = false;

            if (!VehicleConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                // Everything this refuses is the file's: read, merged, written.
                if (!ConfigFile.TryMergeSection(VehicleConfiguration.SectionName, configuration.ToJSON(), out Error))
                {
                    NotSaved = true;
                    return false;
                }

                ApplyVehicleConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyVehicleConfiguration(Configuration)

        /// <summary>
        /// Put a vehicle section into effect. What it does not mention is left
        /// as it is.
        /// </summary>
        private void ApplyVehicleConfiguration(VehicleConfiguration Configuration)
        {

            var changed = new List<String>();

            if (Configuration.Name is not null && VehicleName != Configuration.Name)
            {
                VehicleName = Configuration.Name;
                changed.Add($"name = {VehicleName}");
            }

            if (Configuration.VIN is not null && VIN != Configuration.VIN)
            {
                VIN = Configuration.VIN;
                changed.Add($"VIN = {VIN}");
            }

            if (Configuration.BatteryCapacity_kWh.HasValue && BatteryCapacity_kWh != Configuration.BatteryCapacity_kWh.Value)
            {
                BatteryCapacity_kWh = Configuration.BatteryCapacity_kWh.Value;
                changed.Add(String.Create(CultureInfo.InvariantCulture, $"battery = {BatteryCapacity_kWh:F1} kWh"));
            }

            if (Configuration.StateOfCharge_percent.HasValue && StateOfCharge_percent != Configuration.StateOfCharge_percent.Value)
            {
                StateOfCharge_percent = Configuration.StateOfCharge_percent.Value;
                changed.Add($"state of charge = {StateOfCharge_percent:F0} %");
            }

            if (Configuration.TargetStateOfCharge_percent.HasValue && TargetStateOfCharge_percent != Configuration.TargetStateOfCharge_percent.Value)
            {
                TargetStateOfCharge_percent = Configuration.TargetStateOfCharge_percent.Value;
                changed.Add($"target state of charge = {TargetStateOfCharge_percent:F0} %");
            }

            if (Configuration.MaxChargingPower_kW.HasValue && MaxChargingPower_kW != Configuration.MaxChargingPower_kW.Value)
            {
                MaxChargingPower_kW = Configuration.MaxChargingPower_kW.Value;
                changed.Add(String.Create(CultureInfo.InvariantCulture, $"charging power = {MaxChargingPower_kW:F1} kW"));
            }

            if (Configuration.TaperFrom_percent.HasValue && TaperFrom_percent != Configuration.TaperFrom_percent.Value)
            {
                TaperFrom_percent = Configuration.TaperFrom_percent.Value;
                changed.Add($"taper from = {TaperFrom_percent:F0} %");
            }

            if (changed.Count > 0)
                Log.Notice($"Vehicle configuration changed: {String.Join(", ", changed)}.", "vehicle", "config");

        }

        #endregion

        #endregion

        #region V2G

        #region V2GConfigurationJSON()

        /// <summary>
        /// The wire below the charging cable, from this vehicle's side: which
        /// interface it would broadcast on, what it asks for, and which
        /// interfaces the machine actually has.
        /// </summary>
        /// <remarks>
        /// The list of candidates is part of the answer rather than a resource
        /// of its own, because the one question somebody on this page has is
        /// "which of these is the powerline modem" - and a page that had to ask
        /// twice would show the setting and the list out of step with each
        /// other.
        /// </remarks>
        public JObject V2GConfigurationJSON()

            => new (

                   new JProperty("interface",                    V2GSettings.InterfaceName),

                   new JProperty("interfaces",                   new JArray(
                       V2GLink.Candidates().Select(candidate => new JObject(
                           new JProperty("name",        candidate.Name),
                           new JProperty("index",       candidate.Index),
                           new JProperty("linkLocal",   V2GLink.Scoped(candidate)),
                           new JProperty("mac",         Convert.ToHexString(candidate.MACAddress))
                       ))
                   )),

                   new JProperty("settings",                     new JObject(
                       new JProperty("requestedSecurity",            V2GConfiguration.NameOf(V2GSettings.RequestedSecurity ?? SDP_Security.TLS)),
                       new JProperty("perAttemptTimeoutSeconds",     (V2GSettings.PerAttemptTimeout ?? V2GLink.DefaultPerAttemptTimeout).TotalSeconds),
                       new JProperty("maxRetries",                   V2GSettings.MaxRetries    ?? V2GLink.DefaultMaxRetries),
                       new JProperty("totalDeadlineSeconds",         (V2GSettings.TotalDeadline ?? V2GLink.DefaultTotalDeadline).TotalSeconds),
                       new JProperty("rejectNoTLSResponses",         V2GSettings.RejectNoTLSResponses        ?? true),
                       new JProperty("requireLinkLocalSECCAddress",  V2GSettings.RequireLinkLocalSECCAddress ?? true),
                       new JProperty("multicastLoopback",            V2GSettings.MulticastLoopback           ?? false)
                   )),

                   new JProperty("lastDiscovery",                lastDiscovery),

                   new JProperty("file",                         ConfigFile.Path)

               );

        #endregion

        #region TryUpdateV2GConfiguration(JSON, out Error [, out NotSaved])

        /// <summary>
        /// Change what the next discovery does.
        /// </summary>
        /// <remarks>
        /// Nothing here starts one. Changing the interface a vehicle would
        /// broadcast on is a setting; broadcasting is an act, and the two are
        /// deliberately different requests under different permissions:
        /// "v2g:edit" for this, "v2g:run" for a discovery - see
        /// <see cref="VehicleAccess.V2G"/>.
        /// </remarks>
        public Boolean TryUpdateV2GConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)

            => TryUpdateV2GConfiguration(JSON, out Error, out _);

        /// <summary>
        /// Change what the next discovery does - and say whether a refusal was
        /// the file's rather than the change's.
        /// </summary>
        /// <param name="JSON">What the page sent, in the shape of the "v2g" section.</param>
        /// <param name="Error">Why nothing was changed.</param>
        /// <param name="NotSaved">True where the configuration file could not be read or written: nothing about the change was wrong, and nothing was changed.</param>
        public Boolean TryUpdateV2GConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error,
                                                 out Boolean                       NotSaved)
        {

            NotSaved = false;

            if (!V2GConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            // An interface named here that the machine does not have is refused
            // rather than kept for later: the alternative is a setting that
            // looks saved and fails at the one moment somebody presses the
            // button, with the reason five minutes behind them in the log.
            if (configuration.InterfaceName is not null &&
                V2GLink.FindInterface(configuration.InterfaceName) is null)
            {

                var candidates = V2GLink.Candidates();

                Error = $"'{configuration.InterfaceName}' is not an interface of this machine that could carry V2G traffic. " +
                        (candidates.Count > 0
                             ? $"Candidates: {String.Join(", ", candidates.Select(candidate => candidate.Name))}."
                             : "This machine has none: an interface needs to be up, have a MAC address and an IPv6 link-local address.");

                return false;

            }

            reconfigureLock.Wait();

            try
            {

                // Everything this refuses is the file's: read, merged, written.
                if (!ConfigFile.TryMergeSection(V2GConfiguration.SectionName, configuration.ToJSON(), out Error))
                {
                    NotSaved = true;
                    return false;
                }

                ApplyV2GConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyV2GConfiguration(Configuration)

        /// <summary>
        /// Put a V2G section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyV2GConfiguration(V2GConfiguration Configuration)
        {

            var previous = V2GSettings;
            var changed  = new List<String>();

            if (Configuration.InterfaceName is not null && previous.InterfaceName != Configuration.InterfaceName)
                changed.Add($"interface = {Configuration.InterfaceName}");

            else if (Configuration.NoInterface && previous.InterfaceName is not null)
                changed.Add("interface = whichever one comes first");

            if (Configuration.RequestedSecurity.HasValue && previous.RequestedSecurity != Configuration.RequestedSecurity)
                changed.Add($"asking for {V2GConfiguration.NameOf(Configuration.RequestedSecurity.Value)}");

            if (Configuration.PerAttemptTimeout.HasValue && previous.PerAttemptTimeout != Configuration.PerAttemptTimeout)
                changed.Add($"per-attempt timeout = {Configuration.PerAttemptTimeout.Value.TotalMilliseconds:F0} ms");

            if (Configuration.MaxRetries.HasValue && previous.MaxRetries != Configuration.MaxRetries)
                changed.Add($"max retries = {Configuration.MaxRetries.Value}");

            if (Configuration.TotalDeadline.HasValue && previous.TotalDeadline != Configuration.TotalDeadline)
                changed.Add($"deadline = {Configuration.TotalDeadline.Value.TotalSeconds:F0} s");

            if (Configuration.RejectNoTLSResponses.HasValue && previous.RejectNoTLSResponses != Configuration.RejectNoTLSResponses)
                changed.Add($"reject non-TLS answers = {Configuration.RejectNoTLSResponses.Value}");

            if (Configuration.RequireLinkLocalSECCAddress.HasValue && previous.RequireLinkLocalSECCAddress != Configuration.RequireLinkLocalSECCAddress)
                changed.Add($"require a link-local SECC address = {Configuration.RequireLinkLocalSECCAddress.Value}");

            if (Configuration.MulticastLoopback.HasValue && previous.MulticastLoopback != Configuration.MulticastLoopback)
                changed.Add($"multicast loopback = {Configuration.MulticastLoopback.Value}");

            // Field by field rather than by replacing the record, so that a
            // page sending three of the eight settings does not silently reset
            // the other five to whatever they were at construction.
            V2GSettings = previous with {

                              // NoInterface is the one field that is not
                              // "keep what was there": it exists precisely to
                              // take a named interface back.
                              InterfaceName                = Configuration.NoInterface
                                                                 ? null
                                                                 : Configuration.InterfaceName         ?? previous.InterfaceName,

                              // Only while it still holds: naming an interface
                              // is what takes "whichever one comes first" back
                              // again, and a flag that survived that would keep
                              // forcing the name it just set to null.
                              NoInterface                  = Configuration.NoInterface ||
                                                             (Configuration.InterfaceName is null && previous.NoInterface),
                              RequestedSecurity            = Configuration.RequestedSecurity           ?? previous.RequestedSecurity,
                              PerAttemptTimeout            = Configuration.PerAttemptTimeout           ?? previous.PerAttemptTimeout,
                              MaxRetries                   = Configuration.MaxRetries                  ?? previous.MaxRetries,
                              TotalDeadline                = Configuration.TotalDeadline               ?? previous.TotalDeadline,
                              RejectNoTLSResponses         = Configuration.RejectNoTLSResponses        ?? previous.RejectNoTLSResponses,
                              RequireLinkLocalSECCAddress  = Configuration.RequireLinkLocalSECCAddress ?? previous.RequireLinkLocalSECCAddress,
                              MulticastLoopback            = Configuration.MulticastLoopback           ?? previous.MulticastLoopback
                          };

            if (changed.Count > 0)
                Log.Notice($"V2G configuration changed: {String.Join(", ", changed)}.", "15118", "config");

        }

        #endregion

        #endregion

        #region Certificates

        #region (protected override) CompleteCertificatesJSON(JSON)

        /// <summary>
        /// Which certificate each kind of credential is chosen as for a
        /// session, beside what every node says about its store - see
        /// <see cref="WWCPNode.CertificatesJSON"/> - so that the page can mark
        /// it without also fetching the session settings.
        /// </summary>
        /// <remarks>
        /// The credentials are what a vehicle presents: exactly one of each is
        /// chosen, and that choice is a session setting rather than a property
        /// of the store. Each is said with the kind its slot wants, so that a
        /// certificate kept as several kinds is marked in that kind's row
        /// alone.
        /// </remarks>
        protected override void CompleteCertificatesJSON(JObject JSON)
        {

            JSON["chosen"] = new JObject(
                                 Slots.Select(slot => new JProperty(slot.Field,
                                                                    slot.Handle is null
                                                                        ? null
                                                                        : new JObject(
                                                                              new JProperty("id",    slot.Handle),
                                                                              new JProperty("kind",  slot.Kind.AsText())
                                                                          )))
                             );

        }

        #endregion

        #region (override) WhatUses(Handle, Kind)

        /// <summary>
        /// The session setting that names the certificate of this handle as the
        /// kind it would be taken out as, as the sentence a refusal to delete it
        /// says - or null where none does.
        /// </summary>
        /// <remarks>
        /// Deleting it anyway would leave a vehicle configured to present
        /// something that is not there, which is discovered at the next session
        /// rather than here - and switching it off is what somebody taking a
        /// certificate out of service usually meant. Taken out as another kind
        /// than the one a session names it as, it stays what the session needs.
        /// </remarks>
        public override String? WhatUses(String            Handle,
                                          CertificateKind?  Kind)

            => UsedBySession(Handle, Kind) is String field
                   ? $"That certificate is what 'session.{field}' names. Choose another one there " +
                      "first, or switch this one off instead of deleting it."
                   : null;

        #endregion

        #region UsedBySession(Handle, Kind = null)

        /// <summary>
        /// The session setting that names this certificate as this kind - as
        /// any kind, where none is given - or null where none does.
        /// </summary>
        public String? UsedBySession(String?           Handle,
                                     CertificateKind?  Kind = null)
        {

            if (Handle is null or { Length: 0 })
                return null;

            foreach (var slot in Slots)
                if (slot.Handle == Handle && (Kind is null || slot.Kind == Kind.Value))
                    return slot.Field;

            return null;

        }

        #endregion

        #region (private) Slots

        /// <summary>
        /// The session's four credential slots: the setting, the kind it wants,
        /// and the handle it names.
        /// </summary>
        private IEnumerable<(String Field, CertificateKind Kind, String? Handle)> Slots
        {
            get
            {
                var settings = SessionSettings;
                yield return ("vehicleCertificate",   CertificateKind.Vehicle,             settings.VehicleCertificate);
                yield return ("contractCertificate",  CertificateKind.Contract,            settings.ContractCertificate);
                yield return ("oemCertificate",       CertificateKind.OEMProvisioning,     settings.OEMCertificate);
                yield return ("tariffCertificate",    CertificateKind.TariffVerification,  settings.TariffCertificate);
            }
        }

        #endregion

        #endregion

        #region Session

        #region SessionConfigurationJSON()

        /// <summary>
        /// What this vehicle does once it has found a station, and how the last session went.
        /// </summary>
        /// <remarks>
        /// The certificates appear as handles into the store, and each one is answered with what that
        /// handle currently resolves to - the label, and whether it is usable at all. A page that showed
        /// only the handle would be a page on which a deleted certificate and a working one look the same.
        /// </remarks>
        public JObject SessionConfigurationJSON()

            => new (

                   new JProperty("connect",                  SessionSettings.Connect),

                   new JProperty("settings",                 new JObject(
                       new JProperty("protocol",                 SessionSettings.ProtocolWritten ?? "both"),
                       new JProperty("mode",                     SessionSettings.ModeWritten     ?? "dc"),
                       new JProperty("tls",                      SessionSettings.TLSWritten      ?? "none"),
                       new JProperty("renegotiate",              SessionSettings.Renegotiate     ?? false),
                       new JProperty("slacPeer",                 SessionSettings.SLACPeer),
                       new JProperty("t1sBus",                   SessionSettings.T1SBus),
                       new JProperty("t1sTransport",             SessionSettings.T1STransportInEffect.Write()),
                       new JProperty("t1sInterface",             SessionSettings.T1SInterface),
                       new JProperty("t1sWeight",                SessionSettings.T1SWeightInEffect)
                   )),

                   new JProperty("certificates",             new JObject(
                       new JProperty("pkiDirectory",             SessionSettings.PKIDirectory),
                       new JProperty("vehicleCertificate",       Chosen(SessionSettings.VehicleCertificate,   CertificateKind.Vehicle)),
                       new JProperty("contractCertificate",      Chosen(SessionSettings.ContractCertificate,  CertificateKind.Contract)),
                       new JProperty("oemCertificate",           Chosen(SessionSettings.OEMCertificate,       CertificateKind.OEMProvisioning)),
                       new JProperty("tariffCertificate",        Chosen(SessionSettings.TariffCertificate,    CertificateKind.TariffVerification)),
                       // The roots are not chosen per session - every usable
                       // one of each kind is believed - so what is reported is
                       // how many there are to believe.
                       new JProperty("trustAnchors",             new JObject(
                           CertificateKindExtensions.All.
                               Where (kind => kind.IsTrustAnchor()).
                               Select(kind => new JProperty(kind.AsText(), Certificates.UsableByKind(kind).Count))
                       ))
                   )),

                   new JProperty("goals",                    new JObject(
                       new JProperty("targetEnergyKWh",              SessionSettings.TargetEnergy_kWh),
                       new JProperty("maxChargingTimeSeconds",       SessionSettings.MaxChargingTime?.TotalSeconds),
                       new JProperty("departureInSeconds",           SessionSettings.DepartureIn?.    TotalSeconds),
                       new JProperty("minimumStateOfChargePercent",  SessionSettings.MinimumStateOfCharge_percent)
                   )),

                   new JProperty("running",                  SessionRunning),
                   new JProperty("paused",                   pausedSession is null
                                                                 ? null
                                                                 : Convert.ToHexString(pausedSession.SessionId)),
                   new JProperty("lastSession",              lastSession),

                   new JProperty("file",                     ConfigFile.Path)

               );

        #endregion

        #region (private) Chosen(Handle, Kind)

        /// <summary>
        /// What one of the session's certificate handles currently stands for,
        /// as the kind its slot wants.
        /// </summary>
        /// <remarks>
        /// Null where nothing is chosen, and an object with the handle in it
        /// otherwise - carrying what the store says about it, or
        /// <c>"missing": true</c> where the store has nothing by that name. The
        /// missing case is reported rather than answered as "nothing chosen",
        /// because a certificate somebody deleted out from under a session
        /// setting is a different problem from a setting nobody ever made, and
        /// only one of the two is fixed by choosing something. One kept as
        /// another kind only is told as it is, and not usable: a session would
        /// refuse it.
        /// </remarks>
        private JObject? Chosen(String?          Handle,
                                CertificateKind  Kind)
        {

            if (Handle is null)
                return null;

            var entry = Certificates.Get(Handle, Kind) ?? Certificates.Get(Handle);

            return entry is null
                       ? new JObject(
                             new JProperty("id",       Handle),
                             new JProperty("missing",  true)
                         )
                       : new JObject(
                             new JProperty("id",       entry.Id),
                             new JProperty("label",    entry.Label),
                             new JProperty("subject",  entry.Subject),
                             new JProperty("notAfter", entry.NotAfter.UtcDateTime),
                             new JProperty("usable",   entry.Kind == Kind && entry.IsUsable),
                             new JProperty("missing",  false)
                         );

        }

        #endregion

        #region TryUpdateSessionConfiguration(JSON, out Error [, out NotSaved])

        /// <summary>
        /// Change what the next session does.
        /// </summary>
        /// <remarks>
        /// Nothing here starts one, and nothing here stops one that is running: what a session does is
        /// decided when it starts, so a change made while one runs takes effect at the next. That is not a
        /// restriction but the only honest reading - a vehicle that changed the protocol it was speaking
        /// halfway through a session would be two vehicles.
        ///
        /// A field left out is left alone; a field set to an explicit null is taken back to the default.
        /// See <see cref="Configuration.SessionConfiguration.Clearable"/>.
        /// </remarks>
        public Boolean TryUpdateSessionConfiguration(JObject                           JSON,
                                                     [NotNullWhen(false)] out String?  Error)

            => TryUpdateSessionConfiguration(JSON, out Error, out _);

        /// <summary>
        /// Change what the next session does - and say whether a refusal was
        /// the file's rather than the change's.
        /// </summary>
        /// <param name="JSON">What the page sent, in the shape of the "session" section.</param>
        /// <param name="Error">Why nothing was changed.</param>
        /// <param name="NotSaved">True where the configuration file could not be read or written: nothing about the change was wrong, and nothing was changed.</param>
        public Boolean TryUpdateSessionConfiguration(JObject                           JSON,
                                                     [NotNullWhen(false)] out String?  Error,
                                                     out Boolean                       NotSaved)
        {

            NotSaved = false;

            if (!SessionConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            // Refused here rather than discovered at the handshake: a setting
            // that names a certificate this vehicle does not have is a setting
            // somebody believed they had made.
            //
            // The kind is checked as well as the existence, because the four
            // handles are not interchangeable and a contract certificate put in
            // the Vehicle slot fails as a TLS handshake the station appears to
            // have hung up on.
            foreach (var (field, handle, wanted) in new[] {
                         ("vehicleCertificate",  configuration.VehicleCertificate,  CertificateKind.Vehicle),
                         ("contractCertificate", configuration.ContractCertificate, CertificateKind.Contract),
                         ("oemCertificate",      configuration.OEMCertificate,      CertificateKind.OEMProvisioning),
                         ("tariffCertificate",   configuration.TariffCertificate,   CertificateKind.TariffVerification)
                     })
            {

                if (handle is null)
                    continue;

                // Asked for as the kind the slot wants: one certificate may be
                // kept as several, and the first of them need not be this one.
                if (Certificates.Get(handle, wanted) is not null)
                    continue;

                var entry = Certificates.Get(handle);

                Error = entry is null
                            ? $"'{SessionConfiguration.SectionName}.{field}': there is no certificate '{handle}' " +
                               "in this vehicle's store."
                            : $"'{SessionConfiguration.SectionName}.{field}': '{entry.Label}' is " +
                              $"{entry.Kind.WithArticle()} and this names {wanted.WithArticle()}.";

                return false;

            }

            if (configuration.PKIDirectory is { } pki && !Directory.Exists(pki))
            {
                Error = $"'{SessionConfiguration.SectionName}.pkiDirectory': there is no directory '{pki}'.";
                return false;
            }

            reconfigureLock.Wait();

            try
            {

                // Everything this refuses is the file's: read, merged, written.
                if (!ConfigFile.TryMergeSection(SessionConfiguration.SectionName, configuration.ToJSON(), out Error))
                {
                    NotSaved = true;
                    return false;
                }

                ApplySessionConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplySessionConfiguration(Configuration)

        /// <summary>
        /// Put a session section into effect. What it does not mention is left as it is; what it set to an
        /// explicit null goes back to the default.
        /// </summary>
        private void ApplySessionConfiguration(SessionConfiguration Configuration)
        {

            var previous  = SessionSettings;
            var cleared   = Configuration.Cleared ?? (IReadOnlySet<String>) new HashSet<String>();
            var changed   = new List<String>();

            // Three outcomes per field and not two: named, taken back, or not
            // mentioned. Written once here rather than eighteen times below.
            T? Settle<T>(String Field, T? Now, T? Before)
                => cleared.Contains(Field) ? default : Now ?? Before;

            var connect      = Settle("connect",             Configuration.Connect,             previous.Connect);
            var protocol     = Settle("protocol",            Configuration.Protocol,            previous.Protocol);
            var offerBoth    = Settle("protocol",            Configuration.OfferBoth,           previous.OfferBoth);
            var mode         = Settle("mode",                Configuration.Mode,                previous.Mode);
            var mcs          = Settle("mode",                Configuration.MCS,                 previous.MCS);
            var tls          = Settle("tls",                 Configuration.TLS,                 previous.TLS);
            var pkiDirectory = Settle("pkiDirectory",        Configuration.PKIDirectory,        previous.PKIDirectory);
            var vehicleCert  = Settle("vehicleCertificate",  Configuration.VehicleCertificate,  previous.VehicleCertificate);
            var contractCert = Settle("contractCertificate", Configuration.ContractCertificate, previous.ContractCertificate);
            var oemCert      = Settle("oemCertificate",      Configuration.OEMCertificate,      previous.OEMCertificate);
            var tariffCert   = Settle("tariffCertificate",   Configuration.TariffCertificate,   previous.TariffCertificate);
            var slacPeer     = Settle("slacPeer",            Configuration.SLACPeer,            previous.SLACPeer);
            var t1sBus       = Settle("t1sBus",              Configuration.T1SBus,              previous.T1SBus);
            var t1sTransport = Settle("t1sTransport",        Configuration.T1STransport,        previous.T1STransport);
            var t1sInterface = Settle("t1sInterface",        Configuration.T1SInterface,        previous.T1SInterface);
            var t1sWeight    = Settle("t1sWeight",           Configuration.T1SWeight,           previous.T1SWeight);
            var renegotiate  = Settle("renegotiate",         Configuration.Renegotiate,         previous.Renegotiate);

            var targetEnergy = Settle("targetEnergyKWh",             Configuration.TargetEnergy_kWh,             previous.TargetEnergy_kWh);
            var maxTime      = Settle("maxChargingTimeSeconds",      Configuration.MaxChargingTime,              previous.MaxChargingTime);
            var departure    = Settle("departureInSeconds",          Configuration.DepartureIn,                  previous.DepartureIn);
            var minimumSoC   = Settle("minimumStateOfChargePercent", Configuration.MinimumStateOfCharge_percent, previous.MinimumStateOfCharge_percent);

            SessionSettings = new SessionConfiguration(
                                  connect,
                                  protocol,
                                  offerBoth,
                                  mode,
                                  mcs,
                                  tls,
                                  pkiDirectory,
                                  vehicleCert,
                                  contractCert,
                                  oemCert,
                                  tariffCert,
                                  targetEnergy,
                                  maxTime,
                                  departure,
                                  minimumSoC,
                                  renegotiate,
                                  slacPeer,
                                  t1sBus,
                                  t1sTransport,
                                  t1sInterface,
                                  t1sWeight
                              );

            #region What to say about it

            if (previous.Connect != connect)
                changed.Add($"station = {connect ?? "found over SDP"}");

            if (previous.ProtocolWritten != SessionSettings.ProtocolWritten)
                changed.Add($"protocol = {SessionSettings.ProtocolWritten ?? "both"}");

            if (previous.ModeWritten != SessionSettings.ModeWritten)
                changed.Add($"mode = {SessionSettings.ModeWritten ?? "dc"}");

            if (previous.TLSWritten != SessionSettings.TLSWritten)
                changed.Add($"TLS = {SessionSettings.TLSWritten ?? "none"}");

            if (previous.SLACPeer != slacPeer)
                changed.Add($"SLAC peer = {slacPeer ?? "none"}");

            if (previous.T1SBus != t1sBus)
                changed.Add($"T1S bus = {t1sBus ?? "none"}");

            if (previous.T1STransport != t1sTransport || previous.T1SInterface != t1sInterface || previous.T1SWeight != t1sWeight)
                changed.Add($"T1S transport = {SessionSettings.T1STransportInEffect.Write()}" +
                            (t1sInterface is null ? "" : $" on {t1sInterface}") +
                            $", weight {SessionSettings.T1SWeightInEffect}");

            if (previous.Renegotiate != renegotiate)
                changed.Add($"renegotiate = {renegotiate ?? false}");

            foreach (var (name, was, now) in new (String, String?, String?)[] {
                         ("PKI directory",         previous.PKIDirectory,        pkiDirectory),
                         ("Vehicle certificate",   previous.VehicleCertificate,  vehicleCert),
                         ("contract certificate",  previous.ContractCertificate, contractCert),
                         ("OEM certificate",       previous.OEMCertificate,      oemCert),
                         ("tariff certificate",    previous.TariffCertificate,   tariffCert)
                     })
            {
                // Said by the name somebody gave it rather than by its handle:
                // "contract certificate = 3f2a1c8b..." is a sentence nobody can
                // check without looking the handle up again.
                if (was != now)
                    changed.Add($"{name} = {(now is null ? "none" : Certificates.Get(now)?.Label ?? now)}");
            }

            if (previous.TargetEnergy_kWh != targetEnergy)
                changed.Add($"target energy = {(targetEnergy.HasValue ? String.Create(CultureInfo.InvariantCulture, $"{targetEnergy.Value:F1} kWh") : "none")}");

            if (previous.MaxChargingTime != maxTime)
                changed.Add($"charging time = {(maxTime.HasValue ? $"{maxTime.Value.TotalMinutes:F0} min" : "no limit")}");

            if (previous.DepartureIn != departure)
                changed.Add($"departure = {(departure.HasValue ? $"in {departure.Value.TotalMinutes:F0} min" : "none")}");

            if (previous.MinimumStateOfCharge_percent != minimumSoC)
                changed.Add($"minimum state of charge = {(minimumSoC.HasValue ? $"{minimumSoC.Value:F0} %" : "none")}");

            if (changed.Count > 0)
                Log.Notice($"Session configuration changed: {String.Join(", ", changed)}.", "15118", "config");

            #endregion

        }

        #endregion

        #endregion

    }

}
