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

    const clock    = { samples: 12, unreadable: 0, firstOffset_ms: 300, offset_ms: 300, uncertainty_ms: 600, drift_ms: 0, drifted: false, agrees: true };

    const reading  = { sample: 3, meterId: 'VAN*M1', stationWh: 4200, vehicleWh: 4200, differenceWh: 0, signed: true, meterTime: '2026-11-27T12:00:00.0000000+00:00' };

    const seen     = { clockTolerance_ms: 5000, stationClock: clock, meterClock: clock, vehicleWh: 4200, readings: [ reading ] };

    it('puts the station\'s reading beside the vehicle\'s own count, and their difference', () => {
        const text = drawn({ ...completed, transparency: seen });
        assert.match(text, /Transparency/);
        assert.match(text, /4200 Wh at\s+VAN\*M1/);
        assert.match(text, /4200 Wh at the same reading/);
        assert.match(text, /0 Wh \(0 %\), over 1 reading\(s\)/);
    });

    // A bidirectional session: the station keeps two rising registers, the vehicle one count that goes
    // down when it gives energy back. The difference is taken on the net figure, so a reading that showed
    // only the charged register would say "0 Wh" beside a vehicle at -2001 Wh and a difference of 0.
    it('shows both registers of a bidirectional reading, and the net figure the difference is taken on', () => {
        const back = { ...reading, stationWh: 0, dischargedWh: 2001, stationNetWh: -2001, vehicleWh: -2001, differenceWh: 0 };
        const text = drawn({ ...completed, transparency: { ...seen, vehicleWh: -2001, readings: [ back ] } });
        assert.match(text, /0 Wh charged, 2001 Wh given back, -2001 Wh net at\s+VAN\*M1/);
        assert.match(text, /-2001 Wh at the same reading/);
        assert.doesNotMatch(drawn({ ...completed, transparency: seen }), /given back/);
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

    it('says a clock moved only where that is more than a second ticking over', () => {
        const ticked = { ...clock, drift_ms: 500, drifted: false };
        const moved  = { ...clock, drift_ms: 4000, drifted: true };
        assert.doesNotMatch(drawn({ ...completed, transparency: { ...seen, meterClock:   ticked } }), /moved/);
        assert.match       (drawn({ ...completed, transparency: { ...seen, stationClock: moved  } }), /moved 4\.0 s ahead over 12 timestamps/);
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


describe('what a session result says of the -20 metering confirmations', () => {

    const clock    = { samples: 12, unreadable: 0, firstOffset_ms: 300, offset_ms: 300, uncertainty_ms: 600, drift_ms: 0, drifted: false, agrees: true };

    const reading  = { sample: 3, meterId: 'VAN*M1', stationWh: 2400, vehicleWh: 2400, differenceWh: 0, signed: true, meterTime: null };

    const sent     = { meterId: 'VAN*M1', stationWh: 1600, vehicleWh: 1600, differenceWh: 0, withReceipt: false, response: 'OK' };

    const seen     = { clockTolerance_ms: 5000, stationClock: clock, meterClock: clock, vehicleWh: 2400, readings: [ reading ] };

    it('says how many the vehicle signed, what the station answered, and the last beside the vehicle\'s count', () => {
        const text = drawn({ ...completed, transparency: { ...seen, meteringConfirmations: { impossible: 0, sent: [ sent, { ...sent, stationWh: 2400, vehicleWh: 2400 } ] } } });
        assert.match(text, /Metering confirmations\s*2 signed with the contract key; the station answered OK to all/);
        assert.match(text, /Last confirmed\s*2400 Wh at\s+VAN\*M1;\s+this vehicle 2400 Wh, difference 0 Wh/);
    });

    it('says a confirmation acknowledges receipt, and that the vehicle\'s count is not in it', () => {
        const text = drawn({ ...completed, transparency: { ...seen, meteringConfirmations: { impossible: 0, sent: [ sent ] } } });
        assert.match(text, /received the reading\. It does not say the reading is right/);
        assert.match(text, /answered OK to it/);
    });

    it('names a signed cost receipt, and each answer where they differ', () => {
        const text = drawn({ ...completed, transparency: { ...seen, meteringConfirmations: { impossible: 0, sent: [ sent, { ...sent, withReceipt: true, response: 'WARNING_General' } ] } } });
        assert.match(text, /OK 1x, WARNING_General 1x/);
        assert.match(text, /with the cost receipt/);
    });

    it('says the station asked for none, and draws no hint about what a confirmation means', () => {
        const text = drawn({ ...completed, transparency: { ...seen, meteringConfirmations: { impossible: 0, sent: [] } } });
        assert.match(text, /the station asked for none/);
        assert.doesNotMatch(text, /received the reading/);
    });

    it('says how often it was asked for one it could not sign', () => {
        const text = drawn({ ...completed, transparency: { ...seen, meteringConfirmations: { impossible: 3, sent: [] } } });
        assert.match(text, /none signed/);
        assert.match(text, /Could not sign\s*3 request\(s\) - no contract key in this session/);
    });

    it('draws no such line over -2, which counts its metering receipts instead', () => {
        assert.doesNotMatch(drawn({ ...completed, transparency: { ...seen, meteringConfirmations: null } }), /Metering confirmations/);
        assert.doesNotMatch(drawn({ ...completed, transparency: seen }),                                    /Metering confirmations/);
    });

});
