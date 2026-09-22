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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.NTS;



using cloud.charging.open.protocols.ISO15118.SDP.Messages;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.EV.Certificates;
using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.ISO15118;

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
    /// asks before it calls: see the permissions in
    /// <see cref="Web.UserRole"/>.
    /// </remarks>
    public partial class EV
    {

        #region Data

        /// <summary>
        /// How the last time synchronisation went, as the web interface reads
        /// it, or null while none has been asked for.
        /// </summary>
        private JObject? lastTimeSync;

        /// <summary>
        /// The record types the DNS test offers, of the several hundred that
        /// exist. Anything else may still be typed - this is the list of what
        /// somebody is likely to want, not of what is allowed.
        /// </summary>
        private static readonly DNSResourceRecordTypes[] commonRecordTypes = [
            DNSResourceRecordTypes.A,
            DNSResourceRecordTypes.AAAA,
            DNSResourceRecordTypes.CNAME,
            DNSResourceRecordTypes.MX,
            DNSResourceRecordTypes.NS,
            DNSResourceRecordTypes.TXT,
            DNSResourceRecordTypes.SOA,
            DNSResourceRecordTypes.SRV,
            DNSResourceRecordTypes.PTR,
            DNSResourceRecordTypes.CAA,
            DNSResourceRecordTypes.TLSA,
            DNSResourceRecordTypes.DNSKEY,
            DNSResourceRecordTypes.DS,
            DNSResourceRecordTypes.HTTPS,
            DNSResourceRecordTypes.SVCB
        ];

        #endregion


        #region DNS

        #region DNSConfigurationJSON()

        /// <summary>
        /// How this vehicle resolves names.
        /// </summary>
        public JObject DNSConfigurationJSON()

            => new (

                   new JProperty("enabled",           DNSEnabled),

                   // The servers this vehicle would ask, which is not the same
                   // as the ones the client holds: switched off, it holds none.
                   new JProperty("servers",           new JArray(
                       configuredDNSServers.Select(DNSConfiguration.ServerJSON)
                   )),

                   new JProperty("settings",          new JObject(
                       new JProperty("queryTimeoutSeconds",  dnsClient.QueryTimeout.TotalSeconds),
                       new JProperty("recursionDesired",     dnsClient.RecursionDesired),
                       new JProperty("useCache",             dnsClient.UseCache),
                       new JProperty("dnssecOK",             dnsClient.DnssecOK),
                       new JProperty("followCNAMEs",         dnsClient.FollowCNAMEs),
                       new JProperty("maxCNAMEFollows",      dnsClient.MaxCNAMEFollows),
                       new JProperty("maxRetries",           dnsClient.MaxRetries)
                   )),

                   // What was decided when the client was made and is not on
                   // offer here; shown so that the page does not read as if
                   // these were the only settings there are.
                   new JProperty("fixed",             new JObject(
                       new JProperty("udpPayloadSize",       dnsClient.UDPPayloadSize),
                       new JProperty("ednsOptions",          dnsClient.EDNSOptions.Count),
                       new JProperty("clientSubnet",         dnsClient.ClientSubnet is null
                                                                 ? null
                                                                 : $"{dnsClient.ClientSubnet.Address}/{dnsClient.ClientSubnet.SourcePrefixLength}"),
                       new JProperty("cacheCleanUpEvery",    dnsClient.DNSCache.CleanUpEvery.    ToString()),
                       new JProperty("negativeCacheTTL",     dnsClient.DNSCache.NegativeCacheTTL.ToString())
                   )),

                   new JProperty("limits",            new JObject(
                       new JProperty("maxServers",           DNSConfiguration.MaxServers),
                       new JProperty("maxQueryTimeout",      DNSConfiguration.MaxQueryTimeoutSeconds),
                       new JProperty("transports",           new JArray(Enum.GetNames<DNSTransport>())),
                       new JProperty("recordTypes",          new JArray(commonRecordTypes.Select(recordType => recordType.ToString())))
                   )),

                   new JProperty("file",              ConfigFile.Path)

               );

        #endregion

        #region TryUpdateDNSConfiguration(JSON, out Error)

        /// <summary>
        /// Change how this vehicle resolves names, at once and for everything
        /// that was handed its DNS client.
        /// </summary>
        /// <remarks>
        /// The request has the same shape as the "dns" section of the
        /// configuration file, on purpose: one vocabulary for the file and for
        /// the web interface means one parser, and nothing that is expressible
        /// in one and not in the other.
        ///
        /// What the request does not mention is not changed and not erased from
        /// the file - a page that only offers the checkboxes may send only the
        /// checkboxes without taking the name servers with it.
        /// </remarks>
        public Boolean TryUpdateDNSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            if (!DNSConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            if (configuration.Servers is { Count: 0 })
            {
                Error = "A vehicle that resolves no names cannot reach anything. Switch name resolution off instead of emptying the list.";
                return false;
            }

            // Under the same lock as every other change to this vehicle:
            // writing a section is a read, a change and a write of one file,
            // and two browsers saving different sections at the same moment
            // would otherwise leave one of the two changes in neither.
            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(DNSConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

                ApplyDNSConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyDNSConfiguration(Configuration)

        /// <summary>
        /// Put a DNS section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyDNSConfiguration(DNSConfiguration Configuration)
        {

            var changed = new List<String>();

            if (Configuration.Servers is not null &&
                !configuredDNSServers.SequenceEqual(Configuration.Servers))
            {
                configuredDNSServers = Configuration.Servers;
                changed.Add($"servers = {String.Join(", ", configuredDNSServers)}");
            }

            if (Configuration.Enabled.HasValue && DNSEnabled != Configuration.Enabled.Value)
            {
                DNSEnabled = Configuration.Enabled.Value;
                changed.Add(DNSEnabled ? "switched on" : "switched off");
            }

            // Always, not only when one of the two above changed: the client
            // must end up holding exactly the servers this vehicle means it to
            // hold, and working that out from which halves changed is how the
            // two drift apart.
            dnsClient.SetDNSServers(DNSEnabled ? configuredDNSServers : []);

            if (Configuration.QueryTimeout.HasValue && dnsClient.QueryTimeout != Configuration.QueryTimeout.Value)
            {
                dnsClient.QueryTimeout = Configuration.QueryTimeout.Value;
                changed.Add($"query timeout = {dnsClient.QueryTimeout}");
            }

            if (Configuration.RecursionDesired.HasValue && dnsClient.RecursionDesired != Configuration.RecursionDesired)
            {
                dnsClient.RecursionDesired = Configuration.RecursionDesired;
                changed.Add($"recursion desired = {Configuration.RecursionDesired}");
            }

            if (Configuration.UseCache.HasValue && dnsClient.UseCache != Configuration.UseCache.Value)
            {
                dnsClient.UseCache = Configuration.UseCache.Value;
                changed.Add($"use cache = {dnsClient.UseCache}");
            }

            if (Configuration.DnssecOK.HasValue && dnsClient.DnssecOK != Configuration.DnssecOK.Value)
            {
                dnsClient.DnssecOK = Configuration.DnssecOK.Value;
                changed.Add($"DNSSEC OK = {dnsClient.DnssecOK}");
            }

            if (Configuration.FollowCNAMEs.HasValue && dnsClient.FollowCNAMEs != Configuration.FollowCNAMEs.Value)
            {
                dnsClient.FollowCNAMEs = Configuration.FollowCNAMEs.Value;
                changed.Add($"follow CNAMEs = {dnsClient.FollowCNAMEs}");
            }

            if (Configuration.MaxCNAMEFollows.HasValue && dnsClient.MaxCNAMEFollows != Configuration.MaxCNAMEFollows.Value)
            {
                dnsClient.MaxCNAMEFollows = Configuration.MaxCNAMEFollows.Value;
                changed.Add($"max CNAME follows = {dnsClient.MaxCNAMEFollows}");
            }

            if (Configuration.MaxRetries.HasValue && dnsClient.MaxRetries != Configuration.MaxRetries.Value)
            {
                dnsClient.MaxRetries = Configuration.MaxRetries.Value;
                changed.Add($"max retries = {dnsClient.MaxRetries}");
            }

            if (changed.Count > 0)
                Log.Notice($"DNS configuration changed: {String.Join(", ", changed)}.", "dns", "config");

        }

        #endregion

        #endregion


        #region NTS

        #region NTSConfigurationJSON()

        /// <summary>
        /// Where this vehicle gets the time from, and how the key exchange
        /// behind it is doing.
        /// </summary>
        public JObject NTSConfigurationJSON()
        {

            var pool = ntsClient.CookiePoolDiagnostics;
            var last = ntsClient.LastNTSKEResponse;

            return new JObject(

                       new JProperty("enabled",      NTSEnabled),

                       new JProperty("server",       new JObject(
                           new JProperty("hostname",              ntsClient.Hostname.ToString()),
                           new JProperty("ntsKEPort",             ntsClient.NTSKE_Port.ToUInt16()),
                           new JProperty("ntpPort",               ntsClient.NTP_Port.  ToUInt16()),
                           new JProperty("ipVersionPreference",   ntsClient.IPVersionPreference.ToString()),
                           new JProperty("clientId",              ntsClient.Id)
                       )),

                       new JProperty("settings",     new JObject(
                           new JProperty("timeoutSeconds",        ntsClient.Timeout?.TotalSeconds)
                       )),

                       new JProperty("cookies",      new JObject(
                           new JProperty("available",             pool.AvailableCookieCount),
                           new JProperty("maxPoolSize",           pool.MaxCookiePoolSize),
                           new JProperty("lowWatermark",          pool.LowWatermark),
                           new JProperty("seeded",                pool.SeededCookieCount),
                           new JProperty("received",              pool.CookiesReceived),
                           new JProperty("consumed",              pool.CookiesConsumed),
                           new JProperty("dropped",               pool.DroppedCookieCount),
                           new JProperty("isLow",                 pool.IsLow),
                           new JProperty("isEmpty",               pool.IsEmpty),
                           new JProperty("isFull",                pool.IsFull)
                       )),

                       new JProperty("policy",       new JObject(
                           new JProperty("targetCookieCount",             ntsClient.CookiePoolPolicy.TargetCookieCount),
                           new JProperty("maxPlaceholders",               ntsClient.CookiePoolPolicy.MaxPlaceholders),
                           new JProperty("renegotiateWhenExhausted",      ntsClient.CookiePoolPolicy.RenegotiateWhenExhausted),
                           new JProperty("minimumRenegotiationInterval",  ntsClient.CookiePoolPolicy.MinimumRenegotiationInterval.ToString())
                       )),

                       new JProperty("keyExchange",  new JObject(
                           new JProperty("automatic",                 ntsClient.AutomaticKeyExchanges),
                           new JProperty("aeadAlgorithms",            new JArray(ntsClient.OfferedAEADAlgorithms.Select(algorithm => algorithm.ToString()))),
                           new JProperty("compliantExporterContext",  ntsClient.CompliantAES128GCMSIVExporterContext),
                           new JProperty("lastExchange",              last is null
                                                                          ? null
                                                                          : new JObject(
                                                                                new JProperty("error",     last.ErrorMessage),
                                                                                new JProperty("warnings",  new JArray(last.WarningMessages)),
                                                                                new JProperty("servers",   new JArray(last.NTPv4ServerNames))
                                                                            ))
                       )),

                       // What the group is actually doing, which is what
                       // synchronises this vehicle's clock. The single client
                       // reported above is the one the detailed test configures
                       // itself from, and its cookie pool is not what a
                       // synchronisation spends.
                       new JProperty("timeSources",  new JArray(
                           timeSources.Bands().SelectMany(band => band).Select(source => {

                               var held = timeEngine.KeyExchanges.TryGetValue(source.Hostname, out var state) ? state : null;

                               return new JObject(
                                          new JProperty("hostname",       source.Hostname.ToString()),
                                          new JProperty("priority",       source.Priority),
                                          new JProperty("enabled",        source.Enabled),
                                          new JProperty("cookies",        held?.RemainingCookies),
                                          new JProperty("lastExchange",   held?.LastRefreshed.ToString("o")),
                                          new JProperty("aeadAlgorithm",  held?.NTSKEResponse?.AEADAlgorithm.ToString())
                                      );

                           })
                       )),
                       new JProperty("group",        new JObject(
                           new JProperty("name",                 timeSources.Name),
                           new JProperty("minServers",           timeSources.MinServers),
                           new JProperty("maxDeviationSeconds",  timeSources.MaxDeviation.TotalSeconds)
                       )),
                       new JProperty("lastSync",     lastTimeSync),

                       new JProperty("limits",       new JObject(
                           new JProperty("maxTimeout",  NTSConfiguration.MaxTimeoutSeconds)
                       )),

                       new JProperty("file",         ConfigFile.Path)

                   );

        }

        #endregion

        #region TryUpdateNTSConfiguration(JSON, out Error)

        /// <summary>
        /// Change where this vehicle reads the time.
        /// </summary>
        /// <remarks>
        /// Pointing the vehicle at another server replaces the client rather
        /// than reconfiguring it: the cookies and the keys an NTS client holds
        /// were issued by the host it was made for, and carrying them to a
        /// different one would at best fail and at worst send one server the
        /// key material of another.
        /// </remarks>
        public Boolean TryUpdateNTSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            if (!NTSConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(NTSConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

                ApplyNTSConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyNTSConfiguration(Configuration)

        /// <summary>
        /// Put an NTS section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyNTSConfiguration(NTSConfiguration Configuration)
        {

            // Kept whole: what this method does with the client is only half of
            // it, and the other half - how often to check, and what the
            // operator claims about the server - is read from elsewhere and
            // much later. See EV.Clock.cs.
            ntsSettings = Configuration;

            var changed  = new List<String>();

            #region The group of time servers

            // Rebuilt from the section rather than patched: it is a list, and
            // working out which entry changed in order to report it would say
            // less than naming the servers, which is what happens below.
            var wasAsking  = String.Join(", ", timeSources.Bands().SelectMany(band => band).Select(source => source.Hostname.ToString()));

            timeSources    = Configuration.ToGroup(Configuration.Hostname ?? ntsClient.Hostname);

            var nowAsking  = String.Join(", ", timeSources.Bands().SelectMany(band => band).Select(source => source.Hostname.ToString()));

            if (wasAsking != nowAsking)
                changed.Add($"time servers = {nowAsking}");

            #endregion

            var hostname = Configuration.Hostname  ?? ntsClient.Hostname;
            var ntsKE    = Configuration.NTSKEPort ?? ntsClient.NTSKE_Port;
            var ntp      = Configuration.NTPPort   ?? ntsClient.NTP_Port;

            if (hostname != ntsClient.Hostname ||
                ntsKE    != ntsClient.NTSKE_Port ||
                ntp      != ntsClient.NTP_Port)
            {

                ntsClient = new NTSClient(
                                hostname,
                                NTSKE_Port:    ntsKE,
                                NTP_Port:      ntp,
                                Timeout:       Configuration.Timeout ?? ntsClient.Timeout,
                                DNSClient:     dnsClient,
                                TimeProvider:  TimeProvider
                            );

                // The old client's cookies went with it, so what the page shows
                // about the last exchange belongs to a server this vehicle no
                // longer asks.
                lastTimeSync = null;

                changed.Add($"server = {hostname}:{ntsKE} (NTS-KE), :{ntp} (NTP)");

            }

            else if (Configuration.Timeout.HasValue && ntsClient.Timeout != Configuration.Timeout.Value)
            {
                ntsClient.Timeout = Configuration.Timeout.Value;
                changed.Add($"timeout = {Configuration.Timeout.Value}");
            }

            if (Configuration.Enabled.HasValue && NTSEnabled != Configuration.Enabled.Value)
            {
                NTSEnabled = Configuration.Enabled.Value;
                changed.Add(NTSEnabled ? "switched on" : "switched off");
            }

            if (changed.Count > 0)
                Log.Notice($"NTS configuration changed: {String.Join(", ", changed)}.", "nts", "config");

        }

        #endregion

        #endregion



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

        #region TryUpdateVehicleConfiguration(JSON, out Error)

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
        {

            if (!VehicleConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(VehicleConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

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
                changed.Add($"battery = {BatteryCapacity_kWh:F1} kWh");
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
                changed.Add($"charging power = {MaxChargingPower_kW:F1} kW");
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

        #region TryUpdateV2GConfiguration(JSON, out Error)

        /// <summary>
        /// Change what the next discovery does.
        /// </summary>
        /// <remarks>
        /// Nothing here starts one. Changing the interface a vehicle would
        /// broadcast on is a setting; broadcasting is an act, and the two are
        /// deliberately different requests under different permissions - see
        /// <see cref="Web.Permissions.RunDiagnostics"/>.
        /// </remarks>
        public Boolean TryUpdateV2GConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

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

                if (!ConfigFile.TryMergeSection(V2GConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

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

        #region CertificatesJSON()

        /// <summary>
        /// Everything in this vehicle's certificate store, grouped the way it is shown.
        /// </summary>
        /// <remarks>
        /// Two groups and not one list. The roots are what this vehicle <i>believes</i>: any number of each
        /// kind may be on at once, and none of them is ever chosen for a session. The credentials are what
        /// it <i>presents</i>: exactly one of each is chosen, and that choice is a session setting rather
        /// than a property of the store. A page that put them in one table would have to explain that
        /// difference in a column heading.
        ///
        /// Which handle each kind of credential is currently chosen by is answered here as well, so that
        /// the page can mark it without also fetching the session settings.
        /// </remarks>
        public JObject CertificatesJSON()
        {

            var byKind = new JObject();

            foreach (var kind in CertificateKindExtensions.All)
                byKind.Add(kind.AsText(),
                           new JArray(Certificates.ByKind(kind).Select(entry => entry.ToJSON(WithDiagnostics: true))));

            return new JObject(

                       new JProperty("directory",    Certificates.Directory),

                       new JProperty("trustAnchors", new JArray(
                           CertificateKindExtensions.All.Where(kind =>  kind.IsTrustAnchor()).Select(kind => kind.AsText())
                       )),

                       new JProperty("credentials",  new JArray(
                           CertificateKindExtensions.All.Where(kind => !kind.IsTrustAnchor()).Select(kind => kind.AsText())
                       )),

                       new JProperty("kinds",        new JObject(
                           CertificateKindExtensions.All.Select(kind =>
                               new JProperty(kind.AsText(), new JObject(
                                   new JProperty("description",     kind.Describe()),
                                   new JProperty("trustAnchor",     kind.IsTrustAnchor()),
                                   new JProperty("needsPrivateKey", kind.NeedsPrivateKey())
                               )))
                       )),

                       new JProperty("certificates", byKind),

                       new JProperty("chosen",       new JObject(
                           new JProperty("vehicleCertificate",   SessionSettings.VehicleCertificate),
                           new JProperty("contractCertificate",  SessionSettings.ContractCertificate),
                           new JProperty("oemCertificate",       SessionSettings.OEMCertificate),
                           new JProperty("tariffCertificate",    SessionSettings.TariffCertificate)
                       )),

                       // Said here because this is the page where somebody is looking at the
                       // consequences of it, rather than only in the log at a start.
                       new JProperty("keysAreUnencrypted", Certificates.Entries.Any(entry => entry.HasPrivateKey))

                   );

        }

        #endregion

        #region UsedBySession(Handle)

        /// <summary>
        /// The session setting that names this certificate, or null where none does.
        /// </summary>
        public String? UsedBySession(String? Handle)
        {

            if (Handle is null or { Length: 0 })
                return null;

            var settings = SessionSettings;

            if (settings.VehicleCertificate  == Handle)  return "vehicleCertificate";
            if (settings.ContractCertificate == Handle)  return "contractCertificate";
            if (settings.OEMCertificate      == Handle)  return "oemCertificate";
            if (settings.TariffCertificate   == Handle)  return "tariffCertificate";

            return null;

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
                       new JProperty("vehicleCertificate",       Chosen(SessionSettings.VehicleCertificate)),
                       new JProperty("contractCertificate",      Chosen(SessionSettings.ContractCertificate)),
                       new JProperty("oemCertificate",           Chosen(SessionSettings.OEMCertificate)),
                       new JProperty("tariffCertificate",        Chosen(SessionSettings.TariffCertificate)),
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

        #region (private) Chosen(Handle)

        /// <summary>
        /// What one of the session's certificate handles currently stands for.
        /// </summary>
        /// <remarks>
        /// Null where nothing is chosen, and an object with the handle in it
        /// otherwise - carrying what the store says about it, or
        /// <c>"missing": true</c> where the store has nothing by that name. The
        /// missing case is reported rather than answered as "nothing chosen",
        /// because a certificate somebody deleted out from under a session
        /// setting is a different problem from a setting nobody ever made, and
        /// only one of the two is fixed by choosing something.
        /// </remarks>
        private JObject? Chosen(String? Handle)
        {

            if (Handle is null)
                return null;

            var entry = Certificates.Get(Handle);

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
                             new JProperty("usable",   entry.IsUsable),
                             new JProperty("missing",  false)
                         );

        }

        #endregion

        #region TryUpdateSessionConfiguration(JSON, out Error)

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
        {

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

                var entry = Certificates.Get(handle);

                if (entry is null)
                {
                    Error = $"'{SessionConfiguration.SectionName}.{field}': there is no certificate '{handle}' " +
                             "in this vehicle's store.";
                    return false;
                }

                if (entry.Kind != wanted)
                {
                    Error = $"'{SessionConfiguration.SectionName}.{field}': '{entry.Label}' is a " +
                            $"{entry.Kind.AsText()} and this names a {wanted.AsText()}.";
                    return false;
                }

            }

            if (configuration.PKIDirectory is { } pki && !Directory.Exists(pki))
            {
                Error = $"'{SessionConfiguration.SectionName}.pkiDirectory': there is no directory '{pki}'.";
                return false;
            }

            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(SessionConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

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
                changed.Add($"target energy = {(targetEnergy.HasValue ? $"{targetEnergy.Value:F1} kWh" : "none")}");

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
