import type { SessionBattery, SessionClock, SessionConfirmations, SessionRun, SessionTransparency } from '../api/client';
import { formatValue } from '@node/ui';
import { html, nothing, type TemplateResult } from '@node/view';

/**
 * What came of one session, as the Charging page and the Charging Session
 * Settings page show it: the outcome, the station, the pack, the stages before it.
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

            ${run.transparency ? html`<h3>Transparency</h3>${transparency(run.transparency)}` : nothing}

            ${run.terminatedByStation
                  ? html`<p class="hint">The station told the vehicle to end the charging, so the session ended for good - not paused, whatever this run asked for, and with nothing to rejoin.</p>`
                  : nothing}

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

/**
 * The station's clock and meter beside the vehicle's own.
 *
 * The clocks come first, because every later line leans on them. A reading is
 * said to be signed or unsigned and never verified, because nothing verified
 * it; and the two counts agreeing is said to be arithmetic, because in a
 * simulator both come from one modelled charge loop.
 */
function transparency(seen: SessionTransparency): TemplateResult {

    const last = seen.readings.at(-1);

    return html`
        <div class="kv-list">
            <div class="kv">
                <span class="k">Station clock</span>
                <span class="v">${seen.stationClock ? clock(seen.stationClock, seen.clockTolerance_ms) : 'not on the wire - ISO 15118-2 has no header timestamp'}</span>
            </div>
            <div class="kv">
                <span class="k">Meter clock</span>
                <span class="v">${seen.meterClock ? clock(seen.meterClock, seen.clockTolerance_ms) : 'no reading carried a timestamp'}</span>
            </div>
            ${last
                  ? html`
                      <div class="kv">
                          <span class="k">Station meter</span>
                          <span class="v">
                              ${last.dischargedWh != null
                                    // Bidirectional: both registers, and the net figure the difference
                                    // below is taken on.
                                    ? `${last.stationWh} Wh charged, ${last.dischargedWh} Wh given back, ${last.stationNetWh ?? last.stationWh - last.dischargedWh} Wh net`
                                    : `${last.stationWh} Wh`} at <code>${last.meterId}</code>,
                              ${last.signed ? 'signed, not verified' : 'unsigned'}
                          </span>
                      </div>
                      <div class="kv"><span class="k">This vehicle</span><span class="v">${last.vehicleWh} Wh at the same reading</span></div>
                      <div class="kv">
                          <span class="k">Difference</span>
                          <span class="v">${signed(last.differenceWh)} Wh${relative(last.differenceWh, last.vehicleWh)}, over ${seen.readings.length} reading(s)</span>
                      </div>
                    `
                  : html`
                      <div class="kv"><span class="k">Station meter</span><span class="v">no reading reported</span></div>
                      <div class="kv"><span class="k">This vehicle</span><span class="v">${seen.vehicleWh} Wh</span></div>
                    `}
            ${seen.meteringConfirmations ? confirmations(seen.meteringConfirmations) : nothing}
        </div>
        ${last
              ? html`<p class="hint">Station and vehicle count the same modelled charge loop here, so their agreeing shows that field, unit and scale are right - not that a meter measured correctly.</p>`
              : html`<p class="hint">Without a reading from the station there is nothing to put beside the vehicle's own count.</p>`}
        ${seen.meteringConfirmations?.sent.length
              ? html`<p class="hint">A metering confirmation says the vehicle received the reading. It does not say the reading is right, and the vehicle's own count is not part of what it signs.</p>`
              : nothing}
    `;

}

/**
 * The -20 MeteringConfirmations: how many the vehicle signed, what the
 * station answered, the last one beside the vehicle's count, and how often
 * it was asked for one it could not sign.
 */
function confirmations(seen: SessionConfirmations): TemplateResult {

    const last = seen.sent.at(-1);

    const asked = last
                      ? `${seen.sent.length} signed with the contract key; the station answered ${answers(seen)}`
                      : seen.impossible === 0
                          ? 'the station asked for none'
                          : 'none signed';

    return html`
        <div class="kv"><span class="k">Metering confirmations</span><span class="v">${asked}</span></div>
        ${last
              ? html`
                  <div class="kv">
                      <span class="k">Last confirmed</span>
                      <span class="v">
                          ${last.stationWh} Wh at <code>${last.meterId}</code>${last.withReceipt ? ', with the cost receipt' : ''};
                          this vehicle ${last.vehicleWh} Wh, difference ${signed(last.differenceWh)} Wh
                      </span>
                  </div>
                `
              : nothing}
        ${seen.impossible > 0
              ? html`
                  <div class="kv">
                      <span class="k">Could not sign</span>
                      <span class="v">${seen.impossible} request(s) - no contract key in this session, or no reading with the request</span>
                  </div>
                `
              : nothing}
    `;

}

/** The station's answers in a few words: "OK to all", or each answer with how often it came. */
function answers(seen: SessionConfirmations): string {

    const counts = new Map<string, number>();

    for (const sent of seen.sent)
        counts.set(sent.response, (counts.get(sent.response) ?? 0) + 1);

    if (counts.size === 1)
        return `${[...counts.keys()][0]} to ${seen.sent.length === 1 ? 'it' : 'all'}`;

    return [...counts].map(([response, count]) => `${response} ${count}x`).join(', ');

}

/** One clock in a sentence: how far, how sure, and whether it moved. */
function clock(seen: SessionClock, tolerance_ms: number): string {

    const offset = `${seconds(seen.offset_ms)}, resolved to ±${(seen.uncertainty_ms / 1000).toFixed(1)} s`;

    const drift  = seen.drifted && seen.drift_ms !== null
                       ? `; moved ${seconds(seen.drift_ms)} over ${seen.samples} timestamps`
                       : '';

    const verdict = seen.agrees === false
                        ? ` - more than the ${(tolerance_ms / 1000).toFixed(0)} s this vehicle tolerates`
                        : '';

    return offset + drift + verdict;

}

/** An offset in words: "0.3 s ahead", "90.2 s behind". */
function seconds(ms: number): string {
    return `${(Math.abs(ms) / 1000).toFixed(1)} s ${ms < 0 ? 'behind' : 'ahead'}`;
}

/** A difference with its sign, so that a zero reads as one. */
function signed(value: number): string {
    return value > 0 ? `+${value}` : `${value}`;
}

/** The difference relative to the vehicle's count, where that is not zero. */
function relative(difference: number, vehicle: number): string {
    return vehicle === 0 ? '' : ` (${signed(Number((100 * difference / vehicle).toFixed(2)))} %)`;
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
