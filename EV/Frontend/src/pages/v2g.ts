import { api, type DiscoveryResult, type SECC, type SlacResult, type V2GConfiguration, type V2GUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * How long this page waits on top of the deadline the vehicle was given.
 *
 * The vehicle stops asking at its own deadline and then still has to write the
 * answer back, so a page that waited exactly as long would give up on a
 * discovery that succeeded.
 */
const andABitMore = 5;


/**
 * The wire below the charging cable, from this vehicle's side.
 *
 * "Look for a station" multicasts an SDP request to ff02::1 on the chosen
 * interface and waits for a station to answer with the address and port of its
 * V2G endpoint. Every request that goes out and every answer that comes in is
 * written to the log as it happens, so the Logs page - or anything else
 * reading the event stream - shows the exchange rather than only its outcome.
 * A station answering on the third attempt and one answering on the fifteenth
 * are the same result here and a very different link.
 *
 * Nothing on this page starts a charging session. Discovery asks a question;
 * it connects to nothing and draws nothing.
 */
export const v2gPage: Page = {

    title: 'ISO 15118',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/v2g',
            title:     'ISO 15118',
            subtitle:  'The wire below the charging cable: which interface this vehicle speaks V2G on, and who is on it.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeNetworkSettings');
        const mayLook   = auth.can('runDiagnostics');

        let cancelled  = false;
        let current: V2GConfiguration | null = null;
        let searching  = false;
        let pairing    = false;
        let paired: SlacResult | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const settings      = configuration.settings;
            const found         = configuration.result ?? configuration.lastDiscovery;
            const candidates    = configuration.interfaces;

            render(content, html`

                ${mayLook ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at this page
                        but not send anything on the link. That needs the driver, the service or the system
                        administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card wide">

                        <h2><i class="fa-solid fa-tower-broadcast"></i> Look for a station</h2>

                        <div class="form-actions">
                            <button type="button" id="discover" class="btn primary" ${mayLook && !searching ? '' : html`disabled`}>
                                ${searching ? 'Asking the link ...' : 'Look for a station'}
                            </button>
                            <span id="discover-error" class="form-error" role="alert"></span>
                        </div>

                        <p class="hint">
                            One SDP request, multicast to <code>ff02::1</code> port 15118 on
                            ${configuration.interface === null
                                  ? html`the first interface that could carry it`
                                  : html`<code>${configuration.interface}</code>`},
                            repeated up to ${settings.maxRetries} times at
                            ${Math.round(settings.perAttemptTimeoutSeconds * 1000)} ms until something answers
                            or ${settings.totalDeadlineSeconds} s have gone by. Every request and every answer
                            goes into the log while it happens, so the Logs page shows the exchange itself.
                            Nothing is connected to and no session is started.
                        </p>

                        ${found ? discovery(found) : ''}

                    </section>

                    <section class="card wide">

                        <h2><i class="fa-solid fa-plug-circle-bolt"></i> Pair over SLAC</h2>

                        <div class="form-actions">
                            <button type="button" id="pair" class="btn" ${mayLook && !pairing ? '' : html`disabled`}>
                                ${pairing ? 'Sounding ...' : 'Pair over SLAC'}
                            </button>
                            <span id="pair-error" class="form-error" role="alert"></span>
                        </div>

                        <p class="hint">
                            SLAC comes before SDP in a real plug-in: a vehicle and a station share a
                            powerline medium that every other vehicle and station on the same building's
                            wiring also shares, and the first thing they settle is which of the stations
                            that can hear the vehicle is the one at the end of its cable. The answer is
                            signal attenuation - the vehicle sounds, every station reports how loudly it
                            heard, and the quietest link is the cable that is plugged in.
                            Real SLAC is EtherType 0x88E1 over AF_PACKET and needs Linux and CAP_NET_RAW;
                            this runs the same state machine over a simulated medium, against the peer set
                            on the <a href="/configuration/session">Charging</a> page. A session pairs by
                            itself where one is configured - this button is here because SLAC agreeing and
                            SDP finding nothing is a very different link from SLAC never agreeing at all.
                        </p>

                        ${paired ? pairingResult(paired) : ''}

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-ethernet"></i> Interface</h2>

                        <form id="interface-form" class="form-stack">

                            <label>The interface the station is on
                                <select name="interface" ${mayChange ? '' : html`disabled`}>
                                    <option value="" ${configuration.interface === null ? html`selected` : ''}>
                                        the first one that could carry it
                                    </option>
                                    ${candidates.map(candidate => html`
                                        <option value="${candidate.name}" ${configuration.interface === candidate.name ? html`selected` : ''}>
                                            ${candidate.name}
                                        </option>
                                    `)}
                                </select>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="interface-note"  class="form-notice" role="status"></span>
                                <span id="interface-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                On a real vehicle this is the powerline modem. On a bench it is whichever
                                interface the station is reachable over. An interface only appears here when
                                it is up, has a MAC address and has an IPv6 link-local address - SDP needs
                                all three.
                            </span>

                        </form>

                        ${candidates.length === 0
                              ? html`
                                  <div class="error-box">
                                      No interface of this machine could carry V2G traffic. A discovery would
                                      have nothing to broadcast on.
                                  </div>
                                `
                              : html`
                                  <div class="kv-list">
                                      ${candidates.map(candidate => html`
                                          <div class="kv">
                                              <span class="k">${candidate.name}</span>
                                              <span class="v">
                                                  <code>${candidate.linkLocal}</code>
                                                  <span class="muted small">${candidate.mac}</span>
                                              </span>
                                          </div>
                                      `)}
                                  </div>
                                `}

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-sliders"></i> What to ask for</h2>

                        <form id="settings-form" class="form-stack">

                            <label>Security
                                <select name="requestedSecurity" ${mayChange ? '' : html`disabled`}>
                                    <option value="tls"   ${settings.requestedSecurity === 'tls'   ? html`selected` : ''}>TLS</option>
                                    <option value="noTls" ${settings.requestedSecurity === 'noTls' ? html`selected` : ''}>no TLS</option>
                                </select>
                            </label>

                            <label>Wait for an answer, in milliseconds
                                <input type="number" name="perAttemptTimeoutMs" min="10" max="60000" step="10"
                                       value="${Math.round(settings.perAttemptTimeoutSeconds * 1000)}"
                                       ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Ask at most this many times
                                <input type="number" name="maxRetries" min="1" max="1000" step="1"
                                       value="${settings.maxRetries}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Give up after, in seconds
                                <input type="number" name="totalDeadlineSeconds" min="0.1" max="600" step="0.1"
                                       value="${settings.totalDeadlineSeconds}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="rejectNoTLSResponses"
                                       ${settings.rejectNoTLSResponses ? html`checked` : ''}
                                       ${mayChange ? '' : html`disabled`} />
                                <span>refuse a station that answers "no TLS" to a request for TLS</span>
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="requireLinkLocalSECCAddress"
                                       ${settings.requireLinkLocalSECCAddress ? html`checked` : ''}
                                       ${mayChange ? '' : html`disabled`} />
                                <span>refuse an answer naming an address that is not link-local</span>
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="multicastLoopback"
                                       ${settings.multicastLoopback ? html`checked` : ''}
                                       ${mayChange ? '' : html`disabled`} />
                                <span>hear this machine's own answers</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="settings-note"  class="form-notice" role="status"></span>
                                <span id="settings-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. [V2G2-159] asks a vehicle to repeat the
                                request every 250 ms for up to a minute; the defaults here are shorter,
                                because this one is a page somebody is watching. Switch the loopback on when
                                the station is a second process on this same machine - otherwise its answer
                                never reaches this one.
                            </span>

                        </form>

                    </section>

                </div>

            `);

            wire();

        }


        /** What came back, or what did not. */
        function discovery(result: DiscoveryResult): HTMLFragment {

            const good = result.outcome === 'found';

            return html`
                <div class="query-result ${good ? 'ok' : 'bad'}">

                    <div class="kv-list">
                        <div class="kv">
                            <span class="k">Result</span>
                            <span class="v">${outcome(result)}</span>
                        </div>
                        ${result.interface  ? html`<div class="kv"><span class="k">Interface</span><span class="v"><code>${result.interface}</code></span></div>` : ''}
                        ${result.startedAt  ? html`<div class="kv"><span class="k">At</span><span class="v">${formatValue(result.startedAt)}</span></div>` : ''}
                        ${result.attempts !== undefined  ? html`<div class="kv"><span class="k">Requests sent</span><span class="v">${result.attempts}</span></div>` : ''}
                        ${result.elapsed_ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${result.elapsed_ms} ms</span></div>` : ''}
                        ${result.error      ? html`<div class="kv"><span class="k">Error</span><span class="v">${result.error}</span></div>` : ''}
                    </div>

                    ${result.secc ? html`<h3>The station</h3>${station(result.secc)}` : ''}

                    ${result.others && result.others.length > 0
                          ? html`
                              <h3>Also on this link</h3>
                              ${result.others.map(station)}
                              <p class="hint">
                                  More than one station answered. Which of them a session would go to is the
                                  first one above - the rest are shown because a link with two stations on it
                                  is usually a surprise worth knowing about.
                              </p>
                            `
                          : ''}

                    ${result.rejected && result.rejected.length > 0
                          ? html`
                              <h3>Answers that were refused</h3>
                              ${result.rejected.map(station)}
                            `
                          : ''}

                </div>
            `;

        }

        /** One station, as SDP described it. */
        function station(secc: SECC): HTMLFragment {

            return html`
                <div class="kv-list">
                    <div class="kv">
                        <span class="k">V2G endpoint</span>
                        <span class="v"><code>[${secc.address}]:${secc.port}</code></span>
                    </div>
                    <div class="kv">
                        <span class="k">Security</span>
                        <span class="v">${secc.security === 'tls' ? 'TLS' : 'no TLS'} over ${secc.transport}</span>
                    </div>
                    ${secc.from   ? html`<div class="kv"><span class="k">Answered from</span><span class="v"><code>${secc.from}</code></span></div>` : ''}
                    ${secc.reason ? html`<div class="kv"><span class="k">Refused because</span><span class="v">${secc.reason}</span></div>` : ''}
                </div>
            `;

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


        /** What came of one pairing. */
        function pairingResult(result: SlacResult): HTMLFragment {

            return html`
                <div class="query-result ${result.outcome === 'paired' ? 'ok' : 'bad'}">
                    <div class="kv-list">
                        <div class="kv"><span class="k">Result</span><span class="v">${
                            result.outcome === 'paired'        ? 'paired'
                          : result.outcome === 'notConfigured' ? 'no peer is configured'
                          : result.outcome === 'busy'          ? 'a session is running, and it is already paired'
                          : result.outcome === 'cancelled'     ? 'cancelled'
                          :                                      'the pairing failed'
                        }</span></div>
                        ${result.peer       ? html`<div class="kv"><span class="k">Peer</span><span class="v"><code>${result.peer}</code></span></div>` : ''}
                        ${result.nid        ? html`<div class="kv"><span class="k">Network</span><span class="v"><code>${result.nid}</code></span></div>` : ''}
                        ${result.elapsed_ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${result.elapsed_ms} ms</span></div>` : ''}
                        ${result.error      ? html`<div class="kv"><span class="k">Error</span><span class="v">${result.error}</span></div>` : ''}
                    </div>
                </div>
            `;

        }


        function wire(): void {

            must<HTMLButtonElement>(content, '#discover').addEventListener('click', () => void discover());
            must<HTMLButtonElement>(content, '#pair').    addEventListener('click', () => void pair());

            must<HTMLFormElement>(content, '#interface-form').addEventListener('submit', event => {

                event.preventDefault();

                const chosen = String(new FormData(event.target as HTMLFormElement).get('interface') ?? '').trim();

                // An empty selection means "the first one that could carry it".
                // That is sent as an explicit null rather than as "" or as
                // nothing at all: a missing field leaves the setting alone, and
                // "" would be a name, of which there is no such interface.
                void save('interface', { interface: chosen.length > 0 ? chosen : null });

            });

            must<HTMLFormElement>(content, '#settings-form').addEventListener('submit', event => {

                event.preventDefault();

                const data = new FormData(event.target as HTMLFormElement);

                void save('settings', {
                    requestedSecurity:            String(data.get('requestedSecurity') ?? 'tls') === 'noTls' ? 'noTls' : 'tls',
                    perAttemptTimeoutSeconds:     Number(data.get('perAttemptTimeoutMs')) / 1000,
                    maxRetries:                   Number(data.get('maxRetries')),
                    totalDeadlineSeconds:         Number(data.get('totalDeadlineSeconds')),
                    rejectNoTLSResponses:         data.get('rejectNoTLSResponses')        !== null,
                    requireLinkLocalSECCAddress:  data.get('requireLinkLocalSECCAddress') !== null,
                    multicastLoopback:            data.get('multicastLoopback')           !== null
                });

            });

        }


        async function save(which: 'interface' | 'settings', update: V2GUpdate): Promise<void> {

            const note = must<HTMLElement>(content, `#${which}-note`);

            note.textContent = '';

            must<HTMLElement>(content, `#${which}-error`).textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.v2g.save(update));
                draw();
                must<HTMLElement>(content, `#${which}-note`).textContent = 'Saved, and in effect for the next discovery.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, `#${which}-error`).textContent = errorMessage(problem);
            }

        }


        async function discover(): Promise<void> {

            must<HTMLElement>(content, '#discover-error').textContent = '';

            searching = true;
            draw();

            try
            {
                // The answer carries the whole configuration as well as the
                // result, because a discovery moves the record of the last one
                // that this page is showing.
                current = await api.v2g.discover((current?.settings.totalDeadlineSeconds ?? 10) + andABitMore);
            }
            catch (problem)
            {
                searching = false;
                draw();
                must<HTMLElement>(content, '#discover-error').textContent = errorMessage(problem);
                return;
            }

            searching = false;
            draw();

        }


        async function pair(): Promise<void> {

            must<HTMLElement>(content, '#pair-error').textContent = '';

            pairing = true;
            draw();

            try
            {
                paired = await api.v2g.pair();
            }
            catch (problem)
            {
                pairing = false;
                draw();
                must<HTMLElement>(content, '#pair-error').textContent = errorMessage(problem);
                return;
            }

            pairing = false;
            draw();

        }


        async function load(): Promise<void> {

            try
            {
                const loaded = await api.v2g.get();

                if (!cancelled) {
                    current = loaded;
                    draw();
                }
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The ISO 15118 configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#interface-form')) ||
                                             typedSinceDrawn(content.querySelector('#settings-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
