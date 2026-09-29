import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { toURL } from '@node/basePath';
import { html } from '@node/html';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { vehiclePage }       from './pages/vehicle';
import { v2gPage }           from './pages/v2g';
import { sessionPage }       from './pages/session';

/**
 * What one of a session's four certificates is, on the row of the one the
 * Charging page names - which the vehicle will not let go of while it does.
 */
const chosenFor = (What: string) => ({
    label:  'chosen',
    title:  `${What} - chosen on the Charging page, and not deleted while it is`
});

// What a vehicle has pages for beside what every node has: itself and its
// battery, the wire below the charging cable, and charging. The sign-in, the
// log, the name servers, the time servers, the certificate store, the frame,
// "/" - the first page of the menu the account signed in may open - and
// following the log while somebody is signed in are every node's - see
// WWCP_Node's start.ts. Following the log is also what makes the ISO 15118
// page worth watching: a discovery writes every attempt and every answer into
// it as it happens.
startNode({

    name:  'Electric Vehicle',
    icon:  'fa-car-side',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            { path: '/configuration/vehicle',  label: 'Vehicle',    icon: 'fa-car-side',         permission: [ 'vehicle:read' ] },
            { path: '/configuration/v2g',      label: 'ISO 15118',  icon: 'fa-tower-broadcast',  permission: [ 'v2g:read' ]     },
            nodeMenu.certificates,
            { path: '/configuration/session',  label: 'Charging',   icon: 'fa-bolt',             permission: [ 'session:read' ] }
        ]),
        nodeMenu.logs
    ],

    pages: {

        '/configuration':               configurationPage,
        '/configuration/vehicle':       vehiclePage,
        '/configuration/v2g':           v2gPage,
        '/configuration/session':       sessionPage

    },

    // What a vehicle says of its roots beyond what every kind says: that none
    // of them is chosen for a session, and that a chain of ISO 15118's is not
    // checked at all where there is no root of its kind - a server's still is,
    // against the roots this machine trusts. And what it presents is chosen
    // for a session, on the Charging page.
    certificates: {

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
                <a href="${toURL('/configuration/session')}">Charging</a> page; this is where they are put
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

    }

});
