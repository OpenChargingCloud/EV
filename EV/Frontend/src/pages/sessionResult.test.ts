/**
 * What came of one session, drawn in a document of happy-dom: a session the
 * station told to end says so, and one it did not says nothing about it.
 */

import '../../test/vehicle.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { SessionRun } from '../api/client.ts';

const { render }         = await import('@node/view');
const { sessionResult }  = await import('./sessionResult.ts');


/** The text the result is drawn as. */
function drawn(run: SessionRun): string {
    const root = document.createElement('div');
    render(root, sessionResult(run));
    return root.textContent ?? '';
}

const completed: SessionRun = { outcome: 'completed', protocol: '-20', mode: 'MCS', paused: false };


describe('a session result', () => {

    it('says the station told the vehicle to end the charging, and that nothing is left to rejoin', () => {
        const text = drawn({ ...completed, terminatedByStation: true });
        assert.match(text, /The station told the vehicle to end the charging/);
        assert.match(text, /nothing to rejoin/);
    });

    it('says nothing of it where the station did not', () => {
        assert.doesNotMatch(drawn({ ...completed, terminatedByStation: false }), /told the vehicle to end/);
        assert.doesNotMatch(drawn(completed),                                     /told the vehicle to end/);
    });

});


describe('what a session result says of the station\'s clock and meter', () => {

    const clock    = { samples: 12, unreadable: 0, firstOffset_ms: 300, offset_ms: 300, uncertainty_ms: 600, drift_ms: 0, agrees: true };

    const reading  = { sample: 3, meterId: 'VAN*M1', stationWh: 4200, vehicleWh: 4200, differenceWh: 0, signed: true, meterTime: '2026-11-27T12:00:00.0000000+00:00' };

    const seen     = { clockTolerance_ms: 5000, stationClock: clock, meterClock: clock, vehicleWh: 4200, readings: [ reading ] };

    it('puts the station\'s reading beside the vehicle\'s own count, and their difference', () => {
        const text = drawn({ ...completed, transparency: seen });
        assert.match(text, /Transparency/);
        assert.match(text, /4200 Wh at\s+VAN\*M1/);
        assert.match(text, /4200 Wh at the same reading/);
        assert.match(text, /0 Wh \(0 %\), over 1 reading\(s\)/);
    });

    it('says a signature is there and not that it holds', () => {
        assert.match(drawn({ ...completed, transparency: seen }), /signed, not verified/);
        assert.match(drawn({ ...completed, transparency: { ...seen, readings: [ { ...reading, signed: false } ] } }), /unsigned/);
    });

    it('says how far the station\'s clock is off, and when that is too far', () => {
        const ahead = { ...clock, firstOffset_ms: 90200, offset_ms: 90200, agrees: false };
        const text  = drawn({ ...completed, transparency: { ...seen, stationClock: ahead } });
        assert.match(text, /90\.2 s ahead/);
        assert.match(text, /more than the 5 s this vehicle tolerates/);
        assert.doesNotMatch(drawn({ ...completed, transparency: seen }), /tolerates/);
    });

    it('says a -2 session shows no station clock, rather than leaving the line out', () => {
        assert.match(drawn({ ...completed, transparency: { ...seen, stationClock: null } }), /ISO 15118-2 has no header timestamp/);
    });

    it('says there is nothing to compare where the station reported no reading', () => {
        const text = drawn({ ...completed, transparency: { ...seen, meterClock: null, readings: [] } });
        assert.match(text, /no reading reported/);
        assert.match(text, /nothing to put beside the vehicle's own count/);
    });

    it('draws no such section for a run that has none', () => {
        assert.doesNotMatch(drawn(completed), /Transparency/);
    });

});
