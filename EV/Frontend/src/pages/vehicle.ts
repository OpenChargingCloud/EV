import { api, type VehicleConfiguration, type VehicleUpdate } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, numberField, whileSaving } from '@node/ui';
import { typedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render } from '@node/view';

/**
 * What this vehicle is, and what its battery wants.
 *
 * None of it reaches a station until the next session starts, which is why
 * this page may be edited while one is running: a battery figure changed
 * mid-session would otherwise turn into a vehicle that asked for one thing and
 * then metered another.
 *
 * Where each of these figures lands on the wire is not the same in the four
 * modes - "9 kW" is an EVMaxCurrent in -2 AC and an EVTargetCurrent in -20 DC
 * - so they are kept here as what a driver would say and translated by the
 * session, not by this page.
 *
 * Drawn by view.ts: a draw changes only what differs, so that what is typed
 * into one of its two forms - and its focus - outlives the other being saved.
 */
export const vehiclePage: Page = {

    title: 'Vehicle',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/vehicle',
            title:     'Vehicle',
            subtitle:  'What this vehicle is, and what its battery asks a station for.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('vehicle', 'edit');

        let cancelled = false;
        let current: VehicleConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const battery       = configuration.battery;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at this vehicle', 'change it')}
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-car-side"></i> Identity</h2>

                        <form id="identity-form" class="form-stack" @submit=${saveIdentity}>

                            <label>Name
                                <input type="text" name="name" value="${configuration.name}"
                                       maxlength="${configuration.limits.maxNameLength}"
                                       placeholder="EV" ?disabled=${!mayChange} />
                            </label>

                            <label>Vehicle identification number
                                <input type="text" name="vin" value="${configuration.vin ?? ''}"
                                       maxlength="${configuration.limits.maxNameLength}"
                                       placeholder="WVWZZZ..." ?disabled=${!mayChange} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="identity-note"  class="form-notice" role="status"></span>
                                <span id="identity-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                What this vehicle calls itself in its own log and on this page. Neither of
                                these is how a station recognises it - that is the Vehicle certificate,
                                which is a different thing and lives somewhere else.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-battery-half"></i> Battery</h2>

                        <form id="battery-form" class="form-stack" @submit=${saveBattery}>

                            <label>Usable capacity in kWh
                                <input type="number" name="batteryCapacityKWh" min="0.1" step="0.1"
                                       max="${configuration.limits.maxCapacityKWh}"
                                       value="${battery.capacityKWh}" ?disabled=${!mayChange} />
                            </label>

                            <label>State of charge at plug-in, in percent
                                <input type="number" name="stateOfChargePercent" min="0" max="100" step="1"
                                       value="${battery.stateOfChargePercent}" ?disabled=${!mayChange} />
                            </label>

                            <label>Charge until, in percent
                                <input type="number" name="targetStateOfChargePercent" min="0" max="100" step="1"
                                       value="${battery.targetStateOfChargePercent}" ?disabled=${!mayChange} />
                            </label>

                            <label>Ask for, in kW
                                <input type="number" name="maxChargingPowerKW" min="0.1" step="0.1"
                                       max="${configuration.limits.maxPowerKW}"
                                       value="${battery.maxChargingPowerKW}" ?disabled=${!mayChange} />
                            </label>

                            <label>Start asking for less from, in percent
                                <input type="number" name="taperFromPercent" min="0" max="100" step="1"
                                       value="${battery.taperFromPercent}" ?disabled=${!mayChange} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="battery-note"  class="form-notice" role="status"></span>
                                <span id="battery-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. A taper of 100 % charges flat to the end;
                                below that the ask falls off linearly to nothing at full, which is
                                arithmetic and not a charging curve. A station that gives less than it was
                                asked for shows up as a slower charge - the pack fills with what the meter
                                counted, not with what was wanted.
                            </span>

                        </form>

                    </section>

                </div>

            `);

        }


        function saveIdentity(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const data = new FormData(form);

            void save(form, 'identity', {
                name:  String(data.get('name') ?? '').trim(),
                vin:   String(data.get('vin')  ?? '').trim()
            });

        }


        function saveBattery(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;

            // An emptied number is NaN here and null on the wire, which the
            // vehicle reads as "not given": what it had stays - where Number()
            // made it 0, which is a state of charge of 0 %.
            void save(form, 'battery', {
                batteryCapacityKWh:          numberField(form, 'batteryCapacityKWh'),
                stateOfChargePercent:        numberField(form, 'stateOfChargePercent'),
                targetStateOfChargePercent:  numberField(form, 'targetStateOfChargePercent'),
                maxChargingPowerKW:          numberField(form, 'maxChargingPowerKW'),
                taperFromPercent:            numberField(form, 'taperFromPercent')
            });

        }


        async function save(form: HTMLFormElement, which: 'identity' | 'battery', update: VehicleUpdate): Promise<void> {

            const note = must<HTMLElement>(content, `#${which}-note`);

            note.textContent = '';

            must<HTMLElement>(content, `#${which}-error`).textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.vehicle.save(update));

                if (cancelled)
                    return;

                draw();

                // A draw leaves a form as it is typed into; this one was
                // saved, so it goes back to what it says now - the answer.
                form.reset();

                note.textContent = 'Saved, and in effect from the next session.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, `#${which}-error`).textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {
                const loaded = await api.vehicle.get();

                if (!cancelled) {
                    current = loaded;
                    draw();
                }
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The vehicle configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        /**
         * Loaded anew - Reload - is what the vehicle has, the forms too, which
         * a draw on its own would leave as typed.
         */
        async function reload(): Promise<void> {

            await load();

            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());

        }

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#identity-form')) ||
                                             typedSinceDrawn(content.querySelector('#battery-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
