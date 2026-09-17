import { api, type VehicleConfiguration, type VehicleUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

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
 */
export const vehiclePage: Page = {

    title: 'Vehicle',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/vehicle',
            title:     'Vehicle',
            subtitle:  'What this vehicle is, and what its battery asks a station for.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeChargingSettings');

        let cancelled = false;
        let current: VehicleConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const battery       = configuration.battery;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at this
                        vehicle but not change it. That needs the driver, the service or the system
                        administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-car-side"></i> Identity</h2>

                        <form id="identity-form" class="form-stack">

                            <label>Name
                                <input type="text" name="name" value="${configuration.name}"
                                       maxlength="${configuration.limits.maxNameLength}"
                                       placeholder="EV" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Vehicle identification number
                                <input type="text" name="vin" value="${configuration.vin ?? ''}"
                                       maxlength="${configuration.limits.maxNameLength}"
                                       placeholder="WVWZZZ..." ${mayChange ? '' : html`disabled`} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
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

                        <form id="battery-form" class="form-stack">

                            <label>Usable capacity in kWh
                                <input type="number" name="batteryCapacityKWh" min="0.1" step="0.1"
                                       max="${configuration.limits.maxCapacityKWh}"
                                       value="${battery.capacityKWh}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>State of charge at plug-in, in percent
                                <input type="number" name="stateOfChargePercent" min="0" max="100" step="1"
                                       value="${battery.stateOfChargePercent}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Charge until, in percent
                                <input type="number" name="targetStateOfChargePercent" min="0" max="100" step="1"
                                       value="${battery.targetStateOfChargePercent}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Ask for, in kW
                                <input type="number" name="maxChargingPowerKW" min="0.1" step="0.1"
                                       max="${configuration.limits.maxPowerKW}"
                                       value="${battery.maxChargingPowerKW}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Start asking for less from, in percent
                                <input type="number" name="taperFromPercent" min="0" max="100" step="1"
                                       value="${battery.taperFromPercent}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
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

            wire();

        }


        function wire(): void {

            must<HTMLFormElement>(content, '#identity-form').addEventListener('submit', event => {

                event.preventDefault();

                const data = new FormData(event.target as HTMLFormElement);

                void save('identity', {
                    name:  String(data.get('name') ?? '').trim(),
                    vin:   String(data.get('vin')  ?? '').trim()
                });

            });

            must<HTMLFormElement>(content, '#battery-form').addEventListener('submit', event => {

                event.preventDefault();

                const data = new FormData(event.target as HTMLFormElement);

                void save('battery', {
                    batteryCapacityKWh:          Number(data.get('batteryCapacityKWh')),
                    stateOfChargePercent:        Number(data.get('stateOfChargePercent')),
                    targetStateOfChargePercent:  Number(data.get('targetStateOfChargePercent')),
                    maxChargingPowerKW:          Number(data.get('maxChargingPowerKW')),
                    taperFromPercent:            Number(data.get('taperFromPercent'))
                });

            });

        }


        async function save(which: 'identity' | 'battery', update: VehicleUpdate): Promise<void> {

            const note = must<HTMLElement>(content, `#${which}-note`);

            note.textContent = '';

            must<HTMLElement>(content, `#${which}-error`).textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.vehicle.save(update));
                draw();
                must<HTMLElement>(content, `#${which}-note`).textContent = 'Saved, and in effect from the next session.';
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

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#identity-form')) ||
                                             typedSinceDrawn(content.querySelector('#battery-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
