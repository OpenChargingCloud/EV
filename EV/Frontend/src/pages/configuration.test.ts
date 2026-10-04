/**
 * The configuration drawn, in a document of happy-dom, against a stand-in
 * vehicle: its cards - view.ts's, cardView() - stand as the markup they are,
 * and Reload draws them again with what the vehicle says then.
 */

import { open, until, type Asked } from '../../test/vehicle.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { configurationPage } = await import('./configuration.ts');


let uptime = '1 minute';

function vehicle({ path }: Asked): unknown {

    if (path === '/status')
        return { service: 'EV', version: '0.1.0', hermod: null, timestamp: '2026-10-04T12:00:00Z',
                 startedAt: '2026-10-04T11:59:00Z', uptime, sessions: 1, log: { entries: 0, capacity: 100, lastId: 0, tags: [] } };

    if (path === '/configuration')
        return { vehicle: { name: 'EV' }, battery: { capacityKWh: 60 }, v2g: { interface: null },
                 http: { port: 2347 }, web: {}, log: {}, time: {},
                 assemblies: [ { name: 'EV', version: '0.1.0', commit: 'abc1234' } ] };

    return undefined;

}


describe('the configuration', () => {

    it('draws its cards as markup, and again on Reload', async () => {

        uptime = '1 minute';

        const root = await open(configurationPage, '/configuration', [ 'configuration:read' ],
                                vehicle, root => root.querySelector('.cards') !== null);

        assert.equal(root.querySelectorAll('.cards > section.card').length, 8);
        assert.match(root.querySelector('.cards')!.textContent!, /ISO 15118/);
        assert.match(root.querySelector('.cards')!.textContent!, /Uptime\s+1 minute/);
        assert.doesNotMatch(root.textContent!, /<section/, 'a card was taken as text');

        uptime = '2 minutes';
        root.querySelector<HTMLButtonElement>('#reload')!.click();

        await until(() => /Uptime\s+2 minutes/.test(root.textContent!), 'Reload did not draw what the vehicle says then');

        assert.equal(root.querySelectorAll('.cards > section.card').length, 8);

    });

});
