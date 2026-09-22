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

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.Discovery;
using cloud.charging.open.protocols.ISO15118.NetworkInterfaces;
using cloud.charging.open.protocols.ISO15118.SDP.Client;
using cloud.charging.open.protocols.ISO15118.SDP.Messages;
using cloud.charging.open.protocols.ISO15118.SharedCC;
using cloud.charging.open.protocols.ISO15118.Slac;
using cloud.charging.open.protocols.ISO15118.SLAC.StateMachine;
using cloud.charging.open.protocols.ISO15118.SLAC.Transport;
using cloud.charging.open.protocols.ISO15118.T1S;
using cloud.charging.open.protocols.ISO15118.T1S.PLCA;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.Logging;

// System.Diagnostics carries an EventLog of its own, and this project's is the
// one every line below means.
using Stopwatch = System.Diagnostics.Stopwatch;

#endregion

namespace cloud.charging.open.EV.ISO15118
{

    /// <summary>
    /// What came of one SDP discovery: what the page shows, and - where a station answered usably - where
    /// to connect to it.
    /// </summary>
    /// <remarks>
    /// Two fields rather than one, because the JSON is for reading and the endpoint is for using, and
    /// digging the second back out of the first is how an address loses its scope id. <see cref="Found"/>
    /// is null for every outcome but "found".
    /// </remarks>
    /// <param name="JSON">The discovery, as the web interface reads it.</param>
    /// <param name="Found">The station to connect to, with the discovery interface's scope id attached.</param>
    public sealed record DiscoveryOutcome(JObject       JSON,
                                          SeccEndpoint? Found);


    /// <summary>
    /// The wire below the charging cable, from this vehicle's side: the
    /// interfaces it could speak V2G on, the SLAC pairing that decides which
    /// station is at the end of its cable, and the SECC Discovery Protocol that
    /// then finds that station's V2G endpoint.
    /// </summary>
    /// <remarks>
    /// The station's side of this is a listener that is either up or down for
    /// the lifetime of the process - a station answers SDP because it is a
    /// station. A vehicle's side is not: it broadcasts once, when somebody
    /// plugs a cable in or presses a button, and then has nothing to say until
    /// the next time. So this is a handful of static operations rather than a
    /// running object, and there is no <c>Start</c> on it.
    ///
    /// Everything here is one exchange from beginning to end: one socket,
    /// opened for it and closed after it. Two discoveries at once on one socket
    /// are explicitly not supported by <see cref="EVCC_SDPClient"/>, and a
    /// vehicle has no reason to run two - see <see cref="EV.DiscoverAsync"/>,
    /// which is where that is enforced.
    ///
    /// The session that follows is <see cref="V2GSession"/>, which is the one
    /// thing on this side that is not a single exchange.
    /// </remarks>
    public static class V2GLink
    {

        #region Data

        /// <summary>
        /// How long one attempt waits for an answer before the next request
        /// goes out, when nobody says otherwise.
        /// </summary>
        /// <remarks>
        /// 250 ms, which is what [V2G2-159] asks a vehicle to repeat at.
        /// </remarks>
        public static readonly TimeSpan  DefaultPerAttemptTimeout  = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How many attempts at most, when nobody says otherwise.
        /// </summary>
        /// <remarks>
        /// Well short of the 240 that 60 s at 250 ms would be: this is a
        /// vehicle somebody is watching a page for, and fifteen attempts over
        /// four seconds is long enough to find a station that is there and
        /// short enough that one that is not says so while the browser is
        /// still open.
        /// </remarks>
        public const           Int32     DefaultMaxRetries         = 15;

        /// <summary>
        /// How long the whole discovery may take, however many attempts that
        /// turns out to be.
        /// </summary>
        public static readonly TimeSpan  DefaultTotalDeadline      = TimeSpan.FromSeconds(10);

        private static readonly SystemV2GNetworkInterfaceProvider  interfaces = new ();

        #endregion


        #region Candidates()

