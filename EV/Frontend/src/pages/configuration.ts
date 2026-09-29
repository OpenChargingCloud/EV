import { api } from '../api/client';
import { card, librariesCard } from '@node/cards';
import { html, must, render } from '@node/html';
import type { Page } from '@node/router';
import { shell } from '@node/shell';
import { errorMessage, formatSince } from '@node/ui';

/**
 * What this vehicle is - read-only: it answers "what am I running", not
 * "change it". The pages below this one are where things change.
 *
 * The fields of each section are rendered from whatever the vehicle sends
 * rather than from a list kept here, so a field added on the server shows up
 * without a change to this page. The sections are not: which of them there
 * are, their order and their headings are decided here, and a section the
 * server adds shows up once it has a card below.
 */
export const configurationPage: Page = {

    title: 'Configuration',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration',
            title:     'Configuration',
            subtitle:  'What this vehicle is running.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').
            addEventListener('click', () => void load());

        let cancelled = false;

        async function load(): Promise<void> {

            try
            {

                const [configuration, status] = await Promise.all([
                    api.configuration(),
                    api.status()
                ]);

                if (cancelled)
                    return;

                render(content, html`

                    <div class="cards">

                        ${card('Vehicle',       'fa-car-side',       configuration.vehicle, html`
                            <div class="kv">
                                <span class="k">Uptime</span>
                                <span class="v">${status.uptime} <span class="muted">(started ${formatSince(status.startedAt)})</span></span>
                            </div>
                        `)}

                        ${card('Battery',       'fa-battery-half',     configuration.battery)}
                        ${card('ISO 15118',     'fa-tower-broadcast',  configuration.v2g)}
                        ${card('HTTP server',   'fa-server',           configuration.http)}
                        ${card('Accounts',      'fa-user-lock',        configuration.web)}
                        ${card('Event log',     'fa-list-ul',          configuration.log)}
                        ${card('Time',          'fa-clock',            configuration.time)}

                        ${librariesCard(configuration.assemblies)}

                    </div>

                `);

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                render(content, html`
                    <div class="error-box">The configuration could not be loaded: ${errorMessage(problem)}</div>
                `);

            }

        }

        void load();

        return () => { cancelled = true; };

    }

};
