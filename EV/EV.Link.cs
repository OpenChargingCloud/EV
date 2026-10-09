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

using cloud.charging.open.protocols.ISO15118.T1S.Transport;
using cloud.charging.open.protocols.ISO15118.Transport;

using cloud.charging.open.EV.Configuration;
using cloud.charging.open.EV.ISO15118;

#endregion

namespace cloud.charging.open.EV
{

    /// <summary>
    /// How this vehicle is plugged in: straight onto the link, after a SLAC
    /// pairing, or onto the 10BASE-T1S bus of an MCS coupler.
    /// </summary>
    public enum LinkMedium
    {

        /// <summary>Nothing before SDP: the station is reachable on the link as it is.</summary>
        Direct,

        /// <summary>A SLAC pairing first, which finds the station at the end of the cable.</summary>
        SLAC,

        /// <summary>The coupler's 10BASE-T1S bus first, which has exactly one station on it.</summary>
        T1S

    }


    public partial class EV
    {

        #region (private) PluggedIn

        /// <summary>
        /// A link this vehicle holds: how it got onto it, what that said, and -
        /// on a bus - the attachment that keeps it there.
        /// </summary>
        /// <param name="Medium">How it is plugged in.</param>
        /// <param name="JSON">What plugging in said, as the web interface reads it.</param>
        /// <param name="SLAC">The pairing, where it is plugged in over SLAC.</param>
        /// <param name="Bus">The attachment, where it is on a bus; disposing of it leaves the bus.</param>
        private sealed record PluggedIn(LinkMedium      Medium,
                                        JObject         JSON,
                                        JObject?        SLAC,
                                        T1SAttachment?  Bus);

        #endregion

        #region Data

        /// <summary>
        /// One plugging in or out at a time: a second one while the first is
        /// still pairing or joining the bus would hold two links.
        /// </summary>
        private readonly  SemaphoreSlim  linkLock  = new (1, 1);

        /// <summary>
        /// The link this vehicle holds, or null where it is not plugged in.
        /// </summary>
        private           PluggedIn?     pluggedIn;

        #endregion

        #region Properties

        /// <summary>
        /// How this vehicle is plugged in, as the web interface reads it, or
        /// null where it is not.
        /// </summary>
        public JObject? LinkJSON
            => pluggedIn?.JSON.DeepClone() as JObject;

        #endregion


        #region (static) TryParseMedium(Text, out Medium)

        /// <summary>
        /// "direct", "slac" or "t1s", as a medium.
        /// </summary>
        public static Boolean TryParseMedium(String? Text, out LinkMedium Medium)
        {

            switch (Text?.Trim().ToLowerInvariant())
            {
                case "direct":  Medium = LinkMedium.Direct;  return true;
                case "slac":    Medium = LinkMedium.SLAC;    return true;
                case "t1s":     Medium = LinkMedium.T1S;     return true;
                default:        Medium = default;            return false;
            }

        }

        /// <summary>
        /// A medium as it is written: "direct", "slac" or "t1s".
        /// </summary>
        public static String Write(LinkMedium Medium)

            => Medium switch {
                   LinkMedium.SLAC  => "slac",
                   LinkMedium.T1S   => "t1s",
                   _                => "direct"
               };

        #endregion

        #region PlugInAsync(Medium, Stage = null, CancellationToken = default)

