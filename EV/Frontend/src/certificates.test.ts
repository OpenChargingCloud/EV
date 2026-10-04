/**
 * Every node's certificates page drawn with what a vehicle says on it, in a
 * document of happy-dom, against a stand-in vehicle: its words under what it
 * believes and what it presents, the Charging page they point to, what an
 * unencrypted key lets somebody take, and a certificate a session takes
 * marked as chosen - each a template of view.ts, as the page draws them.
 */

import { open } from '../test/vehicle.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { Certificate, CertificateStore } from './api/client.ts';

const { certificatesPage }     = await import('@node/pages/certificates.ts');
const { vehicleCertificates }  = await import('./certificates.ts');


const vehicleA = {
    id:            'aaaaaaaaaaaaaaaa',
    kind:          'vehicleCertificate',
    label:         'Vehicle A',
    subject:       'CN=Vehicle A',
    thumbprint:    'aa'.repeat(32),
    keyAlgorithm:  'ECDSA P-256',
    hasPrivateKey: true,
    chainLength:   1,
    notAfter:      '2030-01-01T00:00:00Z',
    active:        true,
    expired:       false,
    notYetValid:   false
} as unknown as Certificate;

const store = {
    directory:           'certificates',
    trustAnchors:        [ 'v2gRoot' ],
    credentials:         [ 'vehicleCertificate' ],
    recognised:          [],
    kinds:               { v2gRoot:             { description: 'V2G root',            usages: [] },
                           vehicleCertificate:  { description: 'Vehicle certificate', usages: [] } },
    usages:              [],
    certificates:        { v2gRoot: [], vehicleCertificate: [ vehicleA ] },
    keysAreUnencrypted:  true,
    chosen:              { vehicleCertificate: vehicleA.id, contractCertificate: null }
} as unknown as CertificateStore;

const opened = () => open(certificatesPage(vehicleCertificates), '/configuration/certificates', [ 'certificates:read' ],
                          ({ path }) => path === '/certificates' ? store : undefined,
                          root => [ ...root.querySelectorAll('h2') ].some(one => /believes/.test(one.textContent ?? '')));

/** The hint right below a group's heading: "What this electric vehicle believes", say. */
function hintBelow(root: HTMLElement, heading: RegExp): string {
    const found = [ ...root.querySelectorAll('h2') ].find(one => heading.test(one.textContent ?? ''));
    assert.ok(found, `there is no heading ${heading}`);
    const hint = found.nextElementSibling;
    assert.ok(hint !== null && hint.classList.contains('hint'), `there is no hint below ${heading}`);
    return hint.textContent!.replace(/\s+/g, ' ').trim();
}


describe('what a vehicle says on the certificates page', () => {

    it('is said in templates of view.ts, as the page draws', () => {

        for (const [ name, hint ] of Object.entries(vehicleCertificates.hints!))
            assert.ok(typeof hint === 'object' && '_$litType$' in hint, `the hint ${name} is no template of view.ts`);

    });

    it('says under what it believes that none of its roots is chosen for a session', async () => {

        const root = await opened();

        assert.match(hintBelow(root, /believes/), /Every switched-on root of a kind is believed at once, and none of them is chosen for a session\./);

    });

    it('says under what it presents that a session\'s certificates are chosen on the Charging page, and links to it', async () => {

        const root = await opened();
        const link = [ ...root.querySelectorAll<HTMLAnchorElement>('a') ].find(one => one.textContent === 'Charging');

        assert.match(hintBelow(root, /presents/), /Which of these one session uses is chosen on the Charging page;/);
        assert.ok(link, 'there is no link to the Charging page');
        assert.equal(link.getAttribute('href'), '/configuration/session');

    });

    it('says what an unencrypted key lets somebody take: the vehicle\'s identity and its contract', async () => {

        const root   = await opened();
        const notice = [ ...root.querySelectorAll('.notice') ].find(one => /not encrypted/.test(one.textContent ?? ''));

        assert.ok(notice, 'there is no notice of the unencrypted keys');
        assert.match(notice.textContent!.replace(/\s+/g, ' '), /can take this vehicle's identity and its contract\./);

    });

    it('marks the certificate a session takes as chosen, in the vehicle\'s words', async () => {

        const root = await opened();
        const row  = [ ...root.querySelectorAll('tr') ].find(one => (one.textContent ?? '').includes('Vehicle A'));

        assert.ok(row, 'there is no row of Vehicle A');

        const mark = [ ...row.querySelectorAll<HTMLElement>('.chip[title]') ].find(one => one.textContent!.trim() === 'chosen');

        assert.ok(mark, 'Vehicle A is not marked as chosen');
        assert.equal(mark.getAttribute('title'), 'Who this vehicle is in a session - chosen on the Charging page, and not deleted while it is');

    });

});
