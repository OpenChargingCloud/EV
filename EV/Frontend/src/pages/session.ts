import { api, type Certificate, type CertificateKind, type CertificateStore,
         type SessionBattery, type SessionConfiguration, type SessionRun, type SessionUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

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
 */
export const sessionPage: Page = {

    title: 'Charging session',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/session',
            title:     'Charging session',
            subtitle:  'What this vehicle does once it has found a station.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayCharge      = auth.can('runSessions');
        const mayChangeLink  = auth.can('changeNetworkSettings');
        const mayChangeGoals = auth.can('changeChargingSettings');
        const mayChangeCerts = auth.can('manageCredentials');

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
                         label:  string): HTMLFragment {

            const chosen    = current?.certificates[field] ?? null;
            const available = store?.certificates[kind] ?? [];

            // A handle the store no longer has is still what the setting says,
            // so it is offered as itself rather than quietly becoming "none".
            const missing = chosen !== null && chosen.missing;

            return html`
                <label>${label}
                    <select name="${field}" ${mayChangeCerts ? '' : html`disabled`}>
                        <option value="">(none)</option>
                        ${available.map(one => html`
                            <option value="${one.id}" ${chosen?.id === one.id ? html`selected` : ''}>
                                ${describe(one)}
                            </option>
                        `)}
                        ${missing ? html`
                            <option value="${chosen!.id}" selected>${chosen!.id} - no longer in the store</option>
                        ` : ''}
                    </select>
                </label>
                ${available.length === 0 && !missing ? html`
                    <p class="hint">
                        None of this kind is in the
                        <a href="/configuration/certificates">certificate store</a> yet.
                    </p>` : ''}
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


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const settings      = configuration.settings;
            const certificates  = configuration.certificates;
            const goals         = configuration.goals;
            const running       = configuration.running;

            render(content, html`

                ${mayCharge ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at this page
                        but not charge. That needs the driver, the service or the system administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card wide">

                        <h2><i class="fa-solid fa-bolt"></i> Charge</h2>

                        <div class="form-actions">
                            <button type="button" id="charge" class="btn primary" ${mayCharge && !running ? '' : html`disabled`}>
                                ${running ? 'Charging ...' : 'Charge'}
                            </button>
                            <button type="button" id="stop" class="btn" ${mayCharge && running ? '' : html`disabled`}>
                                <i class="fa-solid fa-stop"></i> Stop
                            </button>
                            <span id="charge-error" class="form-error" role="alert"></span>
                        </div>

                        <form id="run-form" class="form-stack">

                            <label class="switch">
                                <input type="checkbox" name="pause" ${running ? html`disabled` : ''} />
                                <span>end the session paused, so that it can be rejoined</span>
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="pauseResume" ${running ? html`disabled` : ''} />
                                <span>pause and rejoin in one run - charge, pause, reconnect, carry on</span>
                            </label>

                            ${configuration.paused
                                  ? html`
                                      <label class="switch">
                                          <input type="checkbox" name="resume" ${running ? html`disabled` : ''} />
                                          <span>rejoin the paused session <code>${configuration.paused}</code></span>
                                      </label>
                                    `
                                  : ''}

                        </form>

                        <p class="hint">
                            ${running
                                  ? html`
                                      The session is running. Every message is on the <a href="/logs">Logs</a> page
                                      while it happens; the sum appears here when it ends.
                                    `
                                  : html`
                                      ${configuration.connect === null
                                            ? html`A station is looked for over SDP first.`
                                            : html`Connecting to <code>${configuration.connect}</code>.`}
                                      ${settings.slacPeer === null
                                            ? ''
                                            : html` A SLAC pairing with <code>${settings.slacPeer}</code> runs before it.`}
                                      ${settings.t1sTransport === 'none'
                                            ? ''
                                            : html` The coupler's 10BASE-T1S bus is joined over <code>${settings.t1sTransport}</code> before it.`}
                                      One iteration of the charge loop is one simulated minute, so a full charge
                                      is several hundred exchanges - name a charging time below when the station
                                      at the other end is a real one.
                                    `}
                        </p>

                        ${configuration.lastSession ? session(configuration.lastSession) : ''}

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-road"></i> Where to, and what to speak</h2>

                        <form id="link-form" class="form-stack">

                            <label>The station to drive to
                                <input type="text" name="connect" value="${configuration.connect ?? ''}"
                                       placeholder="leave empty to look for one over SDP"
                                       ${mayChangeLink ? '' : html`disabled`} />
                            </label>

                            <label>Protocol
                                <select name="protocol" ${mayChangeLink ? '' : html`disabled`}>
                                    <option value="both" ${settings.protocol === 'both' ? html`selected` : ''}>offer both, let the station pick</option>
                                    <option value="20"   ${settings.protocol === '20'   ? html`selected` : ''}>ISO 15118-20 only</option>
                                    <option value="2"    ${settings.protocol === '2'    ? html`selected` : ''}>ISO 15118-2 only</option>
                                </select>
                            </label>

                            <label>Energy transfer mode
                                <select name="mode" ${mayChangeLink ? '' : html`disabled`}>
                                    <option value="dc"  ${settings.mode === 'dc'  ? html`selected` : ''}>DC</option>
                                    <option value="ac"  ${settings.mode === 'ac'  ? html`selected` : ''}>AC</option>
                                    <option value="mcs" ${settings.mode === 'mcs' ? html`selected` : ''}>MCS (-20 only)</option>
                                </select>
                            </label>

                            <label>TLS
                                <select name="tls" ${mayChangeLink ? '' : html`disabled`}>
                                    <option value="none"   ${settings.tls === 'none'   ? html`selected` : ''}>none - plain TCP</option>
                                    <option value="dotnet" ${settings.tls === 'dotnet' ? html`selected` : ''}>.NET SslStream</option>
                                    <option value="bc"     ${settings.tls === 'bc'     ? html`selected` : ''}>BouncyCastle - the -20 profile</option>
                                </select>
                            </label>

                            <label>SLAC peer
                                <input type="text" name="slacPeer" value="${settings.slacPeer ?? ''}"
                                       placeholder="leave empty for no pairing stage"
                                       ${mayChangeLink ? '' : html`disabled`} />
                            </label>

                            <label>10BASE-T1S bus - the medium below an MCS coupler
                                <select name="t1sTransport" ${mayChangeLink ? '' : html`disabled`}>
                                    <option value="none"     ${settings.t1sTransport === 'none'     ? html`selected` : ''}>none - a CCS vehicle</option>
                                    <option value="auto"     ${settings.t1sTransport === 'auto'     ? html`selected` : ''}>auto - a real adapter where there is one</option>
                                    <option value="afpacket" ${settings.t1sTransport === 'afpacket' ? html`selected` : ''}>afpacket - a real adapter, by name (Linux)</option>
                                    <option value="udp"      ${settings.t1sTransport === 'udp'      ? html`selected` : ''}>udp - the emulated medium, for a bench</option>
                                </select>
                            </label>

                            <label>T1S bus group
                                <input type="text" name="t1sBus" value="${settings.t1sBus ?? ''}"
                                       placeholder="239.151.18.1:16118 - the emulated medium's group and port"
                                       ${mayChangeLink ? '' : html`disabled`} />
                            </label>

                            <label>T1S interface
                                <input type="text" name="t1sInterface" value="${settings.t1sInterface ?? ''}"
                                       placeholder="leave empty: the V2G interface for an adapter, the system's pick for udp"
                                       ${mayChangeLink ? '' : html`disabled`} />
                            </label>

                            <label>T1S weight - transmit opportunities per cycle, 1 to 8
                                <input type="number" name="t1sWeight" value="${settings.t1sWeight}" min="1" max="8" step="1"
                                       ${mayChangeLink ? '' : html`disabled`} />
                            </label>

                            <label class="switch">
                                <input type="checkbox" name="renegotiate" ${settings.renegotiate ? html`checked` : ''}
                                       ${mayChangeLink ? '' : html`disabled`} />
                                <span>ISO 15118-2: renegotiate after the first cycle</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChangeLink ? '' : html`disabled`}>Save</button>
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

                        <form id="goals-form" class="form-stack">

                            <label>Charge until, in kWh delivered
                                <input type="number" name="targetEnergyKWh" min="0.001" step="0.1"
                                       value="${goals.targetEnergyKWh ?? ''}" placeholder="no limit"
                                       ${mayChangeGoals ? '' : html`disabled`} />
                            </label>

                            <label>Stop after, in minutes of simulated time
                                <input type="number" name="maxChargingTimeMinutes" min="1" step="1"
                                       value="${goals.maxChargingTimeSeconds === null ? '' : Math.round(goals.maxChargingTimeSeconds / 60)}"
                                       placeholder="no limit" ${mayChangeGoals ? '' : html`disabled`} />
                            </label>

                            <label>Leaving in, in minutes
                                <input type="number" name="departureInMinutes" min="1" step="1"
                                       value="${goals.departureInSeconds === null ? '' : Math.round(goals.departureInSeconds / 60)}"
                                       placeholder="not stated" ${mayChangeGoals ? '' : html`disabled`} />
                            </label>

                            <label>The driver needs, in percent
                                <input type="number" name="minimumStateOfChargePercent" min="0" max="100" step="1"
                                       value="${goals.minimumStateOfChargePercent ?? ''}" placeholder="not stated"
                                       ${mayChangeGoals ? '' : html`disabled`} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChangeGoals ? '' : html`disabled`}>Save</button>
                                <span id="goals-note"  class="form-notice" role="status"></span>
                                <span id="goals-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Name none of these and the goal is the target state of charge on the
                                <a href="/configuration/vehicle">Vehicle</a> page. Name several and the first one
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

                        <form id="certificates-form" class="form-stack">

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
                                       placeholder="a directory" ${mayChangeCerts ? '' : html`disabled`} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChangeCerts ? '' : html`disabled`}>Save</button>
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
                                <a href="/configuration/certificates">certificate store</a>; put one there first
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

            wire();

            // While a session runs, ask again: the exchange is on the event
            // stream but the sum is not, and the sum is what this page shows.
            clearPending();

            if (running && !cancelled)
                pending = setTimeout(() => void load(true), askAgainAfter);

        }


        /** What came of one session. */
        function session(run: SessionRun): HTMLFragment {

            const good = run.outcome === 'completed';

            return html`
                <div class="query-result ${good ? 'ok' : 'bad'}">

                    <div class="kv-list">
                        <div class="kv"><span class="k">Result</span><span class="v">${outcome(run)}</span></div>
                        ${run.station      ? html`<div class="kv"><span class="k">Station</span><span class="v"><code>${run.station}</code></span></div>` : ''}
                        ${run.protocol     ? html`<div class="kv"><span class="k">Protocol</span><span class="v">ISO 15118${run.protocol}, ${run.mode}</span></div>` : ''}
                        ${run.startedAt    ? html`<div class="kv"><span class="k">At</span><span class="v">${formatValue(run.startedAt)}</span></div>` : ''}
                        ${run.elapsed_ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${(run.elapsed_ms / 1000).toFixed(1)} s</span></div>` : ''}
                        ${run.exchanges !== undefined  ? html`<div class="kv"><span class="k">Exchanges</span><span class="v">${run.exchanges}, ${run.bytesOnWire} bytes on the wire (request side)</span></div>` : ''}
                        ${run.authorization ? html`<div class="kv"><span class="k">Authorization</span><span class="v">${run.authorization}</span></div>` : ''}
                        ${run.sessionSetup  ? html`<div class="kv"><span class="k">Session setup</span><span class="v">${run.sessionSetup}</span></div>` : ''}
                        ${run.sessionId     ? html`<div class="kv"><span class="k">Session</span><span class="v"><code>${run.sessionId}</code></span></div>` : ''}
                        ${run.meteringReceipts ? html`<div class="kv"><span class="k">Metering receipts</span><span class="v">${run.meteringReceipts}</span></div>` : ''}
                        ${run.renegotiations   ? html`<div class="kv"><span class="k">Renegotiations</span><span class="v">${run.renegotiations}</span></div>` : ''}
                        ${run.error         ? html`<div class="kv"><span class="k">Error</span><span class="v">${run.error}</span></div>` : ''}
                    </div>

                    ${run.battery ? html`<h3>Battery</h3>${battery(run.battery)}` : ''}

                    ${run.contractInstalled
                          ? html`<p class="hint">A contract certificate was issued and its private key unwrapped - the ECDH round trip closed.</p>`
                          : ''}

                    ${run.resumeRefused
                          ? html`
                              <p class="hint">
                                  The station refused the rejoin and opened a new session. Everything the paused
                                  one carried, authorization included, was dropped.
                              </p>
                            `
                          : run.sameStation === true
                              ? html`<p class="hint">The rejoined session is confirmed to be with the same station, by certificate binding.</p>`
                              : ''}

                    ${run.tariff
                          ? html`
                              <h3>Tariff</h3>
                              <div class="kv-list">
                                  <div class="kv"><span class="k">Signature</span><span class="v">${run.tariff.signaturePresent ? 'present' : 'absent'}</span></div>
                                  <div class="kv"><span class="k">Digests</span><span class="v">${run.tariff.digestOk ? 'OK' : 'failed'}</span></div>
                                  <div class="kv"><span class="k">ECDSA</span><span class="v">${run.tariff.signatureOk ? 'OK' : 'failed or unverified'}</span></div>
                              </div>
                            `
                          : ''}

                    ${run.slac
                          ? html`
                              <h3>SLAC</h3>
                              <div class="kv-list">
                                  <div class="kv"><span class="k">Pairing</span><span class="v">${run.slac.outcome}</span></div>
                                  ${run.slac.nid ? html`<div class="kv"><span class="k">Network</span><span class="v"><code>${run.slac.nid}</code></span></div>` : ''}
                              </div>
                            `
                          : ''}

                    ${run.t1s
                          ? html`
                              <h3>10BASE-T1S</h3>
                              <div class="kv-list">
                                  <div class="kv"><span class="k">Bus</span><span class="v">${run.t1s.outcome}</span></div>
                                  ${run.t1s.medium            ? html`<div class="kv"><span class="k">Medium</span><span class="v"><code>${run.t1s.medium}</code></span></div>` : ''}
                                  ${run.t1s.nodeId !== undefined
                                                              ? html`<div class="kv"><span class="k">Node</span><span class="v">${run.t1s.nodeId}, ${run.t1s.weight} opportunit${run.t1s.weight === 1 ? 'y' : 'ies'} per cycle</span></div>` : ''}
                                  ${run.t1s.reason            ? html`<div class="kv"><span class="k">Reason</span><span class="v">${run.t1s.reason}</span></div>` : ''}
                                  ${run.t1s.error             ? html`<div class="kv"><span class="k">Error</span><span class="v">${run.t1s.error}</span></div>` : ''}
                              </div>
                            `
                          : ''}

                    ${run.pausedRun
                          ? html`
                              <h3>The half before the pause</h3>
                              ${session(run.pausedRun)}
                            `
                          : ''}

                </div>
            `;

        }

        /** What the pack did. */
        function battery(pack: SessionBattery): HTMLFragment {

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
                    ${pack.stoppedBecause ? html`<div class="kv"><span class="k">Stopped because</span><span class="v">${pack.stoppedBecause}</span></div>` : ''}
                    ${pack.minimumMissed
                          ? html`<div class="kv"><span class="k">The driver</span><span class="v">did not have enough by the time the session ended</span></div>`
                          : ''}
                </div>
                ${pack.describe ? html`<p class="hint">${pack.describe}</p>` : ''}
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


        function wire(): void {

            must<HTMLButtonElement>(content, '#charge').addEventListener('click', () => void charge());
            must<HTMLButtonElement>(content, '#stop').  addEventListener('click', () => void stop());

            must<HTMLFormElement>(content, '#link-form').addEventListener('submit', event => {

                event.preventDefault();

                const data    = new FormData(event.target as HTMLFormElement);
                const connect = String(data.get('connect')      ?? '').trim();
                const peer    = String(data.get('slacPeer')     ?? '').trim();
                const bus     = String(data.get('t1sBus')       ?? '').trim();
                const nic     = String(data.get('t1sInterface') ?? '').trim();
                const weight  = Number(data.get('t1sWeight'));

                // An emptied field is a setting taken back, which the vehicle
                // spells as an explicit null. Leaving it out of the request
                // would mean "change nothing".
                void save('link', {
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

            });

            must<HTMLFormElement>(content, '#goals-form').addEventListener('submit', event => {

                event.preventDefault();

                const data = new FormData(event.target as HTMLFormElement);

                void save('goals', {
                    targetEnergyKWh:              optional(data.get('targetEnergyKWh')),
                    minimumStateOfChargePercent:  optional(data.get('minimumStateOfChargePercent')),
                    // The page offers minutes because that is the unit a
                    // charging session is discussed in; the wire and the file
                    // are in seconds.
                    maxChargingTimeSeconds:       minutes(data.get('maxChargingTimeMinutes')),
                    departureInSeconds:           minutes(data.get('departureInMinutes'))
                });

            });

            must<HTMLFormElement>(content, '#certificates-form').addEventListener('submit', event => {

                event.preventDefault();

                const data = new FormData(event.target as HTMLFormElement);

                void save('certificates', {
                    vehicleCertificate:   text(data.get('vehicleCertificate')),
                    contractCertificate:  text(data.get('contractCertificate')),
                    oemCertificate:       text(data.get('oemCertificate')),
                    tariffCertificate:    text(data.get('tariffCertificate')),
                    pkiDirectory:         text(data.get('pkiDirectory'))
                });

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


        async function save(which: 'link' | 'goals' | 'certificates', update: SessionUpdate): Promise<void> {

            const note = must<HTMLElement>(content, `#${which}-note`);

            note.textContent = '';

            must<HTMLElement>(content, `#${which}-error`).textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.session.save(update));
                draw();
                must<HTMLElement>(content, `#${which}-note`).textContent = 'Saved, and in effect for the next session.';
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

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#link-form')) ||
                                             typedSinceDrawn(content.querySelector('#goals-form')) ||
                                             typedSinceDrawn(content.querySelector('#certificates-form')));

        void load();

        return () => { cancelled = true; clearPending(); release(); };

    }

};