        /// <summary>
        /// Plug this vehicle in: straight onto the link, after a SLAC pairing,
        /// or onto a coupler's bus - and stay plugged in until it is unplugged,
        /// so that the station found over it is the one a session then charges
        /// at, without pairing or joining a second time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A link held before is let go of first: a vehicle is plugged in at one
        /// station at a time. Where plugging in fails, it is not plugged in at
        /// all, and the answer says why.
        /// </para>
        /// <para>
        /// The peer and the bus are those of <paramref name="Stage"/> where it
        /// names them, and the session settings' otherwise - for this plugging
        /// in only; the settings stay what they are. Refused while a session is
        /// running, which is using the link it has.
        /// </para>
        /// </remarks>
        /// <param name="Medium">How to plug in.</param>
        /// <param name="Stage">The SLAC peer and the T1S bus for this plugging in, where they differ from the session settings.</param>
        /// <param name="CancellationToken">Abort plugging in.</param>
        public async Task<JObject> PlugInAsync(LinkMedium             Medium,
                                               SessionConfiguration?  Stage              = null,
                                               CancellationToken      CancellationToken  = default)
        {

            if (!await linkLock.WaitAsync(0, CancellationToken))
                return Refused("busy", SessionRunning
                                           ? "A session is running on this vehicle, over the link it has. Stop it first."
                                           : "This vehicle is already being plugged in or out.");

            try
            {

                await LetGoOfLink("plugged in again");

                var settings  = Stage is null
                                    ? SessionSettings
                                    : SessionSettings with {
                                          SLACPeer      = Stage.SLACPeer     ?? SessionSettings.SLACPeer,
                                          T1STransport  = Stage.T1STransport ?? SessionSettings.T1STransport,
                                          T1SBus        = Stage.T1SBus       ?? SessionSettings.T1SBus,
                                          T1SInterface  = Stage.T1SInterface ?? SessionSettings.T1SInterface,
                                          T1SWeight     = Stage.T1SWeight    ?? SessionSettings.T1SWeight
                                      };

                var since     = TimeProvider.GetUtcNow().ToString("o");

                switch (Medium)
                {

                    case LinkMedium.SLAC:
                    {

                        if (settings.SLACPeer is not String peer)
                            return Refused("notConfigured",
                                           "There is no SLAC peer to pair with. Name the station's SLAC endpoint, " +
                                           "or plug in directly.");

                        var endpoint  = V2GEndpoint.Parse(peer, "the SLAC peer");

                        var slac      = await V2GLink.PairAsync(
                                                endpoint.IPEndPoint ?? new IPEndPoint(IPAddress.Parse(endpoint.ConnectHost), endpoint.Port),
                                                Log,
                                                CancellationToken
                                            );

                        if (slac.Value<String>("outcome") != "paired")
                            return Refused(slac.Value<String>("outcome") ?? "failed",
                                           slac.Value<String>("error") ?? "The SLAC pairing did not complete.",
                                           new JProperty("slac", slac));

                        return Hold(new PluggedIn(Medium, Plugged(Medium, since, new JProperty("slac", slac)), slac, null));

                    }

                    case LinkMedium.T1S:
                    {

                        if (settings.T1STransportInEffect == T1STransportKind.None)
                            return Refused("notConfigured",
                                           "There is no 10BASE-T1S bus to join. Choose how to reach it - an adapter, " +
                                           "or the emulated medium - or plug in directly.");

                        var bus = await V2GLink.AttachAsync(
                                            V2GLink.T1SMediumFor(settings, V2GSettings.InterfaceName),
                                            settings.T1SWeightInEffect,
                                            Log,
                                            CancellationToken:  CancellationToken
                                        );

                        if (!bus.IsAttached)
                        {

                            await bus.DisposeAsync();

                            return Refused(bus.JSON.Value<String>("outcome") ?? "failed",
                                           bus.JSON.Value<String>("error")
                                               ?? bus.JSON.Value<String>("reason")
                                               ?? "The vehicle could not join the coupler's bus.",
                                           new JProperty("t1s", bus.JSON));

                        }

                        return Hold(new PluggedIn(Medium, Plugged(Medium, since, new JProperty("t1s", bus.JSON)), null, bus));

                    }

                    default:

                        Log.Notice("Link: plugged in directly - nothing before SDP.", "15118", "link");

                        return Hold(new PluggedIn(Medium, Plugged(Medium, since), null, null));

                }

            }
            finally
            {
                linkLock.Release();
            }

        }

        #endregion

        #region UnplugAsync()

        /// <summary>
        /// Unplug this vehicle: leave the bus, forget the pairing. True where it
        /// was plugged in.
        /// </summary>
        /// <remarks>
        /// Refused while a session is running or the vehicle is being plugged
        /// in - by throwing, because the caller asked for something that cannot
        /// be: the session holds the link for as long as it runs.
        /// </remarks>
        public async Task<Boolean> UnplugAsync()
        {

            if (!await linkLock.WaitAsync(0))
                throw new InvalidOperationException(SessionRunning
                                                        ? "A session is running on this vehicle, over the link it has. Stop it first."
                                                        : "This vehicle is already being plugged in or out.");

            try
            {
                return await LetGoOfLink("unplugged");
            }
            finally
            {
                linkLock.Release();
            }

        }

        #endregion


        #region (protected override) OnStopping()

        /// <summary>
        /// A vehicle that stops is unplugged: it leaves the bus, so that the
        /// coupler is not left asking a node that is gone.
        /// </summary>
        protected override async Task OnStopping()
        {

            await LetGoOfLink("stopping");

            await base.OnStopping();

        }

        #endregion


        #region (private) Hold(Link)

        private JObject Hold(PluggedIn Link)
        {
            pluggedIn = Link;
            return Link.JSON.DeepClone() as JObject ?? Link.JSON;
        }

        #endregion

        #region (private) Plugged(Medium, Since, params Stage)

        /// <summary>
        /// What a vehicle plugged in says of its link.
        /// </summary>
        private static JObject Plugged(LinkMedium          Medium,
                                       String              Since,
                                       params JProperty[]  Stage)

            => new (
                   [
                       new JProperty("outcome",  "pluggedIn"),
                       new JProperty("via",      Write(Medium)),
                       new JProperty("since",    Since),
                       .. Stage
                   ]
               );

        #endregion

        #region (private) Refused(Outcome, Error, params More)

        private static JObject Refused(String              Outcome,
                                       String              Error,
                                       params JProperty[]  More)

            => new (
                   [
                       new JProperty("outcome",  Outcome),
                       new JProperty("error",    Error),
                       .. More
                   ]
               );

        #endregion

        #region (private) LetGoOfLink(Why)

        /// <summary>
        /// Leave the bus and forget the pairing, where there are either. True
        /// where there was a link to let go of.
        /// </summary>
        private async Task<Boolean> LetGoOfLink(String Why)
        {

            var held  = pluggedIn;

            pluggedIn = null;

            if (held is null)
                return false;

            if (held.Bus is not null)
                await held.Bus.DisposeAsync();

            Log.Notice($"Link: {Why} - the {Write(held.Medium)} link is let go of.", "15118", "link");

            return true;

        }

        #endregion

    }

}