        /// <summary>
        /// Every interface of this machine that could carry V2G traffic: up,
        /// with a MAC address and an IPv6 link-local address.
        /// </summary>
        /// <remarks>
        /// Asked afresh every time rather than kept, because a powerline modem
        /// is a USB device on most benches and the answer changes while the
        /// process is running.
        /// </remarks>
        public static IReadOnlyList<V2GNetworkInterface> Candidates()
        {
            try
            {
                return interfaces.Discover();
            }
            catch
            {
                // Enumerating interfaces is a system call that fails on a
                // machine whose network stack is being reconfigured under it.
                // An empty list is the truthful answer for that moment, and
                // the next call gets the real one.
                return [];
            }
        }

        #endregion

        #region Choose(Candidates)

        /// <summary>
        /// Which of several candidates to use when nobody said which.
        /// </summary>
        /// <remarks>
        /// A V2G port carries IPv6 link-local and nothing else - there is no
        /// IPv4 anywhere in ISO 15118 - while the interface a machine is
        /// administered over practically always has an IPv4 address. So the one
        /// candidate without one is very probably the port with the charging
        /// station behind it, and on the usual two-interface bench that decides
        /// it without anybody configuring anything.
        ///
        /// It stays a guess and is treated as one, but never a guess that is
        /// known to be wrong. With two powerline modems beside one management
        /// interface the question is open between the modems - and answering it
        /// with the management interface, on the grounds that the modems cannot
        /// be told apart, would pick the one candidate that is certainly not the
        /// answer. So the ones without IPv4 are preferred even when there are
        /// several of them, and only a machine where every candidate has an IPv4
        /// address falls back to the first, which is what this gave before there
        /// was any rule.
        ///
        /// Whoever takes this answer says out loud which interface it got and
        /// why, because a guess that does not announce itself is how somebody
        /// spends an afternoon wondering which cable is in use.
        /// </remarks>
        public static V2GNetworkInterface? Choose(IReadOnlyList<V2GNetworkInterface> Candidates)
        {

            if (Candidates.Count == 0)
                return null;

            var withoutIPv4 = Candidates.Where(candidate => !candidate.HasIPv4Address).ToArray();

            return withoutIPv4.Length > 0
                       ? withoutIPv4[0]
                       : Candidates[0];

        }

        #endregion

        #region FindInterface(Name = null)

        /// <summary>
        /// The interface by name, or - with no name - whichever Choose picks.
        /// Null when there is no such interface, or none at all.
        /// </summary>
        public static V2GNetworkInterface? FindInterface(String? Name = null)
        {

            if (String.IsNullOrWhiteSpace(Name))
                return Choose(Candidates());

            try
            {

                var found = interfaces.FindByName(Name.Trim());

                // FindByName answers for any interface of that name, including
                // one that is down or has no link-local address. Such an
                // interface cannot carry SDP, so it is not an answer to this
                // question.
                return found is not null &&
                       Candidates().Any(candidate => candidate.Index == found.Index)
                           ? found
                           : null;

            }
            catch
            {
                return null;
            }

        }

        #endregion

        #region OptionsFor(Interface, Settings)

        /// <summary>
        /// The SDP client options one discovery runs under: what the
        /// configuration says, and the defaults above wherever it says
        /// nothing.
        /// </summary>
        public static EVCC_SDPClientOptions OptionsFor(V2GNetworkInterface  Interface,
                                                       V2GConfiguration     Settings)

            => new () {
                   Interface                    = Interface,
                   RequestedSecurity            = Settings.RequestedSecurity           ?? SDP_Security.TLS,
                   RequestedTransport           = SDP_TransportProtocol.TCP,
                   PerAttemptTimeout            = Settings.PerAttemptTimeout           ?? DefaultPerAttemptTimeout,
                   MaxRetries                   = Settings.MaxRetries                  ?? DefaultMaxRetries,
                   TotalDeadline                = Settings.TotalDeadline               ?? DefaultTotalDeadline,
                   RejectNoTlsResponses         = Settings.RejectNoTLSResponses        ?? true,
                   RequireLinkLocalSeccAddress  = Settings.RequireLinkLocalSECCAddress ?? true,

                   // Every answer, not only the first: a link with two stations
                   // on it is exactly the situation somebody runs this to find
                   // out about, and a client that stopped at the first one
                   // would hide it.
                   DuplicateStrategy            = DuplicateResponseStrategy.CollectAll,

                   MulticastLoopback            = Settings.MulticastLoopback           ?? false
               };

