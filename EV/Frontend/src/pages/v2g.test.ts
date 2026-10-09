/**
 * The ISO 15118 page drawn, in a document of happy-dom, against a stand-in
 * vehicle: the interface saved says what the vehicle took, the interfaces
 * offered keep their option by name, and a pairing says which station is at
 * the end of the cable. Looking for a station, and what a discovery asks for,
 * are the Stations page's - see stations.test.ts.
 */

import { asked, field, open, submit, until, type Asked } from '../../test/vehicle.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { SlacResult, V2GConfiguration, V2GInterface, V2GUpdate } from '../api/client.ts';

const { v2gPage } = await import('./v2g.ts');


let held: V2GConfiguration;

/** What a form says once its save went through - and not before: whileSaving() says "Saving ..." meanwhile. */
const saved = 'Saved, and in effect for the next discovery.';

const eth0: V2GInterface = { name: 'eth0', index: 2, linkLocal: 'fe80::1%2', mac: '02:00:00:00:00:01' };
const plc0: V2GInterface = { name: 'plc0', index: 3, linkLocal: 'fe80::2%3', mac: '02:00:00:00:00:02' };

const paired: SlacResult = { outcome: 'paired', peer: '127.0.0.1:5000', nid: '0102030405060A', elapsed_ms: 1900,
                             station: { mac: '02:00:00:00:00:AA', attenuation_dB: 12.5 } };

function vehicle({ method, path, body }: Asked): unknown {

    if (path === '/configuration/v2g' && method === 'PUT') {

        const { interface: chosen } = body as V2GUpdate;

        held = { ...held, interface: chosen === undefined ? held.interface : chosen };

        return held;

    }

    if (path === '/configuration/v2g/pair')
        return paired;

    if (path === '/configuration/v2g')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = {
        interface:      'eth0',
        interfaces:     [ eth0 ],
        settings:       { requestedSecurity: 'tls', perAttemptTimeoutSeconds: 0.25, maxRetries: 10, totalDeadlineSeconds: 5,
                          rejectNoTLSResponses: false, requireLinkLocalSECCAddress: true, multicastLoopback: false },
        lastDiscovery:  null,
        link:           null,
        file:           'wwcp.json'
    };
    return open(v2gPage, '/configuration/v2g', [ 'v2g:read', 'v2g:edit', 'v2g:run' ],
                vehicle, root => root.querySelector('#interface-form') !== null);
}


describe('the ISO 15118 page', () => {

    it('saves "the first one that could carry it" as no interface, and says so', async () => {

        const root = await opened();

        field<HTMLSelectElement>(root, '#interface-form', 'interface').value = '';

        submit(root, '#interface-form');
        await until(() => held.interface === null && root.querySelector('#interface-note')?.textContent === saved,
                    'the interface was not saved');

        assert.equal((asked.find(one => one.method === 'PUT')?.body as V2GUpdate).interface, null);

    });

    it('keeps an interface offered as the option it is, when another one is offered before it', async () => {

        const root   = await opened();
        const option = root.querySelector<HTMLOptionElement>('#interface-form option[value="eth0"]')!;

        held = { ...held, interfaces: [ plc0, eth0 ] };

        root.querySelector<HTMLButtonElement>('#reload')!.click();
        await until(() => root.querySelector('#interface-form option[value="plc0"]') !== null, 'the new interface was not offered');

        assert.ok(root.querySelector('#interface-form option[value="eth0"]') === option, 'the option of eth0 was made anew');
        const card = root.querySelector('#interface-form')!.closest('section')!;

        assert.equal(card.querySelectorAll('.kv-list .kv .k')[0]!.textContent, 'plc0', 'the interfaces are not listed as offered');

    });

    it('says which station a pairing found at the end of the cable', async () => {

        const root = await opened();

        root.querySelector<HTMLButtonElement>('#pair')!.click();
        await until(() => /paired/.test(root.textContent!) && !root.querySelector<HTMLButtonElement>('#pair')!.disabled,
                    'the pairing was not drawn');

        assert.match(root.textContent!.replace(/\s+/g, ' '), /The station at the cable\s*02:00:00:00:00:AA 12\.5 dB/);
        assert.equal(root.querySelector('#discover'), null, 'looking for a station is the Stations page\'s');

    });

});
