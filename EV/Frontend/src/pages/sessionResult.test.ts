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