        #endregion

        #region DiscoverAsync(Interface, Settings, Log, CancellationToken = default)

        /// <summary>
        /// One SDP discovery: multicast <c>SDP_Request</c> to <c>ff02::1</c> on
        /// the given interface and report what came back.
        /// </summary>
        /// <remarks>
        /// Every request that goes out and every answer that comes in is
        /// written to the log as it happens rather than collected and reported
        /// at the end, because the whole reason somebody presses this button
        /// with the Logs page open is to watch it: a station answering on the
        /// third attempt and a station answering on the fifteenth are the same
        /// JSON and a very different link.
        ///
        /// Malformed answers are logged and not returned. They are not this
        /// vehicle's business - it asked a question and something on the link
        /// answered nonsense - but they are the single most useful line in the
        /// log when a station is there and discovery keeps timing out.
        /// </remarks>
        /// <param name="Interface">The interface to broadcast on.</param>
        /// <param name="Settings">What to ask for, and how long to keep asking.</param>
        /// <param name="Log">Where the exchange is written as it happens.</param>
        /// <param name="CancellationToken">Abort the discovery.</param>
        public static async Task<DiscoveryOutcome> DiscoverAsync(V2GNetworkInterface  Interface,
                                                                 V2GConfiguration     Settings,
                                                                 EventLog             Log,
                                                                 CancellationToken    CancellationToken   = default)
        {

            var options  = OptionsFor(Interface, Settings);
            var started  = DateTimeOffset.UtcNow;

            Log.Notice(
                $"SDP: asking for a station on '{Interface.Name}' ({Scoped(Interface)}), " +
                $"{V2GMulticast.SDPMulticastGroup} port {V2GMulticast.V2GUdpPort}, " +
                $"asking for {(options.RequestedSecurity == SDP_Security.TLS ? "TLS" : "no TLS")} over TCP; " +
                $"up to {options.MaxRetries} attempt(s) at {options.PerAttemptTimeout.TotalMilliseconds:F0} ms, " +
                $"giving up after {options.TotalDeadline.TotalSeconds:F0} s.",
                "15118", "sdp"
            );

            await using var client = new EVCC_SDPClient(options);

            var sent = 0;

            client.RequestSent += request => {
                sent++;
                Log.Debug(
                    $"SDP: request {sent} sent ({(request.Security == SDP_Security.TLS ? "TLS" : "no TLS")}, {request.TransportProtocol}).",
                    "15118", "sdp"
                );
            };

            client.ResponseReceived += (response, from) => Log.Info(
                $"SDP: {from} answered - the station is at {Describe(response)}.",
                "15118", "sdp"
            );

            client.MalformedResponseReceived += (bytes, from, why) => Log.Warning(
                $"SDP: {from} sent {bytes.Length} byte(s) that are not an SDP response: {why}.",
                "15118", "sdp"
            );

            try
            {

                var result = await client.Discover(CancellationToken);

                switch (result)
                {

                    case SDP_DiscoverySuccess success:

                        var others = success.AdditionalResponses.Count;

                        Log.Notice(
                            $"SDP: found a station at {Describe(success.Response)} after {success.Attempts} attempt(s) " +
                            $"in {success.Elapsed.TotalMilliseconds:F0} ms" +
                            (others > 0 ? $", and {others} other station(s) on the same link." : "."),
                            "15118", "sdp"
                        );

                        return new DiscoveryOutcome(
                                   Answer(started, success.Attempts, success.Elapsed, "found",
                                       new JProperty("secc",   SECCJSON(success.Response, success.RemoteEndpoint)),
                                       new JProperty("others", new JArray(
                                           success.AdditionalResponses.Select(response => SECCJSON(response, null))
                                       ))
                                   ),
                                   // The interface's index, so that a link-local
                                   // address the station sent without one can be
                                   // routed back down the link it arrived on.
                                   SeccEndpoint.FromSdp(success.Response, Interface.Index)
                               );


                    case SDP_DiscoveryRejected rejected:

                        foreach (var (response, reason) in rejected.RejectedResponses)
                            Log.Warning($"SDP: the answer from {Describe(response)} was refused: {reason}.", "15118", "sdp");

                        Log.Notice(
                            $"SDP: {rejected.RejectedResponses.Count} station(s) answered and none of the answers was usable, " +
                            $"after {rejected.Attempts} attempt(s) in {rejected.Elapsed.TotalMilliseconds:F0} ms.",
                            "15118", "sdp"
                        );

                        return new DiscoveryOutcome(
                                   Answer(started, rejected.Attempts, rejected.Elapsed, "rejected",
                                       new JProperty("rejected", new JArray(
                                           rejected.RejectedResponses.Select(entry => {
                                               var json = SECCJSON(entry.Response, null);
                                               json["reason"] = entry.Reason;
                                               return json;
                                           })
                                       ))
                                   ),
                                   null
                               );


                    default:

                        Log.Notice(
                            $"SDP: nothing answered on '{Interface.Name}' after {result.Attempts} attempt(s) " +
                            $"in {result.Elapsed.TotalMilliseconds:F0} ms.",
                            "15118", "sdp"
                        );

                        return new DiscoveryOutcome(Answer(started, result.Attempts, result.Elapsed, "timeout"), null);

                }

            }
            catch (OperationCanceledException)
            {

                Log.Info($"SDP: the discovery on '{Interface.Name}' was cancelled.", "15118", "sdp");

                return new DiscoveryOutcome(Answer(started, sent, DateTimeOffset.UtcNow - started, "cancelled"), null);

            }
            catch (Exception e)
            {

                // A socket that cannot join the multicast group, an interface
                // that went away between being chosen and being used: this is
                // the vehicle's own failure and not an answer about the link,
                // so it is reported as one rather than as "nothing answered".
                Log.Error($"SDP: the discovery on '{Interface.Name}' failed: {e.Message}", "15118", "sdp");

                var failed = Answer(started, sent, DateTimeOffset.UtcNow - started, "failed");
                failed["error"] = e.Message;
                return new DiscoveryOutcome(failed, null);

            }

        }

