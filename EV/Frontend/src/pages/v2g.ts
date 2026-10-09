import { api, type SlacResult, type V2GConfiguration, type V2GUpdate } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, whileSaving } from '@node/ui';
import { typedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The wire below the charging cable, from this vehicle's side: which
 * interface it speaks ISO 15118 on, and a SLAC pairing on its own.
 *
 * Looking for a station, and what a discovery asks for, are on the Charging
 * page - "/" - where a station found is charged at.
 *
 * Nothing on this page starts a charging session. A pairing asks a question;
 * it connects to nothing and draws nothing.
 *
 * Drawn by view.ts: a draw changes only what differs, so that what is typed
 * into its form - and its focus - outlives a pairing being drawn.
 */
export const v2gPage: Page = {

    title: 'ISO 15118',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/v2g',
            title:     'ISO 15118',
            subtitle:  'The wire below the charging cable: which interface this vehicle speaks ISO 15118 on, and a SLAC pairing on its own.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('v2g', 'edit');
        const mayRun    = auth.can('v2g', 'run');

        let cancelled  = false;
        let current: V2GConfiguration | null = null;
        let pairing    = false;
        let paired: SlacResult | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const candidates    = configuration.interfaces;

            render(content, html`

                ${mayRun ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at this page', 'send anything on the link')}
                    </div>
                `}

                <div class="cards">

                    <section class="card wide">

                        <h2><i class="fa-solid fa-plug-circle-bolt"></i> Pair over SLAC</h2>

                        <div class="form-actions">
                            <button type="button" id="pair" class="btn" ?disabled=${!mayRun || pairing}
                                    @click=${() => void pair()}>
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
                            on the <a href="${toURL('/configuration/session')}">Charging Session</a> page. A session pairs by
                            itself where one is configured - this button is here because SLAC agreeing and
                            SDP finding nothing is a very different link from SLAC never agreeing at all.
                        </p>

                        ${paired ? pairingResult(paired) : nothing}

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-ethernet"></i> Interface</h2>

                        <form id="interface-form" class="form-stack" @submit=${saveInterface}>

                            <label>The interface the station is on
                                <select name="interface" ?disabled=${!mayChange}>
                                    <option value="" ?selected=${configuration.interface === null}>
                                        the first one that could carry it
                                    </option>
                                    ${repeat(candidates, candidate => candidate.name, candidate => html`
                                        <option value="${candidate.name}" ?selected=${configuration.interface === candidate.name}>
                                            ${candidate.name}
                                        </option>
                                    `)}
                                </select>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="interface-note"  class="form-notice" role="status"></span>
                                <span id="interface-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                On a real vehicle this is the powerline modem. On a bench it is whichever
                                interface the station is reachable over. An interface only appears here when
                                it is up, has a MAC address and has an IPv6 link-local address - SDP needs
                                all three. A station is looked for on the <a href="${toURL('/')}">Charging</a> page.
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
                                      ${repeat(candidates, candidate => candidate.name, candidate => html`
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

                </div>

            `);

        }


        /** What came of one pairing. */
        function pairingResult(result: SlacResult): TemplateResult {

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
                        ${result.peer       ? html`<div class="kv"><span class="k">Peer</span><span class="v"><code>${result.peer}</code></span></div>` : nothing}
                        ${result.station    ? html`<div class="kv"><span class="k">The station at the cable</span><span class="v"><code>${result.station.mac}</code>${result.station.attenuation_dB !== undefined ? html` <span class="muted">${result.station.attenuation_dB} dB</span>` : nothing}</span></div>` : nothing}
                        ${result.nid        ? html`<div class="kv"><span class="k">Network</span><span class="v"><code>${result.nid}</code></span></div>` : nothing}
                        ${result.elapsed_ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${result.elapsed_ms} ms</span></div>` : nothing}
                        ${result.error      ? html`<div class="kv"><span class="k">Error</span><span class="v">${result.error}</span></div>` : nothing}
                    </div>
                </div>
            `;

        }


        function saveInterface(event: SubmitEvent): void {

            event.preventDefault();

            const form   = event.currentTarget as HTMLFormElement;
            const chosen = String(new FormData(form).get('interface') ?? '').trim();

            // An empty selection means "the first one that could carry it".
            // That is sent as an explicit null rather than as "" or as nothing
            // at all: a missing field leaves the setting alone, and "" would be
            // a name, of which there is no such interface.
            void save(form, 'interface', { interface: chosen.length > 0 ? chosen : null });

        }


        async function save(form: HTMLFormElement, which: 'interface', update: V2GUpdate): Promise<void> {

            const note = must<HTMLElement>(content, `#${which}-note`);

            note.textContent = '';

            must<HTMLElement>(content, `#${which}-error`).textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.v2g.save(update));

                if (cancelled)
                    return;

                draw();

                // A draw leaves a form as it is typed into; this one was
                // saved, so it goes back to what it says now - the answer.
                form.reset();

                note.textContent = 'Saved, and in effect for the next discovery.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, `#${which}-error`).textContent = errorMessage(problem);
            }

        }


        async function pair(): Promise<void> {

            must<HTMLElement>(content, '#pair-error').textContent = '';

            // The last pairing's result goes as soon as the next one starts:
            // left standing under "Sounding ...", it read as the new answer.
            paired  = null;
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

        /**
         * Loaded anew - Reload - is what the vehicle has, the forms too, which
         * a draw on its own would leave as typed.
         */
        async function reload(): Promise<void> {

            await load();

            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());

        }

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#interface-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
