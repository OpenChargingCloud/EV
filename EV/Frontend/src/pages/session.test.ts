/**
 * The Charging page drawn, in a document of happy-dom, against a stand-in
 * vehicle: what is typed into one of its forms - and its focus - outlives
 * another being saved and the page being drawn while a session runs, a form
 * saved says what the vehicle took, one refused keeps what is typed and says
 * why, and a certificate chosen keeps its option.
 */

import { asked, field, open, refused, submit, until, type Asked } from '../../test/vehicle.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { SessionConfiguration, SessionUpdate } from '../api/client.ts';

const { sessionPage } = await import('./session.ts');


let held: SessionConfiguration;

/** Refuses a change of the goals where told to. */
let refuseGoals = false;

/** What a form says once its save went through - and not before: whileSaving() says "Saving ..." meanwhile. */
const saved = 'Saved, and in effect for the next session.';

/** Two vehicle certificates in the store, to choose from. */
const certificate = (id: string, label: string) => ({
    id, label, keyAlgorithm: 'ECDSA P-256', notAfter: '2030-01-01T00:00:00Z',
    expired: false, notYetValid: false, active: true
});

const store = { certificates: { vehicle: [ certificate('a1', 'Vehicle A'), certificate('b2', 'Vehicle B') ] } };

function vehicle({ method, path, body }: Asked): unknown {

    if (path === '/configuration/session' && method === 'PUT') {

        const update = body as SessionUpdate;

        if (refuseGoals && update.targetEnergyKWh !== undefined)
            return refused(400, "'session.targetEnergyKWh' must be at most 1000.");

        const chosen = (id: string | null | undefined, was: SessionConfiguration['certificates']['vehicleCertificate']) =>
            id === undefined ? was
          : id === null      ? null
          :                    { id, label: id === 'a1' ? 'Vehicle A' : 'Vehicle B', missing: false } as never;

        // A vehicle takes what it takes: a charge of no more than 100 kWh.
        held = {
            ...held,
            connect:       update.connect === undefined ? held.connect : update.connect,
            settings:      { ...held.settings, protocol: update.protocol ?? held.settings.protocol },
            goals:         update.targetEnergyKWh === undefined ? held.goals : {
                               ...held.goals,
                               targetEnergyKWh:  update.targetEnergyKWh === null ? null : Math.min(update.targetEnergyKWh, 100)
                           },
            certificates:  { ...held.certificates,
                             vehicleCertificate: chosen(update.vehicleCertificate, held.certificates.vehicleCertificate) }
        };

        return held;

    }

    if (path === '/session' && method === 'POST') {
        held = { ...held, running: true };
        return { outcome: 'started' };
    }

    if (path === '/session')
        return held;

    if (path === '/certificates')
        return store;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = {
        connect:       null,
        settings:      { protocol: 'both', mode: 'dc', tls: 'none', renegotiate: false, slacPeer: null,
                         t1sTransport: 'none', t1sBus: null, t1sInterface: null, t1sWeight: 1 },
        certificates:  { pkiDirectory: null, vehicleCertificate: null, contractCertificate: null, oemCertificate: null,
                         tariffCertificate: null, trustAnchors: { v2gRoot: 0, moRoot: 0, oemRoot: 0 } },
        goals:         { targetEnergyKWh: null, maxChargingTimeSeconds: null, departureInSeconds: null,
                         minimumStateOfChargePercent: null },
        running:       false,
        paused:        null,
        lastSession:   null,
        file:          'wwcp.json'
    };
    refuseGoals = false;
    return open(sessionPage, '/configuration/session',
                [ 'session:read', 'session:edit', 'session:run', 'v2g:edit', 'certificates:read', 'certificates:edit' ],
                vehicle, root => root.querySelector('#certificates-form') !== null);
}