        #endregion


        #region T1SMediumFor(Settings, V2GInterfaceName)

        /// <summary>
        /// What medium a session attaches to, from the session settings and
        /// the V2G interface.
        /// </summary>
        /// <remarks>
        /// The adapter is the V2G interface unless another is named, because
        /// on a real MCS the 10BASE-T1S link is the link the V2G traffic is
        /// on. The emulated medium joins its group wherever the operating
        /// system says unless told - a laptop's one cable is the right guess
        /// and its V2G interface, which may be a virtual one with no IPv4
        /// address at all, is not.
        /// </remarks>
        public static T1STransportOptions T1SMediumFor(SessionConfiguration  Settings,
                                                       String?               V2GInterfaceName)
        {

            var kind = Settings.T1STransportInEffect;

            return new T1STransportOptions(
                       Kind:           kind,
                       InterfaceName:  Settings.T1SInterface ??
                                           (kind is T1STransportKind.AfPacket or T1STransportKind.Auto
                                                ? V2GInterfaceName
                                                : null),
                       Group:          Settings.T1SBusEndpoint
                   );

        }

        #endregion

        #region AttachAsync(Medium, Weight, Log, Timeout = null, CancellationToken = default)

        /// <summary>
        /// The 10BASE-T1S attach stage of a Megawatt Charging System coupler:
        /// join the coupler's bus as the vehicle, and stay on it until told to
        /// leave.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What SLAC is to CCS, this is to MCS. There is no powerline under a
        /// megawatt coupler and nothing to sound; the link is IEEE 802.3cg
        /// 10BASE-T1S, a multidrop twisted pair the station coordinates with
        /// PLCA, and the vehicle is one node on it beside the temperature
        /// sensors in the pins. Attaching means waiting for the station's
        /// BEACON, asking for a node identifier in the discovery opportunity,
        /// and being given one - after which the station asks this vehicle
        /// every cycle whether it has anything to say, more often than it asks
        /// anybody else.
        /// </para>
        /// <para>
        /// Unlike a SLAC pairing this does not end when it succeeds: the
        /// vehicle is on the bus for as long as it is plugged in, and the
        /// coordinator gives it up for lost if it goes quiet. So what comes
        /// back is not only a report but a handle, and disposing of the handle
        /// is leaving the bus - which a session does when it ends.
        /// </para>
        /// <para>
        /// Over whichever medium the options name: the emulated one, which
        /// is UDP multicast, or a real adapter through AF_PACKET. Asking for
        /// the adapter where there is none is a failure; leaving the choice
        /// to Auto on a machine without one is not - the vehicle says so and
        /// carries on, which is what "auto" means.
        /// </para>
        /// </remarks>
        /// <param name="Medium">Which medium, and where.</param>
        /// <param name="Weight">How many transmit opportunities per cycle to ask for.</param>
        /// <param name="Log">Where the attaching is written while it happens.</param>
        /// <param name="Timeout">How long to wait for the station's BEACON and identifier.</param>
        /// <param name="CancellationToken">Abort the attaching.</param>
        public static async Task<T1SAttachment> AttachAsync(T1STransportOptions  Medium,
                                                            Byte                 Weight,
                                                            EventLog             Log,
                                                            TimeSpan?            Timeout             = null,
                                                            CancellationToken    CancellationToken   = default)
        {

            var started     = DateTimeOffset.UtcNow;
            var watch       = Stopwatch.StartNew();
            var timeout     = Timeout ?? TimeSpan.FromSeconds(10);
            var mediumText  = Medium.Kind.Write();
            var mediumKind  = Medium.Kind;

            Log.Notice($"T1S: joining the coupler's bus over {mediumText} as the vehicle.", "15118", "t1s");

            IT1STransport?  transport  = null;
            PlcaFollower?   follower   = null;

            try
            {

                var opened = T1STransports.Open(Medium);

                if (opened.IsFailed)
                {

                    Log.Error($"T1S: could not join the bus: {opened.Error}", "15118", "t1s");

                    return new T1SAttachment(
                               null,
                               null,
                               new JObject(
                                   new JProperty("outcome",     "noMedium"),
                                   new JProperty("startedAt",   started.ToString("o")),
                                   new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                                   new JProperty("transport",   opened.Kind.Write()),
                                   new JProperty("error",       opened.Error)
                               )
                           );

                }

                if (opened.Transport is null)
                {

                    Log.Notice($"T1S: {opened.Reason}", "15118", "t1s");

                    return new T1SAttachment(
                               null,
                               null,
                               new JObject(
                                   new JProperty("outcome",     "declined"),
                                   new JProperty("startedAt",   started.ToString("o")),
                                   new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                                   new JProperty("transport",   opened.Kind.Write()),
                                   new JProperty("reason",      opened.Reason)
                               )
                           );

                }

                transport   = opened.Transport;
                mediumText  = transport.Description;
                mediumKind  = opened.Kind;

                follower    = new PlcaFollower(
                                  transport,
                                  new PlcaFollowerOptions(
                                      Role:             T1SNodeRole.Vehicle,
                                      Name:             "EV",
                                      RequestedWeight:  Weight
                                  )
                              );

                follower.Log       += (_, line)   => Log.Debug($"T1S: {line}", "15118", "t1s");
                follower.Detached  += (_, reason) => Log.Warning($"T1S: off the bus - {reason}.", "15118", "t1s");

                await transport.StartAsync(CancellationToken);
                await follower. StartAsync(CancellationToken);

                if (!await follower.WaitUntilAttachedAsync(timeout, CancellationToken))
                {

                    var why = follower.Coordinator is null
                                  ? $"no BEACON was heard on {mediumText} in {timeout.TotalSeconds:F0} s - is a station coordinating that bus?"
                                  : $"the coordinator at {follower.Coordinator} heard this vehicle and did not answer in {timeout.TotalSeconds:F0} s.";

                    Log.Error($"T1S: could not join the bus: {why}", "15118", "t1s");

                    await follower. DisposeAsync();
                    await transport.DisposeAsync();

                    return new T1SAttachment(
                               null,
                               null,
                               new JObject(
                                   new JProperty("outcome",     "notAttached"),
                                   new JProperty("startedAt",   started.ToString("o")),
                                   new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                                   new JProperty("transport",   mediumKind.Write()),
                               new JProperty("medium",      mediumText),
                                   new JProperty("error",       why)
                               )
                           );

                }

                watch.Stop();

                Log.Notice($"T1S: on the bus as node {follower.NodeId} in {watch.Elapsed.TotalMilliseconds:F0} ms - " +
                           $"coordinator {follower.Coordinator}, {follower.Weight} opportunit{(follower.Weight == 1 ? "y" : "ies")} per cycle.",
                           "15118", "t1s");

                return new T1SAttachment(
                           transport,
                           follower,
                           new JObject(
                               new JProperty("outcome",       "attached"),
                               new JProperty("startedAt",     started.ToString("o")),
                               new JProperty("elapsed_ms",    Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                               new JProperty("transport",   mediumKind.Write()),
                               new JProperty("medium",      mediumText),
                               new JProperty("mac",           transport.LocalMac.ToString()),
                               new JProperty("coordinator",   follower.Coordinator?.ToString()),
                               new JProperty("nodeId",        follower.NodeId),
                               new JProperty("weight",        follower.Weight)
                           )
                       );

            }
            catch (OperationCanceledException)
            {

                Log.Info($"T1S: joining the bus over {mediumText} was cancelled.", "15118", "t1s");

                if (follower  is not null) await follower. DisposeAsync();
                if (transport is not null) await transport.DisposeAsync();

                return new T1SAttachment(
                           null,
                           null,
                           new JObject(
                               new JProperty("outcome",     "cancelled"),
                               new JProperty("startedAt",   started.ToString("o")),
                               new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                               new JProperty("transport",   mediumKind.Write()),
                               new JProperty("medium",      mediumText)
                           )
                       );

            }
            catch (Exception e)
            {

                Log.Error($"T1S: joining the bus over {mediumText} failed: {e.Message}", "15118", "t1s");

                if (follower  is not null) await follower. DisposeAsync();
                if (transport is not null) await transport.DisposeAsync();

                return new T1SAttachment(
                           null,
                           null,
                           new JObject(
                               new JProperty("outcome",     "failed"),
                               new JProperty("startedAt",   started.ToString("o")),
                               new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                               new JProperty("transport",   mediumKind.Write()),
                               new JProperty("medium",      mediumText),
                               new JProperty("error",       e.Message)
                           )
                       );

            }

        }

        #endregion

        #region PairAsync(Peer, Log, CancellationToken = default)

        /// <summary>
        /// The SLAC pairing stage: agree with a station on the network this session will run over, before
        /// there is an IP link to run it on.
        /// </summary>
        /// <remarks>
        /// SLAC comes before SDP in a real plug-in. A vehicle and a station share a powerline medium that
        /// every other vehicle and station on the same building's wiring also shares, and the first thing
        /// they have to establish is which of the several stations that can hear the vehicle is the one at
        /// the end of its cable. The answer is signal attenuation: the vehicle sounds, every station that
        /// hears it reports how loudly, and the quietest link is the cable that is plugged in.
        ///
        /// <b>Over a simulated medium only.</b> Real SLAC is EtherType 0x88E1 over AF_PACKET, which needs
        /// Linux and CAP_NET_RAW; this runs the same state machine over UDP against a station that agreed
        /// to do the same, which is what a bench without a powerline modem has. That is why there is no
        /// "pair over the real medium" here and why the peer is required rather than discovered: a
        /// simulated medium has no broadcast domain to find anybody on.
        ///
        /// The identification sent is seventeen zero bytes. A real vehicle sends something that identifies
        /// it, and nothing in the pairing depends on what it is - the matching is done on attenuation.
        /// </remarks>
        /// <param name="Peer">The station's SLAC endpoint on the simulated medium.</param>
        /// <param name="Log">Where the pairing is written while it happens.</param>
        /// <param name="CancellationToken">Abort the pairing.</param>
        public static async Task<JObject> PairAsync(IPEndPoint         Peer,
                                                    EventLog           Log,
                                                    CancellationToken  CancellationToken   = default)
        {

            var started = DateTimeOffset.UtcNow;
            var watch   = Stopwatch.StartNew();

            Log.Notice($"SLAC: pairing with the station at {Peer} over a simulated medium.", "15118", "slac");

            try
            {

                await using var transport = new UdpSlacTransport(
                                                V2GInterface.RandomMac(),
                                                new IPEndPoint(IPAddress.Any, 0),
                                                bootstrapPeers: [ Peer ]
                                            );

                var result = await new SlacEvStage(transport, new EvSlacOptions { PevId = new Byte[17] }).
                                       PairAsync(CancellationToken);

                watch.Stop();

                Log.Notice($"SLAC: paired in {watch.Elapsed.TotalMilliseconds:F0} ms - " +
                           $"network {Convert.ToHexString(result.Nid)}.",
                           "15118", "slac");

                return new JObject(
                           new JProperty("outcome",     "paired"),
                           new JProperty("startedAt",   started.ToString("o")),
                           new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                           new JProperty("peer",        Peer.ToString()),
                           // The network identifier, and deliberately not the
                           // network membership key beside it: the NID is what
                           // says which pairing this was, and the NMK is the
                           // secret that pairing agreed on.
                           new JProperty("nid",         Convert.ToHexString(result.Nid))
                       );

            }
            catch (OperationCanceledException)
            {

                Log.Info($"SLAC: the pairing with {Peer} was cancelled.", "15118", "slac");

                return new JObject(
                           new JProperty("outcome",     "cancelled"),
                           new JProperty("startedAt",   started.ToString("o")),
                           new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                           new JProperty("peer",        Peer.ToString())
                       );

            }
            catch (Exception e)
            {

                Log.Error($"SLAC: the pairing with {Peer} failed: {e.Message}", "15118", "slac");

                return new JObject(
                           new JProperty("outcome",     "failed"),
                           new JProperty("startedAt",   started.ToString("o")),
                           new JProperty("elapsed_ms",  Math.Round(watch.Elapsed.TotalMilliseconds, 1)),
                           new JProperty("peer",        Peer.ToString()),
                           new JProperty("error",       e.Message)
                       );

            }

        }

        #endregion


        #region Scoped(Interface)

        /// <summary>
        /// The address this vehicle sends from, with the scope id on it.
        /// </summary>
        /// <remarks>
        /// Through <see cref="V2GNetworkInterface.LinkLocalEndpoint"/> rather
        /// than by writing the index after the address: the address the
        /// interface reports may already carry a scope id, and it is the same
        /// index either way, so appending one produces <c>fe80::1%21%21</c>.
        /// Which is not an address, and is exactly what this used to print.
        /// </remarks>
        public static String Scoped(V2GNetworkInterface Interface)

            => Interface.LinkLocalEndpoint(0).Address.ToString();

        #endregion

        #region (private static) Answer(...) / SECCJSON(...) / Describe(Response)

        /// <summary>
        /// What every discovery says, whatever came of it.
        /// </summary>
        private static JObject Answer(DateTimeOffset    Started,
                                      Int32             Attempts,
                                      TimeSpan          Elapsed,
                                      String            Outcome,
                                      params JProperty[]  Rest)
        {

            var json = new JObject(
                           new JProperty("outcome",      Outcome),
                           new JProperty("startedAt",    Started.ToString("o")),
                           new JProperty("attempts",     Attempts),
                           new JProperty("elapsed_ms",   Math.Round(Elapsed.TotalMilliseconds, 1))
                       );

            foreach (var property in Rest)
                json.Add(property);

            return json;

        }


        /// <summary>
        /// One station, as the page reads it.
        /// </summary>
        /// <remarks>
        /// The endpoint the answer arrived from is carried beside the one the
        /// answer names, because they are not the same thing and the
        /// difference is worth seeing: the address in the payload is what the
        /// vehicle would connect to, the address on the packet is who actually
        /// sent it.
        /// </remarks>
        private static JObject SECCJSON(SDP_Response  Response,
                                        IPEndPoint?   From)

            => new (
                   new JProperty("address",    Response.SeccIPAddress.ToString()),
                   new JProperty("port",       Response.SeccPort),
                   new JProperty("security",   Response.Security          == SDP_Security.TLS ? "tls" : "noTls"),
                   new JProperty("transport",  Response.TransportProtocol == SDP_TransportProtocol.TCP ? "tcp" : Response.TransportProtocol.ToString()),
                   new JProperty("version",    Response.Version.ToString()),
                   new JProperty("from",       From?.ToString())
               );


        /// <summary>
        /// A station in one clause, for the log.
        /// </summary>
        private static String Describe(SDP_Response Response)

            => $"[{Response.SeccIPAddress}]:{Response.SeccPort} " +
               $"({(Response.Security == SDP_Security.TLS ? "TLS" : "no TLS")}, {Response.TransportProtocol})";

        #endregion

    }



