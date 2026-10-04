import { api, type Certificate, type CertificateKind, type CertificateStore,
         type SessionBattery, type SessionConfiguration, type SessionRun, type SessionUpdate } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, formatValue, numberField, whileSaving } from '@node/ui';
import { typedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * How often this page asks whether the session is over.
 *
 * The exchange itself is on the event stream and shows up on the Logs page
 * while it happens; what polling is for is the one thing the log does not
 * carry - the sum, which exists only when the session ends. Two seconds is
 * short enough that the result appears while somebody is still looking and
 * long enough that a session lasting ten minutes is 300 requests rather than
 * 30,000.
 */
const askAgainAfter = 2_000;


/**
 * Charging: what this vehicle does once it has found a station.
 *
 * The button starts a session and the page does not wait for it. That is not a
 * shortcut - a full charge is hundreds of exchanges and minutes of wall clock,
 * and a request held open that long is a request that times out. So the
 * vehicle answers "started", the exchange arrives on the event stream, and this
 * page asks again every couple of seconds until it is over.
 *
 * Which makes the Logs page the interesting one while a session runs: every
 * message, every certificate decision and every charge-loop iteration is
 * written there as it happens.
 *
 * Drawn by view.ts: a draw changes only what differs, so that what is typed
 * into one of its forms - and its focus - outlives another being saved, and
 * the page being drawn every two seconds while a session runs.
 */
export const sessionPage: Page = {

    title: 'Charging session',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/session',
            title:     'Charging session',
            subtitle:  'What this vehicle does once it has found a station.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayCharge      = auth.can('session', 'run');
        const mayChangeLink  = auth.can('v2g', 'edit');
        const mayChangeGoals = auth.can('session', 'edit');
        const mayChangeCerts = auth.can('certificates', 'edit');

        let cancelled = false;
        let current: SessionConfiguration | null = null;
        let pending: ReturnType<typeof setTimeout> | null = null;

        // What there is to choose from. Fetched once beside the settings and
        // not polled with them: the store changes when somebody changes it, and
        // this page polls every two seconds for the running session.
        let store: CertificateStore | null = null;


        /**
         * One certificate slot, as a list of what the store holds of that kind.
         *
         * A list rather than a text field, because a handle is sixteen
         * hexadecimal digits and nobody should be typing one. An expired or
         * switched-off certificate is still offered, marked as what it is: it is
         * a legitimate thing to have chosen, and hiding it would make a setting
         * somebody already made look like no setting at all.
         *
         * Every slot may also be set to nothing, which is what "the vehicle does
         * not present one at all" is.
         */
        function chooser(field:  'vehicleCertificate' | 'contractCertificate' | 'oemCertificate' | 'tariffCertificate',
                         kind:   CertificateKind,
                         label:  string): TemplateResult {

            const chosen    = current?.certificates[field] ?? null;
            const available = store?.certificates[kind] ?? [];

            // A handle the store no longer has is still what the setting says,
            // so it is offered as itself rather than quietly becoming "none".
            const missing = chosen !== null && chosen.missing;

            // "(none)" says it is the one drawn where no other option is. Left
            // to the browser, which picks the first option of a list nobody
            // marked, it was chosen and not drawn - and an untouched page asked
            // before it was left, whenever a slot was empty.
            const marked  = missing || available.some(one => one.id === chosen?.id);

            return html`
                <label>${label}
                    <select name="${field}" ?disabled=${!mayChangeCerts}>
                        <option value="" ?selected=${!marked}>(none)</option>
                        ${repeat(available, one => one.id, one => html`
                            <option value="${one.id}" ?selected=${chosen?.id === one.id}>
                                ${describe(one)}
                            </option>
                        `)}
                        ${missing ? html`
                            <option value="${chosen!.id}" selected>${chosen!.id} - no longer in the store</option>
                        ` : nothing}
                    </select>
                </label>
                ${available.length === 0 && !missing ? html`
                    <p class="hint">
                        None of this kind is in the
                        <a href="${toURL('/configuration/certificates')}">certificate store</a> yet.
                    </p>` : nothing}
            `;

        }


        /** One certificate, as a line in a list somebody is choosing from. */
        function describe(one: Certificate): string {

            const state = one.expired     ? ' - EXPIRED'
                        : one.notYetValid ? ' - not yet valid'
                        : !one.active     ? ' - switched off'
                        : '';

            return `${one.label} (${one.keyAlgorithm}, until ${one.notAfter.slice(0, 10)})${state}`;

        }


        /**
         * Draw the page from what the vehicle said last - the whole of it, also
         * every two seconds while a session runs: a draw changes only what
         * differs, so that a half-typed goal outlives it, and its focus.
         */
        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const settings      = configuration.settings;
            const certificates  = configuration.certificates;
            const goals         = configuration.goals;
            const running       = configuration.running;

            render(content, html`

                ${mayCharge ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at this page', 'charge')}
                    </div>
                `}

                <div class="cards">

                    <section class="card wide" id="charge-card">

                        <h2><i class="fa-solid fa-bolt"></i> Charge</h2>

                        <div class="form-actions">
                            <button type="button" id="charge" class="btn primary" ?disabled=${!mayCharge || running}
                                    @click=${() => void charge()}>
                                ${running ? 'Charging ...' : 'Charge'}
                            </button>
                            <button type="button" id="stop" class="btn" ?disabled=${!mayCharge || !running}
                                    @click=${() => void stop()}>
                                <i class="fa-solid fa-stop"></i> Stop
                            </button>
                            <span id="charge-error" class="form-error" role="alert"></span>
                        </div>

                        <form id="run-form" class="form-stack">

                            <label class="switch">
                                <input type="checkbox" name="pause" ?disabled=${running} />
                                <span>end the session paused, so that it can be rejoined</span>
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="pauseResume" ?disabled=${running} />
                                <span>pause and rejoin in one run - charge, pause, reconnect, carry on</span>
                            </label>

                            ${configuration.paused
                                  ? html`
                                      <label class="switch">
                                          <input type="checkbox" name="resume" ?disabled=${running} />
                                          <span>rejoin the paused session <code>${configuration.paused}</code></span>
                                      </label>
                                    `
                                  : nothing}

                        </form>

                        <p class="hint">
                            ${running
                                  ? html`
                                      The session is running. Every message is on the <a href="${toURL('/logs')}">Logs</a> page
                                      while it happens; the sum appears here when it ends.
                                    `
                                  : html`
                                      ${configuration.connect === null
                                            ? html`A station is looked for over SDP first.`
                                            : html`Connecting to <code>${configuration.connect}</code>.`}
                                      ${settings.slacPeer === null
                                            ? nothing
                                            : html` A SLAC pairing with <code>${settings.slacPeer}</code> runs before it.`}
                                      ${settings.t1sTransport === 'none'
                                            ? nothing
                                            : html` The coupler's 10BASE-T1S bus is joined over <code>${settings.t1sTransport}</code> before it.`}
                                      One iteration of the charge loop is one simulated minute, so a full charge
                                      is several hundred exchanges - name a charging time below when the station
                                      at the other end is a real one.
                                    `}
                        </p>

                        ${configuration.lastSession ? session(configuration.lastSession) : nothing}

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-road"></i> Where to, and what to speak</h2>

                        <form id="link-form" class="form-stack" @submit=${saveLink}>

                            <label>The station to drive to
                                <input type="text" name="connect" value="${configuration.connect ?? ''}"
                                       placeholder="leave empty to look for one over SDP"
                                       ?disabled=${!mayChangeLink} />
                            </label>

                            <label>Protocol
                                <select name="protocol" ?disabled=${!mayChangeLink}>
                                    <option value="both" ?selected=${settings.protocol === 'both'}>offer both, let the station pick</option>
                                    <option value="20"   ?selected=${settings.protocol === '20'}>ISO 15118-20 only</option>
                                    <option value="2"    ?selected=${settings.protocol === '2'}>ISO 15118-2 only</option>
                                </select>
                            </label>

                            <label>Energy transfer mode
                                <select name="mode" ?disabled=${!mayChangeLink}>
                                    <option value="dc"  ?selected=${settings.mode === 'dc'}>DC</option>
                                    <option value="ac"  ?selected=${settings.mode === 'ac'}>AC</option>
                                    <option value="mcs" ?selected=${settings.mode === 'mcs'}>MCS (-20 only)</option>
                                </select>
                            </label>

                            <label>TLS
                                <select name="tls" ?disabled=${!mayChangeLink}>
                                    <option value="none"   ?selected=${settings.tls === 'none'}>none - plain TCP</option>
                                    <option value="dotnet" ?selected=${settings.tls === 'dotnet'}>.NET SslStream</option>
                                    <option value="bc"     ?selected=${settings.tls === 'bc'}>BouncyCastle - the -20 profile</option>
                                </select>
                            </label>

                            <label>SLAC peer
                                <input type="text" name="slacPeer" value="${settings.slacPeer ?? ''}"
                                       placeholder="leave empty for no pairing stage"
                                       ?disabled=${!mayChangeLink} />
                            </label>

                            <label>10BASE-T1S bus - the medium below an MCS coupler
                                <select name="t1sTransport" ?disabled=${!mayChangeLink}>
                                    <option value="none"     ?selected=${settings.t1sTransport === 'none'}>none - a CCS vehicle</option>
                                    <option value="auto"     ?selected=${settings.t1sTransport === 'auto'}>auto - a real adapter where there is one</option>
                                    <option value="afpacket" ?selected=${settings.t1sTransport === 'afpacket'}>afpacket - a real adapter, by name (Linux)</option>
                                    <option value="udp"      ?selected=${settings.t1sTransport === 'udp'}>udp - the emulated medium, for a bench</option>
                                </select>
                            </label>

                            <label>T1S bus group
                                <input type="text" name="t1sBus" value="${settings.t1sBus ?? ''}"
                                       placeholder="239.151.18.1:16118 - the emulated medium's group and port"
                                       ?disabled=${!mayChangeLink} />
                            </label>

                            <label>T1S interface
                                <input type="text" name="t1sInterface" value="${settings.t1sInterface ?? ''}"
                                       placeholder="leave empty: the V2G interface for an adapter, the system's pick for udp"
                                       ?disabled=${!mayChangeLink} />
                            </label>

                            <label>T1S weight - transmit opportunities per cycle, 1 to 8
                                <input type="number" name="t1sWeight" value="${settings.t1sWeight}" min="1" max="8" step="1"
                                       ?disabled=${!mayChangeLink} />
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="renegotiate" ?checked=${settings.renegotiate}
                                       ?disabled=${!mayChangeLink} />
                                <span>ISO 15118-2: renegotiate after the first cycle</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChangeLink}>Save</button>
                                <span id="link-note"  class="form-notice" role="status"></span>
                                <span id="link-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                The mode is not negotiated - the connector decides it, and a station told
                                something else fails on a message set it did not expect. The protocol is:
                                offering both is what a modern vehicle does, and a -2-only station is then met
                                without a second connection. On Windows and macOS a real -20 TLS session needs
                                the BouncyCastle backend; .NET's cannot carry the profile there at all.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-flag-checkered"></i> When to stop</h2>

                        <form id="goals-form" class="form-stack" @submit=${saveGoals}>

                            <label>Charge until, in kWh delivered
                                <input type="number" name="targetEnergyKWh" min="0.001" step="0.001"
                                       value="${goals.targetEnergyKWh ?? ''}" placeholder="no limit"
                                       ?disabled=${!mayChangeGoals} />
                            </label>

                            <label>Stop after, in minutes of simulated time
                                <input type="number" name="maxChargingTimeMinutes" min="1" step="1"
                                       value="${goals.maxChargingTimeSeconds === null ? '' : Math.round(goals.maxChargingTimeSeconds / 60)}"
                                       placeholder="no limit" ?disabled=${!mayChangeGoals} />
                            </label>

                            <label>Leaving in, in minutes
                                <input type="number" name="departureInMinutes" min="1" step="1"
                                       value="${goals.departureInSeconds === null ? '' : Math.round(goals.departureInSeconds / 60)}"
                                       placeholder="not stated" ?disabled=${!mayChangeGoals} />
                            </label>

                            <label>The driver needs, in percent
                                <input type="number" name="minimumStateOfChargePercent" min="0" max="100" step="1"
                                       value="${goals.minimumStateOfChargePercent ?? ''}" placeholder="not stated"
                                       ?disabled=${!mayChangeGoals} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChangeGoals}>Save</button>
                                <span id="goals-note"  class="form-notice" role="status"></span>
                                <span id="goals-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Name none of these and the goal is the target state of charge on the
                                <a href="${toURL('/configuration/vehicle')}">Vehicle</a> page. Name several and the first one
                                reached ends the session. The last one is a floor and not a goal: it cannot
                                prolong a session - you cannot charge after driving off - and what it does is
                                turn "the session ended" into "the session ended and the driver had enough, or
                                did not". A departure time is also the one goal that goes on the wire, as
                                ISO 15118-20's DepartureTime, which a Dynamic station schedules against.
                            </span>

                        </form>

                    </section>

                    <section class="card wide">

                        <h2><i class="fa-solid fa-key"></i> Certificates</h2>

                        <form id="certificates-form" class="form-stack" @submit=${saveCertificates}>

                            ${chooser('vehicleCertificate',  'vehicle',
                                      'Vehicle certificate - who this vehicle is')}

                            ${chooser('contractCertificate', 'contract',
                                      'Contract certificate - who pays')}

                            ${chooser('oemCertificate',      'oemProvisioning',
                                      'OEM provisioning certificate - what the vehicle was born with')}

                            ${chooser('tariffCertificate',   'tariffVerification',
                                      "Tariff certificate - what a station's signed tariff is checked with")}

                            <label>PKI directory - the development hierarchy a station minted
                                <input type="text" name="pkiDirectory" value="${certificates.pkiDirectory ?? ''}"
                                       placeholder="a directory" ?disabled=${!mayChangeCerts} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChangeCerts}>Save</button>
                                <span id="certificates-note"  class="form-notice" role="status"></span>
                                <span id="certificates-error" class="form-error"  role="alert"></span>
                            </div>

                            <div class="kv-list">
                                <div class="kv">
                                    <span class="k">Trust anchors believed</span>
                                    <span class="v">
                                        ${certificates.trustAnchors.v2gRoot} V2G,
                                        ${certificates.trustAnchors.moRoot} Mobility Operator,
                                        ${certificates.trustAnchors.oemRoot} OEM
                                    </span>
                                </div>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. These name certificates in this vehicle's
                                <a href="${toURL('/configuration/certificates')}">certificate store</a>; put one there first
                                and it appears here. They are not interchangeable, and mixing them up produces
                                failures that read like protocol bugs: the Vehicle one says who this vehicle is,
                                the contract one says who pays, the OEM one is what it was born with and all it
                                can prove before it holds a contract. Which roots are believed is not chosen
                                per session - every switched-on root of a kind is - so that is managed in the
                                store as well.
                            </span>

                        </form>

                    </section>

                </div>

            `);

            // While a session runs, ask again: the exchange is on the event
            // stream but the sum is not, and the sum is what this page shows.
            clearPending();

            if (running && !cancelled)
                pending = setTimeout(() => void load(true), askAgainAfter);

        }


        /** What came of one session. */
        function session(run: SessionRun): TemplateResult {

            const good = run.outcome === 'completed';

            return html`
                <div class="query-result ${good ? 'ok' : 'bad'}">

                    <div class="kv-list">
                        <div class="kv"><span class="k">Result</span><span class="v">${outcome(run)}</span></div>
                        ${run.station      ? html`<div class="kv"><span class="k">Station</span><span class="v"><code>${run.station}</code></span></div>` : nothing}
                        ${run.protocol     ? html`<div class="kv"><span class="k">Protocol</span><span class="v">ISO 15118${run.protocol}, ${run.mode}</span></div>` : nothing}
                        ${run.startedAt    ? html`<div class="kv"><span class="k">At</span><span class="v">${formatValue(run.startedAt)}</span></div>` : nothing}
                        ${run.elapsed_ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${(run.elapsed_ms / 1000).toFixed(1)} s</span></div>` : nothing}
                        ${run.exchanges !== undefined  ? html`<div class="kv"><span class="k">Exchanges</span><span class="v">${run.exchanges}, ${run.bytesOnWire} bytes on the wire (request side)</span></div>` : nothing}
                        ${run.authorization ? html`<div class="kv"><span class="k">Authorization</span><span class="v">${run.authorization}</span></div>` : nothing}
                        ${run.sessionSetup  ? html`<div class="kv"><span class="k">Session setup</span><span class="v">${run.sessionSetup}</span></div>` : nothing}
                        ${run.sessionId     ? html`<div class="kv"><span class="k">Session</span><span class="v"><code>${run.sessionId}</code></span></div>` : nothing}
                        ${run.meteringReceipts ? html`<div class="kv"><span class="k">Metering receipts</span><span class="v">${run.meteringReceipts}</span></div>` : nothing}
                        ${run.renegotiations   ? html`<div class="kv"><span class="k">Renegotiations</span><span class="v">${run.renegotiations}</span></div>` : nothing}
                        ${run.error         ? html`<div class="kv"><span class="k">Error</span><span class="v">${run.error}</span></div>` : nothing}
                    </div>

                    ${run.battery ? html`<h3>Battery</h3>${battery(run.battery)}` : nothing}

                    ${run.contractInstalled
                          ? html`<p class="hint">A contract certificate was issued and its private key unwrapped - the ECDH round trip closed.</p>`
                          : nothing}

                    ${run.resumeRefused
                          ? html`
                              <p class="hint">
                                  The station refused the rejoin and opened a new session. Everything the paused
                                  one carried, authorization included, was dropped.
                              </p>
                            `
                          : run.sameStation === true
                              ? html`<p class="hint">The rejoined session is confirmed to be with the same station, by certificate binding.</p>`
                              : nothing}

                    ${run.tariff
                          ? html`
                              <h3>Tariff</h3>
                              <div class="kv-list">
                                  <div class="kv"><span class="k">Signature</span><span class="v">${run.tariff.signaturePresent ? 'present' : 'absent'}</span></div>
                                  <div class="kv"><span class="k">Digests</span><span class="v">${run.tariff.digestOk ? 'OK' : 'failed'}</span></div>
                                  <div class="kv"><span class="k">ECDSA</span><span class="v">${run.tariff.signatureOk ? 'OK' : 'failed or unverified'}</span></div>
                              </div>
                            `
                          : nothing}

                    ${run.slac
                          ? html`
                              <h3>SLAC</h3>
                              <div class="kv-list">
                                  <div class="kv"><span class="k">Pairing</span><span class="v">${run.slac.outcome}</span></div>
                                  ${run.slac.nid ? html`<div class="kv"><span class="k">Network</span><span class="v"><code>${run.slac.nid}</code></span></div>` : nothing}
                              </div>
                            `
                          : nothing}

                    ${run.t1s
                          ? html`
                              <h3>10BASE-T1S</h3>
                              <div class="kv-list">
                                  <div class="kv"><span class="k">Bus</span><span class="v">${run.t1s.outcome}</span></div>
                                  ${run.t1s.medium            ? html`<div class="kv"><span class="k">Medium</span><span class="v"><code>${run.t1s.medium}</code></span></div>` : nothing}
                                  ${run.t1s.nodeId !== undefined
                                                              ? html`<div class="kv"><span class="k">Node</span><span class="v">${run.t1s.nodeId}, ${run.t1s.weight} opportunit${run.t1s.weight === 1 ? 'y' : 'ies'} per cycle</span></div>` : nothing}
                                  ${run.t1s.reason            ? html`<div class="kv"><span class="k">Reason</span><span class="v">${run.t1s.reason}</span></div>` : nothing}
                                  ${run.t1s.error             ? html`<div class="kv"><span class="k">Error</span><span class="v">${run.t1s.error}</span></div>` : nothing}
                              </div>
                            `
                          : nothing}

                    ${run.pausedRun
                          ? html`
                              <h3>The half before the pause</h3>
                              ${session(run.pausedRun)}
                            `
                          : nothing}

                </div>
            `;

        }

        /** What the pack did. */
        function battery(pack: SessionBattery): TemplateResult {

            return html`
                <div class="kv-list">
                    <div class="kv">
                        <span class="k">State of charge</span>
                        <span class="v">
                            ${pack.startedAtPercent.toFixed(0)} % &rarr; ${pack.stateOfChargePercent.toFixed(1)} %
                            of ${pack.capacityKWh.toFixed(1)} kWh
                        </span>
                    </div>
                    <div class="kv">
                        <span class="k">Delivered</span>
                        <span class="v">${pack.deliveredKWh.toFixed(3)} kWh over ${pack.simulatedMinutes} simulated minute(s)</span>
                    </div>
                    ${pack.stoppedBecause ? html`<div class="kv"><span class="k">Stopped because</span><span class="v">${pack.stoppedBecause}</span></div>` : nothing}
                    ${pack.minimumMissed
                          ? html`<div class="kv"><span class="k">The driver</span><span class="v">did not have enough by the time the session ended</span></div>`
                          : nothing}
                </div>
                ${pack.describe ? html`<p class="hint">${pack.describe}</p>` : nothing}
            `;

        }

        /** The outcome in the words somebody would use. */
        function outcome(run: SessionRun): string {

            switch (run.outcome)
            {
                case 'completed':   return 'the session ran to SessionStop';
                case 'cancelled':   return 'the session was stopped';
                case 'busy':        return 'a session was already running';
                case 'slacFailed':  return 'the SLAC pairing did not complete, so no session was started';
                case 't1sFailed':   return 'the vehicle could not join the coupler\'s bus, so no session was started';
                case 'noStation':   return 'there was no station to drive to';
                default:            return 'the session failed';
            }

        }


        function saveLink(event: SubmitEvent): void {

            event.preventDefault();

            const form    = event.currentTarget as HTMLFormElement;
            const data    = new FormData(form);
            const connect = String(data.get('connect')      ?? '').trim();
            const peer    = String(data.get('slacPeer')     ?? '').trim();
            const bus     = String(data.get('t1sBus')       ?? '').trim();
            const nic     = String(data.get('t1sInterface') ?? '').trim();
            const weight  = numberField(form, 't1sWeight');

            // An emptied field is a setting taken back, which the vehicle spells
            // as an explicit null. Leaving it out of the request would mean
            // "change nothing".
            void save(form, 'link', {
                connect:      connect.length > 0 ? connect : null,
                slacPeer:     peer.length    > 0 ? peer    : null,
                t1sTransport: String(data.get('t1sTransport') ?? 'none') as 'none' | 'auto' | 'afpacket' | 'udp',
                t1sBus:       bus.length     > 0 ? bus     : null,
                t1sInterface: nic.length     > 0 ? nic     : null,
                t1sWeight:    Number.isInteger(weight) && weight >= 1 && weight <= 8 ? weight : null,
                protocol:     String(data.get('protocol') ?? 'both') as 'both' | '2' | '20',
                mode:         String(data.get('mode')     ?? 'dc')   as 'ac' | 'dc' | 'mcs',
                tls:          String(data.get('tls')      ?? 'none') as 'none' | 'dotnet' | 'bc',
                renegotiate:  data.get('renegotiate') !== null
            });

        }

        function saveGoals(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const data = new FormData(form);

            void save(form, 'goals', {
                targetEnergyKWh:              optional(data.get('targetEnergyKWh')),
                minimumStateOfChargePercent:  optional(data.get('minimumStateOfChargePercent')),
                // The page offers minutes because that is the unit a charging
                // session is discussed in; the wire and the file are in seconds.
                maxChargingTimeSeconds:       minutes(data.get('maxChargingTimeMinutes')),
                departureInSeconds:           minutes(data.get('departureInMinutes'))
            });

        }

        function saveCertificates(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const data = new FormData(form);

            void save(form, 'certificates', {
                vehicleCertificate:   text(data.get('vehicleCertificate')),
                contractCertificate:  text(data.get('contractCertificate')),
                oemCertificate:       text(data.get('oemCertificate')),
                tariffCertificate:    text(data.get('tariffCertificate')),
                pkiDirectory:         text(data.get('pkiDirectory'))
            });

        }

        /** An emptied text field is a setting taken back. */
        function text(value: FormDataEntryValue | null): string | null {
            const trimmed = String(value ?? '').trim();
            return trimmed.length > 0 ? trimmed : null;
        }

        /** An emptied number field is a goal taken back. */
        function optional(value: FormDataEntryValue | null): number | null {
            const trimmed = String(value ?? '').trim();
            return trimmed.length > 0 ? Number(trimmed) : null;
        }

        /** The same, in minutes on the page and seconds on the wire. */
        function minutes(value: FormDataEntryValue | null): number | null {
            const asNumber = optional(value);
            return asNumber === null ? null : asNumber * 60;
        }


        async function save(form: HTMLFormElement, which: 'link' | 'goals' | 'certificates', update: SessionUpdate): Promise<void> {

            const note = must<HTMLElement>(content, `#${which}-note`);

            note.textContent = '';

            must<HTMLElement>(content, `#${which}-error`).textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.session.save(update));

                if (cancelled)
                    return;

                draw();

                // A draw leaves a form as it is typed into; this one was
                // saved, so it goes back to what it says now - the answer.
                form.reset();

                note.textContent = 'Saved, and in effect for the next session.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, `#${which}-error`).textContent = errorMessage(problem);
            }

        }


        async function charge(): Promise<void> {

            must<HTMLElement>(content, '#charge-error').textContent = '';

            const form = must<HTMLFormElement>(content, '#run-form');
            const data = new FormData(form);

            try
            {

                await api.session.start({
                    pause:        data.get('pause')       !== null,
                    pauseResume:  data.get('pauseResume') !== null,
                    resume:       data.get('resume') !== null && current?.paused ? current.paused : undefined
                });

                // The vehicle answered "started", not "finished". Ask it what
                // is happening, which sets the polling going.
                await load(true);

            }
            catch (problem)
            {
                must<HTMLElement>(content, '#charge-error').textContent = errorMessage(problem);
            }

        }


        async function stop(): Promise<void> {

            must<HTMLElement>(content, '#charge-error').textContent = '';

            try
            {
                await api.session.stop();
                await load(true);
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#charge-error').textContent = errorMessage(problem);
            }

        }


        /**
         * @param quietly  while polling: a failure to reach the vehicle mid-session
         *                 must not replace the page with an error box, because the
         *                 next attempt two seconds later usually succeeds.
         */
        async function load(quietly = false): Promise<void> {

            try
            {
                const loaded = await api.session.get();

                // Only on the first pass: the polling below is for the running
                // session, and refetching the store thirty times a minute would
                // be asking a question nobody changed the answer to.
                if (store === null)
                    store = await api.certificates.get().catch(() => null);

                if (!cancelled) {
                    current = loaded;
                    draw();
                }
            }
            catch (problem)
            {
                if (cancelled)
                    return;

                if (quietly && current !== null) {
                    pending = setTimeout(() => void load(true), askAgainAfter);
                    return;
                }

                render(content, html`
                    <div class="error-box">The charging session could not be loaded: ${errorMessage(problem)}</div>
                `);
            }

        }

        function clearPending(): void {
            if (pending !== null) {
                clearTimeout(pending);
                pending = null;
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

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#run-form')) ||
                                             typedSinceDrawn(content.querySelector('#link-form')) ||
                                             typedSinceDrawn(content.querySelector('#goals-form')) ||
                                             typedSinceDrawn(content.querySelector('#certificates-form')));

        void load();

        return () => { cancelled = true; clearPending(); release(); };

    }

};
