import { api, type DiscoveryResult, type Link, type LinkMedium, type LinkRequest, type SECC,
         type SessionConfiguration, type SlacResult, type SlacStation, type T1SResult,
         type V2GConfiguration, type V2GUpdate } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { firstPageOfTheMenu } from '@node/start';
import { errorMessage, formatValue, numberField, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

import { sessionResult } from './sessionResult';

/**
 * How long this page waits on top of the deadline the vehicle was given.
 *
 * The vehicle stops asking at its own deadline and then still has to write the
 * answer back, so a page that waited exactly as long would give up on a
 * discovery that succeeded.
 */
const andABitMore = 5;

/** How often this page asks whether a session it started is over - as the Charging page does. */
const askAgainAfter = 2_000;

/** How a session at a station is secured, as the TLS choice beside it says it. */
type TLSChoice = 'none' | 'dotnet' | 'bc';


/**
 * Stations: plug in, look for a station, and charge at it.
 *
 * Plugging in is how the vehicle gets onto the link: straight onto it, after a
 * SLAC pairing that finds the station at the end of the cable, or onto the
 * 10BASE-T1S bus of an MCS coupler, which has exactly one station on it. The
 * vehicle stays plugged in until it is unplugged, so that the station found
 * over that link is the one it charges at - without pairing or joining the bus
 * a second time.
 *
 * "Look for a station" plugs in as chosen, where the vehicle is not plugged in
 * that way already, and then multicasts an SDP request onto the link. Every
 * station that answers usably is offered, each with how to secure a session
 * with it and a button that charges there; the choices are this session's
 * alone, and change no setting.
 *
 * What to ask for in a discovery is here as well, because it is a question of
 * this page: the settings saved are the ones every discovery uses.
 *
 * Drawn by view.ts: a draw changes only what differs, so that what is typed -
 * and its focus - outlives a discovery, a pairing or a running session being
 * drawn.
 */
export const stationsPage: Page = {

    title: 'Stations',

    render(context) {

        // "/" for everybody signed in, and not everybody may look at the link:
        // they get every node's "/" - the first page of the menu they may open.
        if (!auth.can('v2g', 'read'))
            return firstPageOfTheMenu.render(context);

        const { root } = context;

        const content = shell(root, {
            active:    '/',
            title:     'Stations',
            subtitle:  'Plug in, look for a station, and charge at it.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayRun     = auth.can('v2g', 'run');
        const mayChange  = auth.can('v2g', 'edit');
        const mayCharge  = auth.can('session', 'run');

        let cancelled  = false;
        let current: V2GConfiguration | null = null;
        let session: SessionConfiguration | null = null;
        let pending: ReturnType<typeof setTimeout> | null = null;

        /** What the page is doing on the link right now, as its button says it - or null. */
        let busy: string | null = null;

        /** The medium chosen above the button, kept across draws: a draw would set the radio back to the link held. */
        let via: LinkMedium | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const settings      = configuration.settings;
            const link          = configuration.link;
            const chosen        = via ?? link?.via ?? 'direct';
            const stage         = session?.settings;
            const running       = session?.running ?? false;
            // Nothing of the last discovery while the next one is running: left
            // standing under "Asking the link ...", it read as the new answer.
            const found         = busy !== null ? null : configuration.result ?? configuration.lastDiscovery;

            render(content, html`

                ${mayRun ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at this page', 'send anything on the link')}
                    </div>
                `}

                <div class="cards">

                    <section class="card wide" id="plug-card">

                        <h2><i class="fa-solid fa-plug"></i> Plug in, and look for a station</h2>

                        <form id="plug-form" class="form-stack" @submit=${lookForAStation}>

                            <div class="choices" role="radiogroup" aria-label="How to plug in">
                                ${medium('direct', 'directly',          'nothing before SDP - the station is reachable on the link as it is', chosen)}
                                ${medium('slac',   'over SLAC',         'a pairing first, which finds the station at the end of the cable',   chosen)}
                                ${medium('t1s',    'over 10BASE-T1S',   'the bus of an MCS coupler first, which has one station on it',      chosen)}
                            </div>

                            <div class="stage" ?hidden=${chosen !== 'slac'}>
                                <label>The station's SLAC endpoint
                                    <input type="text" name="slacPeer" value="${stage?.slacPeer ?? ''}"
                                           placeholder="[::1]:5000 - where the station listens on the simulated medium"
                                           ?disabled=${!mayRun} />
                                </label>
                            </div>

                            <div class="stage" ?hidden=${chosen !== 't1s'}>
                                <label>How to reach the bus
                                    <select name="t1sTransport" ?disabled=${!mayRun}>
                                        ${repeat([ [ 'auto', 'auto - a real adapter where there is one' ],
                                                   [ 'afpacket', 'afpacket - a real adapter, by name (Linux)' ],
                                                   [ 'udp', 'udp - the emulated medium, for a bench' ] ] as const,
                                                 ([ value ]) => value,
                                                 ([ value, label ]) => html`
                                            <option value="${value}" ?selected=${(stage?.t1sTransport === 'none' ? 'udp' : stage?.t1sTransport ?? 'udp') === value}>${label}</option>
                                        `)}
                                    </select>
                                </label>
                                <label>Bus group
                                    <input type="text" name="t1sBus" value="${stage?.t1sBus ?? ''}"
                                           placeholder="239.151.18.1:16118 - the emulated medium's group and port"
                                           ?disabled=${!mayRun} />
                                </label>
                                <label>Interface
                                    <input type="text" name="t1sInterface" value="${stage?.t1sInterface ?? ''}"
                                           placeholder="leave empty: the V2G interface for an adapter, the system's pick for udp"
                                           ?disabled=${!mayRun} />
                                </label>
                                <label>Weight - transmit opportunities per cycle, 1 to 8
                                    <input type="number" name="t1sWeight" min="1" max="8" step="1" value="${stage?.t1sWeight ?? 3}"
                                           ?disabled=${!mayRun} />
                                </label>
                            </div>

                            <div class="form-actions">
                                <button type="submit" id="look" class="btn primary" ?disabled=${!mayRun || busy !== null || running}>
                                    ${busy ?? 'Look for a station'}
                                </button>
                                ${link === null
                                      ? nothing
                                      : html`
                                          <button type="button" id="unplug" class="btn" ?disabled=${!mayRun || busy !== null || running}
                                                  @click=${() => void unplug()}>
                                              <i class="fa-solid fa-plug-circle-xmark"></i> Unplug
                                          </button>
                                        `}
                                <span id="plug-error" class="form-error" role="alert"></span>
                            </div>

                        </form>

                        <p class="hint">
                            ${chosen === 'slac'
                                  ? html`
                                      SLAC comes before SDP in a real plug-in: every station that can hear the vehicle
                                      reports how loudly, and the loudest - the lowest attenuation - is the one at the end
                                      of its cable. Here it runs over a simulated medium, against the endpoint above.
                                    `
                                  : chosen === 't1s'
                                      ? html`
                                          An MCS coupler's 10BASE-T1S bus has one station on it, which coordinates the bus;
                                          the vehicle joins it as a node and is asked every cycle for as long as it is
                                          plugged in.
                                        `
                                      : html`Nothing comes before SDP: the station is on the link as it is.`}
                            Plugged in, the vehicle stays plugged in until it is unplugged, and a session at a station
                            found here runs over the same link without pairing or joining a second time. Then one SDP
                            request is multicast to <code>ff02::1</code> port 15118 on
                            ${configuration.interface === null
                                  ? html`the first interface that could carry it`
                                  : html`<code>${configuration.interface}</code>`},
                            repeated up to ${settings.maxRetries} times until something answers or
                            ${settings.totalDeadlineSeconds} s have gone by; every request and every answer goes into the
                            <a href="${toURL('/logs')}">log</a> while it happens. What is entered here is for this
                            plugging in only - the settings are on the <a href="${toURL('/configuration/session')}">Charging</a> page.
                        </p>

                        ${link ? linkView(link) : nothing}

                        ${found ? discovery(found, running) : nothing}

                    </section>

                    ${session !== null && (running || session.lastSession !== null)
                          ? html`
                              <section class="card wide" id="charge-card">

                                  <h2><i class="fa-solid fa-bolt"></i> ${running ? 'Charging' : 'The last session'}</h2>

                                  ${running
                                        ? html`
                                            <div class="form-actions">
                                                <button type="button" id="stop" class="btn" ?disabled=${!mayCharge}
                                                        @click=${() => void stop()}>
                                                    <i class="fa-solid fa-stop"></i> Stop
                                                </button>
                                                <span id="charge-error" class="form-error" role="alert"></span>
                                            </div>
                                            <p class="hint">
                                                The session is running. Every message is on the <a href="${toURL('/logs')}">Logs</a>
                                                page while it happens; the sum appears here when it ends.
                                            </p>
                                          `
                                        : html`<span id="charge-error" class="form-error" role="alert"></span>`}

                                  ${!running && session.lastSession ? sessionResult(session.lastSession) : nothing}

                              </section>
                            `
                          : nothing}

                    <section class="card">

                        <h2><i class="fa-solid fa-sliders"></i> What to ask for</h2>

                        <form id="settings-form" class="form-stack" @submit=${saveSettings}>

                            <label>Security
                                <select name="requestedSecurity" ?disabled=${!mayChange}>
                                    <option value="tls"   ?selected=${settings.requestedSecurity === 'tls'}>TLS</option>
                                    <option value="noTls" ?selected=${settings.requestedSecurity === 'noTls'}>no TLS</option>
                                </select>
                            </label>

                            <label>Wait for an answer, in milliseconds
                                <input type="number" name="perAttemptTimeoutMs" min="10" max="60000" step="10"
                                       value="${Math.round(settings.perAttemptTimeoutSeconds * 1000)}"
                                       ?disabled=${!mayChange} />
                            </label>

                            <label>Ask at most this many times
                                <input type="number" name="maxRetries" min="1" max="1000" step="1"
                                       value="${settings.maxRetries}" ?disabled=${!mayChange} />
                            </label>

                            <label>Give up after, in seconds
                                <input type="number" name="totalDeadlineSeconds" min="0.1" max="600" step="0.1"
                                       value="${settings.totalDeadlineSeconds}" ?disabled=${!mayChange} />
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="rejectNoTLSResponses"
                                       ?checked=${settings.rejectNoTLSResponses}
                                       ?disabled=${!mayChange} />
                                <span>refuse a station that answers "no TLS" to a request for TLS</span>
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="requireLinkLocalSECCAddress"
                                       ?checked=${settings.requireLinkLocalSECCAddress}
                                       ?disabled=${!mayChange} />
                                <span>refuse an answer naming an address that is not link-local</span>
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="multicastLoopback"
                                       ?checked=${settings.multicastLoopback}
                                       ?disabled=${!mayChange} />
                                <span>hear this machine's own answers</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="settings-note"  class="form-notice" role="status"></span>
                                <span id="settings-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. [V2G2-159] asks a vehicle to repeat the
                                request every 250 ms for up to a minute; the defaults here are shorter,
                                because this one is a page somebody is watching. Switch the loopback on when
                                the station is a second process on this same machine - otherwise its answer
                                never reaches this one. Which interface is asked is on the
                                <a href="${toURL('/configuration/v2g')}">ISO 15118</a> page.
                            </span>

                        </form>

                    </section>

                </div>

            `);

            if (running)
                schedule();

        }


        /**
         * One way of plugging in, as a choice - checked by its attribute and
         * not its property: a form reset goes back to the attribute, and with
         * the property no way was checked after one, and the next search
         * plugged in directly and let go of the pairing held.
         */
        function medium(value: LinkMedium, label: string, hint: string, chosen: LinkMedium): TemplateResult {

            return html`
                <label class="switch">
                    <input type="radio" name="via" value="${value}" ?checked=${chosen === value} ?disabled=${!mayRun}
                           @change=${() => { via = value; draw(); }} />
                    <span>${label} <span class="muted">- ${hint}</span></span>
                </label>
            `;

        }


        /** The link held, and what plugging in said of it. */
        function linkView(link: Link): TemplateResult {

            return html`
                <div class="query-result ok" id="link">
                    <div class="kv-list">
                        <div class="kv">
                            <span class="k">Plugged in</span>
                            <span class="v">
                                ${link.via === 'slac' ? 'over SLAC' : link.via === 't1s' ? 'over 10BASE-T1S' : 'directly'}
                                <span class="muted">since ${formatValue(link.since)}</span>
                            </span>
                        </div>
                    </div>
                    ${link.slac ? pairing(link.slac) : nothing}
                    ${link.t1s  ? bus(link.t1s)      : nothing}
                </div>
            `;

        }

        /** Whom a SLAC pairing found, and on which network. */
        function pairing(slac: SlacResult): TemplateResult {

            const others = (slac.candidates ?? []).filter(one => one.mac !== slac.station?.mac);

            return html`
                <div class="kv-list">
                    ${slac.station
                          ? html`<div class="kv"><span class="k">The station at the cable</span><span class="v">${slacStation(slac.station)}</span></div>`
                          : nothing}
                    ${slac.nid  ? html`<div class="kv"><span class="k">Network</span><span class="v"><code>${slac.nid}</code></span></div>` : nothing}
                    ${slac.peer ? html`<div class="kv"><span class="k">Paired over</span><span class="v"><code>${slac.peer}</code></span></div>` : nothing}
                    ${others.length > 0
                          ? html`<div class="kv"><span class="k">Heard more quietly</span><span class="v">${others.map(one => html`${slacStation(one)}<br />`)}</span></div>`
                          : nothing}
                </div>
            `;

        }

        function slacStation(one: SlacStation): TemplateResult {
            return html`<code>${one.mac}</code>${one.attenuation_dB !== undefined ? html` <span class="muted">${one.attenuation_dB} dB</span>` : nothing}`;
        }

        /** Which bus, and as which node. */
        function bus(t1s: T1SResult): TemplateResult {

            return html`
                <div class="kv-list">
                    ${t1s.medium      ? html`<div class="kv"><span class="k">Bus</span><span class="v"><code>${t1s.medium}</code></span></div>` : nothing}
                    ${t1s.coordinator ? html`<div class="kv"><span class="k">The station coordinating it</span><span class="v"><code>${t1s.coordinator}</code></span></div>` : nothing}
                    ${t1s.nodeId !== undefined
                          ? html`<div class="kv"><span class="k">This vehicle</span><span class="v">node ${t1s.nodeId}, ${t1s.weight} opportunit${t1s.weight === 1 ? 'y' : 'ies'} per cycle</span></div>`
                          : nothing}
                </div>
            `;

        }


        /** What a discovery found: every usable station, each to charge at. */
        function discovery(result: DiscoveryResult, running: boolean): TemplateResult {

            const stations = result.outcome === 'found' && result.secc ? [ result.secc, ...(result.others ?? []) ] : [];

            return html`
                <div class="query-result ${stations.length > 0 ? 'ok' : 'bad'}" id="discovery">

                    <div class="kv-list">
                        <div class="kv"><span class="k">Result</span><span class="v">${outcome(result)}</span></div>
                        ${result.interface  ? html`<div class="kv"><span class="k">Interface</span><span class="v"><code>${result.interface}</code></span></div>` : nothing}
                        ${result.startedAt  ? html`<div class="kv"><span class="k">At</span><span class="v">${formatValue(result.startedAt)}</span></div>` : nothing}
                        ${result.attempts !== undefined   ? html`<div class="kv"><span class="k">Requests sent</span><span class="v">${result.attempts}</span></div>` : nothing}
                        ${result.elapsed_ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${result.elapsed_ms} ms</span></div>` : nothing}
                        ${result.error      ? html`<div class="kv"><span class="k">Error</span><span class="v">${result.error}</span></div>` : nothing}
                    </div>

                    ${stations.length > 0
                          ? html`
                              <h3>${stations.length === 1 ? 'The station' : `${stations.length} stations`}</h3>
                              <div class="stations">
                                  ${repeat(stations, secc => `${secc.address}:${secc.port}`, (secc, place) => station(secc, place, running))}
                              </div>
                              ${stations.length > 1
                                    ? html`<p class="hint">More than one station answered - a link with two stations on it is usually a surprise worth knowing about.</p>`
                                    : nothing}
                            `
                          : nothing}

                    ${result.rejected && result.rejected.length > 0
                          ? html`
                              <h3>Answers that were refused</h3>
                              ${result.rejected.map(refused => html`
                                  <div class="kv-list">
                                      <div class="kv"><span class="k">V2G endpoint</span><span class="v"><code>[${refused.address}]:${refused.port}</code></span></div>
                                      ${refused.reason ? html`<div class="kv"><span class="k">Refused because</span><span class="v">${refused.reason}</span></div>` : nothing}
                                  </div>
                              `)}
                            `
                          : nothing}

                </div>
            `;

        }

        /** One station found, with how to secure a session with it and the button that charges there. */
        function station(secc: SECC, place: number, running: boolean): TemplateResult {

            const offers  = secc.security === 'tls';
            const tls     = defaultTLS(offers);

            return html`
                <form class="station form-stack" data-station="${place}" @submit=${(event: SubmitEvent) => chargeAt(event, place)}>
                    <div class="kv-list">
                        <div class="kv">
                            <span class="k">V2G endpoint</span>
                            <span class="v"><code>[${secc.address}]:${secc.port}</code></span>
                        </div>
                        <div class="kv">
                            <span class="k">Offers</span>
                            <span class="v">${offers ? 'TLS' : 'no TLS'} over ${secc.transport}</span>
                        </div>
                        ${secc.from ? html`<div class="kv"><span class="k">Answered from</span><span class="v"><code>${secc.from}</code></span></div>` : nothing}
                    </div>
                    <div class="form-actions">
                        <label>Secure it with
                            <select name="tls" ?disabled=${!mayCharge || running}>
                                <option value="none"   ?selected=${tls === 'none'}>nothing - plain TCP</option>
                                <option value="dotnet" ?selected=${tls === 'dotnet'}>TLS - .NET SslStream</option>
                                <option value="bc"     ?selected=${tls === 'bc'}>TLS - BouncyCastle, the -20 profile</option>
                            </select>
                        </label>
                        <button type="submit" class="btn primary" ?disabled=${!mayCharge || running || busy !== null}>
                            <i class="fa-solid fa-bolt"></i> Charge here
                        </button>
                    </div>
                </form>
            `;

        }

        /**
         * How a session with a station is secured unless somebody chooses
         * otherwise: as the station offers, with the stack the settings name -
         * and, where they name none, .NET's.
         */
        function defaultTLS(offers: boolean): TLSChoice {

            if (!offers)
                return 'none';

            const configured = session?.settings.tls ?? 'none';

            return configured === 'none' ? 'dotnet' : configured;

        }

        /** The outcome in the words somebody would use. */
        function outcome(result: DiscoveryResult): string {

            switch (result.outcome)
            {
                case 'found':        return 'a station answered';
                case 'rejected':     return 'something answered, and none of the answers was usable';
                case 'timeout':      return 'nothing answered';
                case 'cancelled':    return 'the discovery was cancelled';
                case 'busy':         return 'a discovery is already running';
                case 'noInterface':  return 'there was nothing to broadcast on';
                default:             return 'the discovery failed';
            }

        }


        /** What the plug form asks for: how, and this plugging in's peer or bus. */
        function linkRequest(form: HTMLFormElement): LinkRequest {

            const data   = new FormData(form);
            const chosen = String(data.get('via') ?? 'direct') as LinkMedium;
            const text   = (name: string) => String(data.get(name) ?? '').trim() || undefined;

            if (chosen === 'slac')
                return { via: chosen, slacPeer: text('slacPeer') };

            if (chosen === 't1s')
                return {
                    via:           chosen,
                    t1sTransport:  text('t1sTransport') as LinkRequest['t1sTransport'],
                    t1sBus:        text('t1sBus'),
                    t1sInterface:  text('t1sInterface'),
                    t1sWeight:     Number.isNaN(numberField(form, 't1sWeight')) ? undefined : numberField(form, 't1sWeight')
                };

            return { via: chosen };

        }

        /** Whether the link held is the one asked for, so that plugging in again would change nothing. */
        function holds(link: Link | null, asked: LinkRequest): boolean {

            if (link === null || link.via !== asked.via)
                return false;

            // A peer or a bus other than the one held is plugging in elsewhere.
            if (asked.via === 'slac')
                return asked.slacPeer === undefined || asked.slacPeer === link.slac?.peer;

            if (asked.via === 't1s')
                return asked.t1sBus === undefined || (link.t1s?.medium ?? '').includes(asked.t1sBus);

            return true;

        }


        async function lookForAStation(event: SubmitEvent): Promise<void> {

            event.preventDefault();

            const form   = event.currentTarget as HTMLFormElement;
            const asked  = linkRequest(form);
            const error  = must<HTMLElement>(content, '#plug-error');

            error.textContent = '';

            try
            {

                if (!holds(current?.link ?? null, asked))
                {

                    busy = asked.via === 'slac' ? 'Pairing ...' : asked.via === 't1s' ? 'Joining the bus ...' : 'Plugging in ...';
                    draw();

                    const plugged = await api.link.plugIn(asked);

                    if (cancelled)
                        return;

                    const { result, ...plugs } = plugged;

                    current = { ...plugs, result: current?.result };

                    if (result.outcome !== 'pluggedIn')
                    {
                        busy = null;
                        draw();
                        must<HTMLElement>(content, '#plug-error').textContent = 'error' in result ? result.error : 'Not plugged in.';
                        return;
                    }

                }

                busy = 'Asking the link ...';
                draw();

                // The answer carries the whole configuration as well as the
                // result, because a discovery moves the record of the last one
                // that this page is showing.
                const asking = await api.v2g.discover((current?.settings.totalDeadlineSeconds ?? 10) + andABitMore);

                if (cancelled)
                    return;

                current = asking;
                busy    = null;
                draw();

                // A draw leaves a form as it is typed into; this one was used,
                // and what it says now is the link it plugged in.
                form.reset();
                via = null;
                draw();

            }
            catch (problem)
            {
                busy = null;
                draw();
                must<HTMLElement>(content, '#plug-error').textContent = errorMessage(problem);
            }

        }


        async function unplug(): Promise<void> {

            const error = must<HTMLElement>(content, '#plug-error');

            error.textContent = '';

            try
            {
                current = { ...await api.link.unplug(), result: current?.result };
                draw();
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#plug-error').textContent = errorMessage(problem);
            }

        }


        async function chargeAt(event: SubmitEvent, place: number): Promise<void> {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const tls   = String(new FormData(form).get('tls') ?? 'none') as TLSChoice;
            const error = must<HTMLElement>(content, '#plug-error');

            error.textContent = '';

            try
            {

                await api.session.start({ station: place, tls });

                // The vehicle answered "started", not "finished". Ask it what
                // is happening, which sets the polling going.
                await loadSession();

            }
            catch (problem)
            {
                must<HTMLElement>(content, '#plug-error').textContent = errorMessage(problem);
            }

        }


        async function stop(): Promise<void> {

            try
            {
                await api.session.stop();
                await loadSession();
            }
            catch (problem)
            {
                const error = content.querySelector<HTMLElement>('#charge-error');
                if (error !== null)
                    error.textContent = errorMessage(problem);
            }

        }


        function saveSettings(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const data = new FormData(form);

            // An emptied number is NaN here and null on the wire, which the
            // vehicle reads as "not given": what it had stays.
            void save(form, {
                requestedSecurity:            String(data.get('requestedSecurity') ?? 'tls') === 'noTls' ? 'noTls' : 'tls',
                perAttemptTimeoutSeconds:     numberField(form, 'perAttemptTimeoutMs') / 1000,
                maxRetries:                   numberField(form, 'maxRetries'),
                totalDeadlineSeconds:         numberField(form, 'totalDeadlineSeconds'),
                rejectNoTLSResponses:         data.get('rejectNoTLSResponses')        !== null,
                requireLinkLocalSECCAddress:  data.get('requireLinkLocalSECCAddress') !== null,
                multicastLoopback:            data.get('multicastLoopback')           !== null
            });

        }


        async function save(form: HTMLFormElement, update: V2GUpdate): Promise<void> {

            const note = must<HTMLElement>(content, '#settings-note');

            note.textContent = '';

            must<HTMLElement>(content, '#settings-error').textContent = '';

            try
            {
                const saved = await whileSaving(content, note, () => api.v2g.save(update));

                if (cancelled)
                    return;

                // What the page shows of the last discovery stays: a save is
                // answered without one.
                current = { ...saved, result: current?.result };

                draw();

                // A draw leaves a form as it is typed into; this one was
                // saved, so it goes back to what it says now - the answer.
                form.reset();

                note.textContent = 'Saved, and in effect for the next discovery.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#settings-error').textContent = errorMessage(problem);
            }

        }


        /** Ask again in a while whether the session is over. */
        function schedule(): void {
            if (pending === null && !cancelled)
                pending = setTimeout(() => { pending = null; void loadSession(true); }, askAgainAfter);
        }

        function clearPending(): void {
            if (pending !== null) {
                clearTimeout(pending);
                pending = null;
            }
        }

        /**
         * @param quietly  while polling: a failure to reach the vehicle mid-session
         *                 must not say anything, because the next attempt two
         *                 seconds later usually succeeds.
         */
        async function loadSession(quietly = false): Promise<void> {

            try
            {
                const loaded = await api.session.get();

                if (!cancelled) {
                    session = loaded;
                    draw();
                }
            }
            catch (problem)
            {
                if (cancelled)
                    return;

                if (quietly) {
                    schedule();
                    return;
                }

                const error = content.querySelector<HTMLElement>('#plug-error');
                if (error !== null)
                    error.textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {

                // The session is not this page's to need: somebody who may look
                // at the link and not at charging still sees the stations.
                const [ loaded, charging ] = await Promise.all([
                    api.v2g.get(),
                    auth.can('session', 'read') ? api.session.get().catch(() => null) : Promise.resolve(null)
                ]);

                if (!cancelled) {
                    current = loaded;
                    session = charging;
                    draw();
                }

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The stations could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        /**
         * Loaded anew - Reload - is what the vehicle has, the forms too, which
         * a draw on its own would leave as typed.
         */
        async function reload(): Promise<void> {

            await load();

            if (!cancelled) {
                via = null;
                content.querySelectorAll('form').forEach(form => form.reset());
                draw();
            }

        }

        // Every form: the plug form, what to ask for, and the TLS chosen
        // beside each station found.
        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; clearPending(); release(); };

    }

};
