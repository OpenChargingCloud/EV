/**
 * The Charging page - "/" - drawn, in a document of happy-dom, against a
 * stand-in vehicle: it plugs in as chosen before it looks, and not again where
 * it is plugged in that way; it offers every station found, each secured as
 * the station offers, and charges at the one chosen; a plugging in refused
 * says why and looks for nothing; and what is typed into what a discovery asks
 * for - and its focus - outlives a discovery being drawn, a save says what the
 * vehicle took, and a refusal keeps what is typed and says why.
 */

import { asked, change, field, open, refused, submit, until, type Asked } from '../../test/vehicle.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { DiscoveryResult, Link, LinkRequest, SessionConfiguration, SessionStart,
              V2GConfiguration, V2GUpdate } from '../api/client.ts';

const { chargingPage } = await import('./charging.ts');


let held: V2GConfiguration;
let charging: SessionConfiguration;

/** Refuses a change of the settings where told to. */
let refuseSettings = false;

/** What plugging in answers, where it is not plugged in. */
let plugging: 'pluggedIn' | 'notConfigured' = 'pluggedIn';

/** What a form says once its save went through - and not before: whileSaving() says "Saving ..." meanwhile. */
const saved = 'Saved, and in effect for the next discovery.';

/** Two stations on the link: one offering TLS, one not. */
const twoStations: DiscoveryResult = {
    outcome:    'found',
    attempts:   1,
    elapsed_ms: 12,
    interface:  'eth0',
    secc:       { address: 'fe80::a', port: 15118, security: 'tls',   transport: 'TCP', version: '1', from: 'fe80::a' },
    others:     [ { address: 'fe80::b', port: 15119, security: 'noTls', transport: 'TCP', version: '1', from: 'fe80::b' } ]
};

const pairedLink: Link = {
    outcome:  'pluggedIn',
    via:      'slac',
    since:    '2026-10-09T10:00:00Z',
    slac:     { outcome: 'paired', peer: '127.0.0.1:5000', nid: '0102030405060A',
                station: { mac: '02:00:00:00:00:AA', attenuation_dB: 12.5 },
                candidates: [ { mac: '02:00:00:00:00:AA', attenuation_dB: 12.5 }, { mac: '02:00:00:00:00:BB', attenuation_dB: 31 } ] }
};

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

    if (path === '/link' && method === 'POST') {

        const how = body as LinkRequest;

        if (plugging === 'notConfigured')
            return { ...held, result: { outcome: 'notConfigured', error: 'There is no SLAC peer to pair with.' } };

        held = { ...held, link: how.via === 'slac' ? pairedLink : { outcome: 'pluggedIn', via: how.via, since: '2026-10-09T10:00:00Z' } };

        return { ...held, result: held.link };

    }

    if (path === '/link' && method === 'DELETE') {
        held = { ...held, link: null };
        return held;
    }

    if (path === '/configuration/v2g/discover') {
        held = { ...held, lastDiscovery: twoStations };
        return { ...held, result: twoStations };
    }

    if (path === '/configuration/v2g')
        return held;

    if (path === '/session' && method === 'POST') {
        charging = { ...charging, running: true };
        return { outcome: 'started' };
    }

    if (path === '/session')
        return charging;

    return undefined;

}

async function opened(link: Link | null = null): Promise<HTMLElement> {
    held = {
        interface:      'eth0',
        interfaces:     [ { name: 'eth0', index: 2, linkLocal: 'fe80::1%2', mac: '02:00:00:00:00:01' } ],
        settings:       { requestedSecurity: 'tls', perAttemptTimeoutSeconds: 0.25, maxRetries: 10, totalDeadlineSeconds: 5,
                          rejectNoTLSResponses: false, requireLinkLocalSECCAddress: true, multicastLoopback: false },
        lastDiscovery:  null,
        link,
        file:           'wwcp.json'
    };
    charging = {
        connect:   null,
        settings:  { protocol: 'both', mode: 'dc', tls: 'none', renegotiate: false, slacPeer: null,
                     t1sTransport: 'none', t1sBus: null, t1sInterface: null, t1sWeight: 3 },
        running:   false,
        paused:    null,
        lastSession: null
    } as unknown as SessionConfiguration;
    refuseSettings = false;
    plugging       = 'pluggedIn';
    return open(chargingPage, '/', [ 'v2g:read', 'v2g:edit', 'v2g:run', 'session:read', 'session:run' ],
                vehicle, root => root.querySelector('#settings-form') !== null);
}

const looked = (root: HTMLElement) => root.querySelector('#discovery') !== null &&
                                      !root.querySelector<HTMLButtonElement>('#look')!.disabled;


