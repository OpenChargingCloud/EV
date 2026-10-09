/**
 * Every node's certificates page drawn with what a vehicle says on it, in a
 * document of happy-dom, against a stand-in vehicle: its words under what it
 * believes and what it presents, the Charging Session page they point to, what an
 * unencrypted key lets somebody take, and a certificate a session takes
 * marked as chosen - each a template of view.ts, as the page draws them.
 */

import { open } from '../test/vehicle.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { Certificate, CertificateStore } from './api/client.ts';

const { certificatesPage, identitiesPage } = await import('@node/pages/certificates.ts');
const { vehicleCertificates }  = await import('./certificates.ts');


const vehicleA = {
    id:            'aaaaaaaaaaaaaaaa',
    kind:          'vehicle',
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
    credentials:         [ 'vehicle', 'contract' ],
    recognised:          [],
    kinds:               { v2gRoot:   { description: 'V2G root',             page: 'certificates', trustAnchor: true,  needsPrivateKey: false, usages: [] },
                           vehicle:   { description: 'Vehicle certificate',  page: 'identities',   trustAnchor: false, needsPrivateKey: true,  usages: [] },
                           contract:  { description: 'Contract certificate', page: 'identities',   trustAnchor: false, needsPrivateKey: true,  usages: [] } },
    usages:              [],
    certificates:        { v2gRoot: [], vehicle: [ vehicleA ], contract: [ { ...vehicleA, kind: 'contract' } ] },
    keysAreUnencrypted:  true,
    chosen:              { vehicleCertificate: { id: vehicleA.id, kind: 'vehicle' }, contractCertificate: null }
} as unknown as CertificateStore;

/** The certificates alone: the roots a vehicle believes. */
const opened = () => open(certificatesPage(vehicleCertificates), '/configuration/certificates', [ 'certificates:read' ],
                          ({ path }) => path === '/certificates' ? store : undefined,
                          root => [ ...root.querySelectorAll('h2') ].some(one => /believes/.test(one.textContent ?? '')));

/** Who the vehicle is, with the keys: its credentials, a session's among them. */
const identities = () => open(identitiesPage(vehicleCertificates), '/configuration/identities', [ 'certificates:read' ],
                              ({ path }) => path === '/certificates' ? store : undefined,
                              root => [ ...root.querySelectorAll('h2') ].some(one => /Who this/.test(one.textContent ?? '')));

/** The hint right below a group's heading: "What this electric vehicle believes", say, or "Who this electric vehicle is". */
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

    it('shows its credentials on Identities, and not among the certificates', async () => {

        const certificates = await opened();

        assert.ok(![ ...certificates.querySelectorAll('h2') ].some(one => /Who this/.test(one.textContent ?? '')), 'who it is is on the certificates page');
        assert.ok(!(certificates.textContent ?? '').includes('Vehicle A'), 'a credential is among the certificates');

        const credentials = await identities();

        assert.ok((credentials.textContent ?? '').includes('Vehicle A'), 'the credential is not among the identities');

    });

    it('says under what it presents that a session\'s certificates are chosen on the Charging Session page, and links to it', async () => {

        const root = await identities();
        const link = [ ...root.querySelectorAll<HTMLAnchorElement>('a') ].find(one => one.textContent === 'Charging Session');

        assert.match(hintBelow(root, /Who this/), /Which of these one session uses is chosen on the Charging Session page;/);
        assert.ok(link, 'there is no link to the Charging Session page');
        assert.equal(link.getAttribute('href'), '/configuration/session');

    });

    it('says what an unencrypted key lets somebody take: the vehicle\'s identity and its contract', async () => {

        const root   = await identities();
        const notice = [ ...root.querySelectorAll('.notice') ].find(one => /not encrypted/.test(one.textContent ?? ''));

        assert.ok(notice, 'there is no notice of the unencrypted keys');
        assert.match(notice.textContent!.replace(/\s+/g, ' '), /can take this vehicle's identity and its contract\./);

    });

    it('marks the certificate a session takes as chosen, in the vehicle\'s words', async () => {

        const root = await identities();
        const row  = [ ...root.querySelectorAll('tr') ].find(one => (one.textContent ?? '').includes('Vehicle A'));

        assert.ok(row, 'there is no row of Vehicle A');

        const mark = [ ...row.querySelectorAll<HTMLElement>('.chip[title]') ].find(one => one.textContent!.trim() === 'chosen');

        assert.ok(mark, 'Vehicle A is not marked as chosen');
        assert.equal(mark.getAttribute('title'), 'Who this vehicle is in a session - chosen on the Charging Session page, and not deleted while it is');

    });

    it('marks a certificate kept as two kinds as chosen in the row of the kind it is chosen as alone', async () => {

        const root   = await identities();
        const rows   = [ ...root.querySelectorAll('tr') ].filter(one => (one.textContent ?? '').includes('Vehicle A'));
        const marked = rows.map(row => [ ...row.querySelectorAll('.chip') ].some(one => one.textContent!.trim() === 'chosen'));

        assert.equal(rows.length, 2, 'Vehicle A is not shown as a vehicle and a contract certificate');
        assert.deepEqual(marked, [ true, false ], 'chosen as the vehicle certificate, and not as the contract certificate');

    });

});
