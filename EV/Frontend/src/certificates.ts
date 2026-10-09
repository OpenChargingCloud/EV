/**
 * What a vehicle says on every node's certificates page beyond what every
 * kind says: that none of its roots is chosen for a session, and that a chain
 * of ISO 15118's is not checked at all where there is no root of its kind - a
 * server's still is, against the roots this machine trusts. And what it
 * presents is chosen for a session, on the Charging Session page.
 *
 * Templates of view.ts, as the page draws them: a draw changes only what
 * differs, the hints with the rest. main.ts hands them to startNode().
 */

import { toURL } from '@node/basePath';
import type { CertificatesOptions } from '@node/pages/certificates';
import { html } from '@node/view';

/**
 * What one of a session's four certificates is, on the row of the one the
 * Charging Session page names - which the vehicle will not let go of while it does.
 */
const chosenFor = (What: string) => ({
    label:  'chosen',
    title:  `${What} - chosen on the Charging Session page, and not deleted while it is`
});

export const vehicleCertificates: CertificatesOptions = {

    hints: {

        believes:     html`
            Trust anchors: the roots a certificate shown to this vehicle has to chain to, each kind for
            what its card says it is for. Every switched-on root of a kind is believed at once, and none
            of them is chosen for a session. A vehicle holding no V2G, Mobility Operator or OEM root does
            not check that kind of chain at all, and says so in the log rather than silently accepting
            it; a server's certificate may chain to the roots this machine trusts as well.
        `,

        presents:     html`
            Which of these one session uses is chosen on the
            <a href="${toURL('/configuration/session')}">Charging Session</a> page; this is where they are put
            on the vehicle and taken off it. A certificate marked <em>chosen</em> is the one that page
            names.
        `,

        unencrypted:  html`can take this vehicle's identity and its contract.`

    },

    chosen: {
        vehicleCertificate:   chosenFor('Who this vehicle is in a session'),
        contractCertificate:  chosenFor('Who pays for a session'),
        oemCertificate:       chosenFor('What this vehicle was born with'),
        tariffCertificate:    chosenFor("What a station's signed tariff is checked against")
    }

};