describe('the Charging page', () => {

    it('plugs in over SLAC before it looks, and shows the station at the end of the cable', async () => {

        const root = await opened();

        change(root.querySelector<HTMLInputElement>('#plug-form input[name="via"][value="slac"]')!, true);
        field(root, '#plug-form', 'slacPeer').value = '127.0.0.1:5000';

        submit(root, '#plug-form');
        await until(() => looked(root), 'nothing was looked for');

        const plugIn = asked.find(one => one.path === '/link' && one.method === 'POST');

        assert.deepEqual(plugIn?.body, { via: 'slac', slacPeer: '127.0.0.1:5000' });
        assert.ok(asked.indexOf(plugIn!) < asked.findIndex(one => one.path === '/configuration/v2g/discover'), 'looked before it plugged in');

        const link = root.querySelector('#link')!.textContent!.replace(/\s+/g, ' ');

        assert.match(link, /over SLAC/);
        assert.match(link, /The station at the cable\s*02:00:00:00:00:AA 12\.5 dB/);
        assert.match(link, /Heard more quietly\s*02:00:00:00:00:BB 31 dB/);
        assert.ok(root.querySelector('#unplug') !== null, 'there is nothing to unplug with');

    });

    it('looks again over the link it plugged in, and does not plug in anew', async () => {

        const root = await opened();

        change(root.querySelector<HTMLInputElement>('#plug-form input[name="via"][value="slac"]')!, true);
        field(root, '#plug-form', 'slacPeer').value = '127.0.0.1:5000';

        submit(root, '#plug-form');
        await until(() => looked(root), 'nothing was looked for');

        // The form was reset once it was used: the way it plugged in is still
        // the one chosen - it was none, and the second search plugged in
        // directly and let go of the pairing.
        assert.equal(root.querySelector<HTMLInputElement>('#plug-form input[name="via"]:checked')?.value, 'slac');

        const before = asked.length;

        submit(root, '#plug-form');
        await until(() => asked.slice(before).some(one => one.path === '/configuration/v2g/discover') && looked(root),
                    'nothing was looked for the second time');

        assert.equal(asked.slice(before).filter(one => one.path === '/link').length, 0, 'it plugged in anew');

    });

    it('does not plug in again where it is plugged in that way, and unplugs', async () => {

        const root = await opened(pairedLink);

        assert.equal(root.querySelector<HTMLInputElement>('#plug-form input[name="via"][value="slac"]')!.checked, true,
                     'the medium chosen is not the link held');

        submit(root, '#plug-form');
        await until(() => looked(root), 'nothing was looked for');

        assert.equal(asked.filter(one => one.path === '/link').length, 0, 'it plugged in again');

        root.querySelector<HTMLButtonElement>('#unplug')!.click();
        await until(() => root.querySelector('#link') === null, 'it did not unplug');

        assert.ok(asked.some(one => one.path === '/link' && one.method === 'DELETE'));

    });

    it('offers every station found, each secured as it offers, and charges at the one chosen', async () => {

        const root = await opened();

        submit(root, '#plug-form');
        await until(() => looked(root), 'nothing was looked for');

        const stations = root.querySelectorAll<HTMLFormElement>('form.station');

        assert.equal(stations.length, 2);
        assert.equal(field<HTMLSelectElement>(root, 'form.station[data-station="0"]', 'tls').value, 'dotnet', 'a station offering TLS is met with TLS');
        assert.equal(field<HTMLSelectElement>(root, 'form.station[data-station="1"]', 'tls').value, 'none',   'a station offering none is met without');

        submit(root, 'form.station[data-station="1"]');
        await until(() => root.querySelector('#stop') !== null, 'the session is not shown as running');

        assert.deepEqual(asked.find(one => one.path === '/session' && one.method === 'POST')?.body as SessionStart,
                         { station: 1, tls: 'none' });

    });

    it('says why it is not plugged in, and looks for nothing', async () => {

        const root = await opened();

        plugging = 'notConfigured';

        change(root.querySelector<HTMLInputElement>('#plug-form input[name="via"][value="slac"]')!, true);

        submit(root, '#plug-form');
        await until(() => root.querySelector('#plug-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#plug-error')!.textContent, 'There is no SLAC peer to pair with.');
        assert.equal(asked.filter(one => one.path === '/configuration/v2g/discover').length, 0, 'it looked all the same');
        assert.equal(root.querySelector('#link'), null);

    });

    it('keeps what is typed into the settings, and its focus, while a discovery is drawn', async () => {

        const root     = await opened();
        const deadline = field(root, '#settings-form', 'totalDeadlineSeconds');

        deadline.value = '7.5';
        deadline.focus();

        submit(root, '#plug-form');
        await until(() => looked(root), 'the discovery was not drawn');

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

});
