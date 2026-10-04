/**
 * The ISO 15118 page drawn, in a document of happy-dom, against a stand-in
 * vehicle: what is typed into one of its two forms - and its focus - outlives
 * the other being saved and a discovery being drawn, a form saved says what
 * the vehicle took, one refused keeps what is typed and says why, and the
 * interfaces offered keep their option by name.
 */

import { asked, field, open, refused, submit, until, type Asked } from '../../test/vehicle.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { V2GConfiguration, V2GInterface, V2GUpdate } from '../api/client.ts';

const { v2gPage } = await import('./v2g.ts');


let held: V2GConfiguration;

/** Refuses a change of the settings where told to. */
let refuseSettings = false;

/** What a form says once its save went through - and not before: whileSaving() says "Saving ..." meanwhile. */
const saved = 'Saved, and in effect for the next discovery.';

const eth0: V2GInterface = { name: 'eth0', index: 2, linkLocal: 'fe80::1%2', mac: '02:00:00:00:00:01' };
const plc0: V2GInterface = { name: 'plc0', index: 3, linkLocal: 'fe80::2%3', mac: '02:00:00:00:00:02' };

function vehicle({ method, path, body }: Asked): unknown {

    if (path === '/configuration/v2g' && method === 'PUT') {

        const update = body as V2GUpdate;

        if (refuseSettings && update.maxRetries !== undefined)
            return refused(400, "'v2g.maxRetries' must be between 1 and 1000.");

        const { interface: chosen, ...settings } = update;

        // A vehicle takes what it takes: no more than a hundred attempts.
        held = {
            ...held,
            interface:  chosen === undefined ? held.interface : chosen,
            settings:   { ...held.settings, ...settings,
                          maxRetries: Math.min(settings.maxRetries ?? held.settings.maxRetries, 100) }
        };

        return held;

    }

    if (path === '/configuration/v2g/discover')
        return { ...held, result: { outcome: 'timeout', attempts: 3, elapsed_ms: 750, interface: 'eth0' } };

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
        file:           'wwcp.json'
    };
    refuseSettings = false;
    return open(v2gPage, '/configuration/v2g', [ 'v2g:read', 'v2g:edit', 'v2g:run' ],
                vehicle, root => root.querySelector('#settings-form') !== null);
}


describe('the ISO 15118 page', () => {

    it('keeps what is typed into the settings, and its focus, while the interface is saved', async () => {

        const root     = await opened();
        const browser  = chromeTakesTheFocus(root);
        const retries  = field(root, '#settings-form', 'maxRetries');

        retries.value = '42';
        retries.focus();

        field<HTMLSelectElement>(root, '#interface-form', 'interface').value = '';

        submit(root, '#interface-form');
        await until(() => held.interface === null && root.querySelector('#interface-note')?.textContent === saved,
                    'the interface was not saved');

        browser.disconnect();

        assert.ok(field(root, '#settings-form', 'maxRetries') === retries, 'the field was made anew');
        assert.equal(retries.value, '42');
        assert.ok(document.activeElement === retries, 'the focus went');

    });

    it('keeps what is typed into the settings, and its focus, while a discovery is drawn', async () => {

        const root     = await opened();
        const deadline = field(root, '#settings-form', 'totalDeadlineSeconds');

        deadline.value = '7.5';
        deadline.focus();

        root.querySelector<HTMLButtonElement>('#discover')!.click();
        await until(() => /nothing answered/.test(root.textContent!) && !root.querySelector<HTMLButtonElement>('#discover')!.disabled,
                    'the discovery was not drawn');

        assert.ok(field(root, '#settings-form', 'totalDeadlineSeconds') === deadline, 'the field was made anew');
        assert.equal(deadline.value, '7.5');
        assert.ok(document.activeElement === deadline, 'the focus went');

    });

    it('shows the settings as the vehicle took them once they are saved, with nothing left to save', async () => {

        const root     = await opened();
        const retries  = field(root, '#settings-form', 'maxRetries');
        const loopback = field(root, '#settings-form', 'multicastLoopback');

        retries.value    = '500';
        loopback.checked = true;

        submit(root, '#settings-form');
        await until(() => held.settings.maxRetries === 100 && root.querySelector('#settings-note')?.textContent === saved,
                    'the settings were not saved');

        assert.equal((asked.find(one => one.method === 'PUT')?.body as V2GUpdate).maxRetries, 500);
        assert.equal(field(root, '#settings-form', 'maxRetries').value,         '100', 'the field says what was typed, not what the vehicle took');
        assert.equal(field(root, '#settings-form', 'maxRetries').defaultValue,  '100');
        assert.equal(field(root, '#settings-form', 'multicastLoopback').checked,         true);
        assert.equal(field(root, '#settings-form', 'multicastLoopback').defaultChecked,  true);

    });

    it('keeps what is typed into settings the vehicle refused, and says why', async () => {

        const root    = await opened();
        const retries = field(root, '#settings-form', 'maxRetries');

        refuseSettings = true;
        retries.value  = '0';

        submit(root, '#settings-form');
        await until(() => root.querySelector('#settings-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#settings-error')!.textContent, "'v2g.maxRetries' must be between 1 and 1000.");
        assert.ok(field(root, '#settings-form', 'maxRetries') === retries, 'the field was made anew');
        assert.equal(retries.value, '0', 'what was typed went');

    });

    it('keeps an interface offered as the option it is, when another one is offered before it', async () => {

        const root   = await opened();
        const option = root.querySelector<HTMLOptionElement>('#interface-form option[value="eth0"]')!;

        held = { ...held, interfaces: [ plc0, eth0 ] };

        root.querySelector<HTMLButtonElement>('#discover')!.click();
        await until(() => root.querySelector('#interface-form option[value="plc0"]') !== null, 'the new interface was not offered');

        assert.ok(root.querySelector('#interface-form option[value="eth0"]') === option, 'the option of eth0 was made anew');
        const card = root.querySelector('#interface-form')!.closest('section')!;

        assert.equal(card.querySelectorAll('.kv-list .kv .k')[0]!.textContent, 'plc0', 'the interfaces are not listed as offered');

    });

});
