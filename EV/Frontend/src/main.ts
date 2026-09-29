import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { dnsPage }           from './pages/dns';
import { ntsPage }           from './pages/nts';
import { vehiclePage }       from './pages/vehicle';
import { v2gPage }           from './pages/v2g';
import { certificatesPage }  from './pages/certificates';
import { sessionPage }       from './pages/session';

// What a vehicle has pages for beside what every node has: itself and its
// battery, the wire below the charging cable, and charging. The sign-in, the
// log, the frame and following the log while somebody is signed in are every
// node's - see WWCP_Node's start.ts. Following the log is also what makes the
// ISO 15118 page worth watching: a discovery writes every attempt and every
// answer into it as it happens.
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

        // "/" is the configuration, and is a page of its own rather than a
        // redirect to /configuration: the sign-in remembers where somebody was
        // going, and for the first visit that is "/".
        '/':                            configurationPage,
        '/configuration':               configurationPage,
        '/configuration/dns':           dnsPage,
        '/configuration/nts':           ntsPage,
        '/configuration/vehicle':       vehiclePage,
        '/configuration/v2g':           v2gPage,
        '/configuration/certificates':  certificatesPage,
        '/configuration/session':       sessionPage

    }

});
