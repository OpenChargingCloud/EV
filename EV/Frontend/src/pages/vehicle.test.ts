/**
 * The vehicle page drawn, in a document of happy-dom, against a stand-in
 * vehicle: what is typed into one of its two forms - and its focus - outlives
 * the other being saved, a form saved says what the vehicle took, and one
 * refused keeps what is typed and says why.
 */

import { asked, field, open, refused, submit, until, type Asked } from '../../test/vehicle.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { VehicleConfiguration, VehicleUpdate } from '../api/client.ts';

const { vehiclePage } = await import('./vehicle.ts');


let held: VehicleConfiguration;

/** What a form says once its save went through - and not before: whileSaving() says "Saving ..." meanwhile. */
const saved = 'Saved, and in effect from the next session.';

/** Refuses a change of the battery where told to. */
let refuseBattery = false;

function vehicle({ method, path, body }: Asked): unknown {

    if (path === '/configuration/vehicle' && method === 'PUT') {

        const update = body as VehicleUpdate;

        if (refuseBattery && update.batteryCapacityKWh !== undefined)
            return refused(400, "'vehicle.batteryCapacityKWh' must be between 0.1 and 1000.");

        // A vehicle takes what it takes: no more power than it may ask for.
        held = {
            ...held,
            name:     update.name ?? held.name,
            vin:      update.vin  ?? held.vin,
            battery:  {
                capacityKWh:                 update.batteryCapacityKWh          ?? held.battery.capacityKWh,
                stateOfChargePercent:        update.stateOfChargePercent        ?? held.battery.stateOfChargePercent,
                targetStateOfChargePercent:  update.targetStateOfChargePercent  ?? held.battery.targetStateOfChargePercent,
                maxChargingPowerKW:          Math.min(update.maxChargingPowerKW ?? held.battery.maxChargingPowerKW, 50),
                taperFromPercent:            update.taperFromPercent            ?? held.battery.taperFromPercent
            }
        };

        return held;

    }

    if (path === '/configuration/vehicle')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = {
        name:     'EV',
        vin:      null,
        battery:  { capacityKWh: 60, stateOfChargePercent: 50, targetStateOfChargePercent: 100,
                    maxChargingPowerKW: 11, taperFromPercent: 80 },
        limits:   { maxCapacityKWh: 1000, maxPowerKW: 400, maxNameLength: 64 },
        file:     'wwcp.json'
    };
    refuseBattery = false;
    return open(vehiclePage, '/configuration/vehicle', [ 'vehicle:read', 'vehicle:edit' ],
                vehicle, root => root.querySelector('#battery-form') !== null);
}


describe('the vehicle page', () => {

    it('keeps what is typed into the battery, and its focus, while the identity is saved', async () => {

        const root     = await opened();
        const browser  = chromeTakesTheFocus(root);
        const capacity = field(root, '#battery-form', 'batteryCapacityKWh');

        capacity.value = '77.5';
        capacity.focus();

        field(root, '#identity-form', 'name').value = 'Test vehicle';

        submit(root, '#identity-form');
        await until(() => held.name === 'Test vehicle' && root.querySelector('#identity-note')?.textContent === saved,
                    'the identity was not saved');

        browser.disconnect();

        assert.ok(field(root, '#battery-form', 'batteryCapacityKWh') === capacity, 'the field was made anew');
        assert.equal(capacity.value, '77.5');
        assert.ok(document.activeElement === capacity, 'the focus went');

    });

    it('shows the battery as the vehicle took it once it is saved, with nothing left to save', async () => {

        const root  = await opened();
        const power = field(root, '#battery-form', 'maxChargingPowerKW');

        power.value = '150';

        submit(root, '#battery-form');
        await until(() => held.battery.maxChargingPowerKW === 50 && root.querySelector('#battery-note')?.textContent === saved,
                    'the battery was not saved');

        assert.equal((asked.find(one => one.method === 'PUT')?.body as VehicleUpdate).maxChargingPowerKW, 150);
        assert.equal(field(root, '#battery-form', 'maxChargingPowerKW').value,         '50', 'the field says what was typed, not what the vehicle took');
        assert.equal(field(root, '#battery-form', 'maxChargingPowerKW').defaultValue,  '50');

    });

    it('keeps what is typed into a battery the vehicle refused, and says why', async () => {

        const root     = await opened();
        const capacity = field(root, '#battery-form', 'batteryCapacityKWh');

        refuseBattery = true;
        capacity.value = '2000';

        submit(root, '#battery-form');
        await until(() => root.querySelector('#battery-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#battery-error')!.textContent, "'vehicle.batteryCapacityKWh' must be between 0.1 and 1000.");
        assert.ok(field(root, '#battery-form', 'batteryCapacityKWh') === capacity, 'the field was made anew');
        assert.equal(capacity.value, '2000', 'what was typed went');

    });

    it('says on Reload what the vehicle has, in every form', async () => {

        const root = await opened();

        field(root, '#identity-form', 'name').value                 = 'typed, not saved';
        field(root, '#battery-form',  'stateOfChargePercent').value = '5';

        held = { ...held, name: 'Renamed elsewhere' };

        root.querySelector<HTMLButtonElement>('#reload')!.click();
        await until(() => field(root, '#identity-form', 'name').value === 'Renamed elsewhere', 'Reload did not say what the vehicle has');

        assert.equal(field(root, '#battery-form', 'stateOfChargePercent').value, '50');

    });

});
