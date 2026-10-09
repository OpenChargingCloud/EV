import type { SessionBattery, SessionRun } from '../api/client';
import { formatValue } from '@node/ui';
import { html, nothing, type TemplateResult } from '@node/view';

/**
 * What came of one session, as the Charging page and the Stations page show
 * it: the outcome, the station, the pack, the stages before it.
 */
export function sessionResult(run: SessionRun): TemplateResult {

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
                          ${run.slac.station ? html`<div class="kv"><span class="k">Station</span><span class="v"><code>${run.slac.station.mac}</code></span></div>` : nothing}
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
                      ${sessionResult(run.pausedRun)}
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