    /// <summary>
    /// A vehicle's place on a coupler's 10BASE-T1S bus: how getting there went,
    /// and - while it is there - the node it is. Disposing of it is leaving.
    /// </summary>
    /// <remarks>
    /// A report and a handle in one, because the two cannot be separated: a
    /// vehicle that attached is on the bus until it leaves, and a caller that
    /// got only the report would have no way to leave. Where attaching failed
    /// the handle is empty and disposing of it does nothing.
    /// </remarks>
    public sealed class T1SAttachment : IAsyncDisposable
    {

        #region Properties

        /// <summary>The node this vehicle is on the bus, or null where it never got on.</summary>
        public PlcaFollower?  Node        { get; }

        /// <summary>How attaching went, as the web interface reads it.</summary>
        public JObject        JSON        { get; }

        /// <summary>Whether this vehicle is on the bus.</summary>
        public Boolean        IsAttached
            => Node is not null && Node.State == PlcaFollowerState.Attached;

        /// <summary>
        /// Whether there was no bus to join and nothing wrong with that: the
        /// transport was left to Auto on a machine without an adapter.
        /// </summary>
        public Boolean        IsDeclined
            => Node is null && JSON.Value<String>("outcome") == "declined";

        #endregion

        #region Data

        private readonly IT1STransport?  transport;

        #endregion

        #region Constructor(s)

        internal T1SAttachment(IT1STransport?  Transport,
                               PlcaFollower?   Node,
                               JObject         JSON)
        {
            this.transport  = Transport;
            this.Node       = Node;
            this.JSON       = JSON;
        }

        #endregion


        #region DisposeAsync()

        /// <summary>
        /// Leave the bus - with a LEAVE, so the coordinator need not wait to
        /// find out - and let go of the medium.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            if (Node is not null)
                await Node.DisposeAsync();

            if (transport is not null)
                await transport.DisposeAsync();

        }

        #endregion

    }

}
