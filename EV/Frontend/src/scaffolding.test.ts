/**
 * The place for this kind's own frontend tests, held until the first of them.
 *
 * Run with `npm test`. What every kind of node shares is tested by WWCP_Node;
 * this kind's own pages have no test yet. Without one test file, `npm run
 * typecheck:test` stops with TS18003 - and taking "test", "typecheck:test",
 * tsconfig.test.json and the CI step out would only mean putting them back by
 * hand with the first test, the CI step the one most easily forgotten, and a
 * test that never runs in CI the result.
 *
 * So this file holds the place, and asks what that first test will need: that
 * "@node/..." is found from here, by the type check and by Node. It goes when
 * that test comes.
 */

import { strict as assert } from 'node:assert';
import { describe, it }     from 'node:test';

import { html }             from '@node/html.ts';


describe('the tests of this kind of node', () => {

    it('find what every kind of node shares, as its pages do', () => {
        assert.equal(typeof html, 'function');
    });

});
