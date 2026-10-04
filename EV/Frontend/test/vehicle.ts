/*
 * The vehicle as the stand-in node of WWCP_Node's test/node.ts, which every
 * kind's page tests share: a stand-in for fetch, and a document of happy-dom
 * to draw a page into. What the tests of the pages, and of the vehicle's
 * words on the certificates page, use.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/vehicle.ts';
 *   const { vehiclePage } = await import('./vehicle.ts');
 */

import { standIn } from '@node/../test/node.ts';
export * from '@node/../test/node.ts';

// A driver rather than an admin, with the permissions each test opens a page
// with.
standIn({ name: 'Electric Vehicle', icon: 'fa-car-side', user: { roles: [ 'driver' ] } });