describe('the Charging page', () => {

    it('keeps what is typed into the goals, and its focus, while where to is saved', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const energy  = field(root, '#goals-form', 'targetEnergyKWh');

        energy.value = '12.5';
        energy.focus();

        field(root, '#link-form', 'connect').value = '[::1]:15118';

        submit(root, '#link-form');
        await until(() => held.connect === '[::1]:15118' && root.querySelector('#link-note')?.textContent === saved,
                    'where to was not saved');

        browser.disconnect();

        assert.ok(field(root, '#goals-form', 'targetEnergyKWh') === energy, 'the field was made anew');
        assert.equal(energy.value, '12.5');
        assert.ok(document.activeElement === energy, 'the focus went');

    });

    it('keeps what is typed into the goals, and its focus, while a session is started and drawn', async () => {

        const root    = await opened();
        const minutes = field(root, '#goals-form', 'maxChargingTimeMinutes');

        minutes.value = '30';
        minutes.focus();

        root.querySelector<HTMLButtonElement>('#charge')!.click();
        await until(() => root.querySelector<HTMLButtonElement>('#stop')?.disabled === false, 'the running session was not drawn');

        assert.ok(field(root, '#goals-form', 'maxChargingTimeMinutes') === minutes, 'the field was made anew');
        assert.equal(minutes.value, '30');
        assert.ok(document.activeElement === minutes, 'the focus went');

    });

    it('shows the goals as the vehicle took them once they are saved, with nothing left to save', async () => {

        const root   = await opened();
        const energy = field(root, '#goals-form', 'targetEnergyKWh');

        energy.value = '250';

        submit(root, '#goals-form');
        await until(() => held.goals.targetEnergyKWh === 100 && root.querySelector('#goals-note')?.textContent === saved,
                    'the goals were not saved');

        assert.equal((asked.find(one => one.method === 'PUT')?.body as SessionUpdate).targetEnergyKWh, 250);
        assert.equal(field(root, '#goals-form', 'targetEnergyKWh').value,         '100', 'the field says what was typed, not what the vehicle took');
        assert.equal(field(root, '#goals-form', 'targetEnergyKWh').defaultValue,  '100');

    });

    it('keeps what is typed into goals the vehicle refused, and says why', async () => {

        const root   = await opened();
        const energy = field(root, '#goals-form', 'targetEnergyKWh');

        refuseGoals  = true;
        energy.value = '5000';

        submit(root, '#goals-form');
        await until(() => root.querySelector('#goals-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#goals-error')!.textContent, "'session.targetEnergyKWh' must be at most 1000.");
        assert.ok(field(root, '#goals-form', 'targetEnergyKWh') === energy, 'the field was made anew');
        assert.equal(energy.value, '5000', 'what was typed went');

    });

    it('takes a target energy as a driver types it: 12.5 kWh is a step of the field', async () => {

        const root   = await opened();
        const energy = field(root, '#goals-form', 'targetEnergyKWh');

        // A browser counts the steps from min: with min 0.001 and a step of
        // 0.1, 12.5 was refused - only 12.401 or 12.501 would do. Worked out
        // here, since happy-dom does not check a step as a browser does.
        const steps = (12.5 - Number(energy.min)) / Number(energy.step);

        assert.ok(Math.abs(steps - Math.round(steps)) < 1e-6, `12.5 is no step of min ${energy.min} and step ${energy.step}`);

    });

    it('says the certificate chosen in the option it was offered as', async () => {

        const root    = await opened();
        const chooser = field<HTMLSelectElement>(root, '#certificates-form', 'vehicleCertificate');
        const optionB = chooser.querySelector<HTMLOptionElement>('option[value="b2"]')!;

        chooser.value = 'b2';

        submit(root, '#certificates-form');
        await until(() => held.certificates.vehicleCertificate !== null && root.querySelector('#certificates-note')?.textContent === saved,
                    'the certificate was not saved');

        assert.ok(chooser.querySelector('option[value="b2"]') === optionB, 'the option was made anew');
        assert.equal(chooser.value, 'b2');
        // The attribute, which a browser's defaultSelected is: happy-dom has no defaultSelected.
        assert.ok(optionB.hasAttribute('selected'), 'the option chosen is not the one drawn as chosen');

    });

});
