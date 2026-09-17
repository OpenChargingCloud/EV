import { logLevels, type LogEntry, type LogLevel } from '../api/client';
import { escapeHTML, html, must, render } from '../html';
import { logs } from '../logs/store';
import type { Page } from '../router';
import { shell } from '../shell';
import { formatTime, formatTimestamp, isAtLeast } from '../ui';

/**
 * Everything that happens inside the vehicle, as it happens.
 *
 * The entries arrive over one Server-Sent Events stream and are appended to
 * the list; the filters work on what is already in the browser, so changing
 * one costs nothing and asks the vehicle for nothing. A list that is scrolled
 * to the bottom follows along; scrolling up stops that, which is what somebody
 * reading an older line wants - and the button at the bottom brings them back.
 */
export const logsPage: Page = {

    title: 'Logs',

    render({ root }) {

        const content = shell(root, {
            active:    '/logs',
            title:     'Logs',
            subtitle:  'Everything this vehicle does, as it happens.',
            actions:   html`
                <span id="stream-state" class="stream-state"></span>
                <button type="button" id="clear" class="btn small" title="Clear what this page shows; the vehicle keeps its log">Clear view</button>
            `
        });

        render(content, html`

            <div class="log-filters">

                <label class="filter-search">
                    <i class="fa-solid fa-magnifying-glass"></i>
                    <input type="search" id="search" placeholder="Search the messages ..." autocomplete="off" />
                </label>

                <label class="filter-level">
                    Level
                    <select id="level">
                        ${logLevels.map(level => html`
                            <option value="${level}" ${level === 'debug' ? html`selected` : ''}>${level}</option>
                        `)}
                    </select>
                </label>

                <label class="filter-follow">
                    <input type="checkbox" id="follow" checked />
                    Follow
                </label>

            </div>

            <div id="tags" class="tag-filters"></div>

            <div id="log" class="log" role="log" aria-live="polite" tabindex="0">
                <div id="log-lines"></div>
                <div id="log-error" class="line error" hidden></div>
                <div id="log-empty" class="log-empty" hidden>
                    Nothing to show. The vehicle has been quiet, or the filters are too narrow.
                </div>
            </div>

            <div class="log-foot small muted">
                <span id="counts"></span>
                <button type="button" id="to-bottom" class="btn small" hidden>Jump to the newest</button>
            </div>

        `);

        const list        = must<HTMLElement>       (content, '#log');
        const lineBox     = must<HTMLElement>       (content, '#log-lines');
        const emptyNote   = must<HTMLElement>       (content, '#log-empty');
        const errorNote   = must<HTMLElement>       (content, '#log-error');
        const tagBox      = must<HTMLElement>       (content, '#tags');
        const counts      = must<HTMLElement>       (content, '#counts');
        const toBottom    = must<HTMLButtonElement> (content, '#to-bottom');
        const search      = must<HTMLInputElement>  (content, '#search');
        const level       = must<HTMLSelectElement> (content, '#level');
        const follow      = must<HTMLInputElement>  (content, '#follow');
        const streamState = must<HTMLElement>       (root,    '#stream-state');
        const clear       = must<HTMLButtonElement> (root,    '#clear');

        /** The tags somebody has switched on; empty means "every tag". */
        const chosenTags = new Set<string>();

        let renderedTags = '';

        /** How many lines the filters are letting through, for the count below. */
        let shown = 0;


        function matches(entry: LogEntry): boolean {

            if (!isAtLeast(entry.level, level.value as LogLevel))
                return false;

            if (chosenTags.size > 0) {

                // Any of the chosen ones, not all of them: somebody who picks
                // "ocpp" and "15118" wants to watch both conversations, not
                // the empty set of lines that are about both at once. The
                // level counts as a tag, which is how "critical" and "ocpp"
                // can be picked together.
                const own = new Set<string>([entry.level, ...entry.tags]);

                let hit = false;

                for (const tag of chosenTags) {
                    if (own.has(tag)) {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                    return false;

            }

            const needle = search.value.trim().toLowerCase();

            return needle.length === 0 ||
                   entry.message.toLowerCase().includes(needle);

        }


        function lineHTML(entry: LogEntry): string {
            return `<div class="line ${entry.level}" data-id="${entry.id}">` +
                       `<time datetime="${escapeHTML(entry.timestamp)}" title="${escapeHTML(formatTimestamp(entry.timestamp))}">${escapeHTML(formatTime(entry.timestamp))}</time>` +
                       `<span class="chip level ${entry.level}">${escapeHTML(entry.level)}</span>` +
                       entry.tags.map(tag => `<span class="chip tag">${escapeHTML(tag)}</span>`).join('') +
                       `<span class="message">${escapeHTML(entry.message)}</span>` +
                   `</div>`;
        }

        function atBottom(): boolean {
            // A few pixels of slack: a list that is one rounding error short
            // of the bottom is, to the person reading it, at the bottom.
            return list.scrollTop + list.clientHeight >= list.scrollHeight - 24;
        }

        function scrollToBottom(): void {
            list.scrollTop = list.scrollHeight;
            toBottom.hidden = true;
        }

        /**
         * Everything from the store, drawn once.
         *
         * Only for the two moments when what is held has actually changed
         * underneath: a snapshot reloaded from the vehicle, and "Clear view".
         * Changing a filter is not one of them - see applyFilters below.
         */
        function redraw(): void {

            lineBox.innerHTML = logs.entries.map(lineHTML).join('');

            applyFilters();

            if (follow.checked)
                scrollToBottom();

            drawTags();

        }

        /**
         * Which of the lines already drawn are wanted.
         *
         * A filter used to rebuild the whole list, which measured 597 ms for
         * one keystroke in the search box at 1959 entries - a third of a
         * second of frozen page per character, and it grows with the log. Most
         * of that was work already done: the same lines built again from the
         * same entries, and every timestamp put through the locale formatter
         * a second time.
         *
         * A line is now made once and then only told whether it is wanted,
         * which measured 2 ms for the same 1959.
         */
        function applyFilters(): void {

            const lines = lineBox.children;
            const many  = Math.min(lines.length, logs.entries.length);

            shown = 0;

            for (let index = 0; index < many; index++) {

                const wanted = matches(logs.entries[index]!);

                lines[index]!.classList.toggle('filtered-out', !wanted);

                if (wanted)
                    shown++;

            }

            emptyNote.hidden = shown > 0;

            updateCounts();

        }

        /** Only what is new: the usual case, and the cheap one. */
        function append(added: LogEntry[]): void {

            if (added.length > 0) {

                const stick = follow.checked && atBottom();

                lineBox.insertAdjacentHTML('beforeend', added.map(lineHTML).join(''));

                // The vehicle keeps a bounded log and so does this page; what
                // fell out of the store has to leave the list as well. The
                // lines and the entries are kept in step, oldest first, which
                // is what lets a filter be applied by position above.
                while (lineBox.childElementCount > logs.entries.length) {

                    if (lineBox.firstElementChild?.classList.contains('filtered-out') === false)
                        shown--;

                    lineBox.firstElementChild?.remove();

                }

                // Only the new lines are asked about. Asking the whole list
                // again would put the cost of a filter change on every single
                // line the vehicle writes.
                const first = lineBox.childElementCount - added.length;
                let   any   = false;

                added.forEach((entry, index) => {

                    const wanted = matches(entry);

                    lineBox.children[first + index]?.classList.toggle('filtered-out', !wanted);

                    if (wanted) {
                        shown++;
                        any = true;
                    }

                });

                emptyNote.hidden = shown > 0;

                if (any) {

                    if (stick)
                        scrollToBottom();
                    else
                        toBottom.hidden = false;

                }

                updateCounts();

            }

            drawTags();

        }

        function updateCounts(): void {

            counts.textContent = `${shown} of ${logs.entries.length} entries` +
                                 (logs.capacity > 0 ? ` (the vehicle keeps the last ${logs.capacity})` : '');

        }

        /** The tag buttons, redrawn only when the vehicle has learned a new tag. */
        function drawTags(): void {

            const all = [...new Set([...logLevels, ...logs.tags])].sort();
            const key = all.join('\0') + '|' + [...chosenTags].sort().join('\0');

            if (key === renderedTags)
                return;

            renderedTags = key;

            tagBox.innerHTML = all.map(tag =>
                `<button type="button" class="chip tag-button ${chosenTags.has(tag) ? 'on' : ''}" data-tag="${escapeHTML(tag)}" aria-pressed="${chosenTags.has(tag)}">${escapeHTML(tag)}</button>`
            ).join('') +
            (chosenTags.size > 0
                 ? '<button type="button" class="chip tag-button clear-tags" data-tag="">all tags</button>'
                 : '');

        }

        function showStream(): void {
            streamState.className   = `stream-state ${logs.streamConnected ? 'live' : 'down'}`;
            streamState.textContent = logs.streamConnected ? 'live' : 'reconnecting ...';
        }


        // Events

        tagBox.addEventListener('click', event => {

            const button = (event.target as Element | null)?.closest<HTMLElement>('.tag-button');

            if (!button)
                return;

            const tag = button.dataset.tag ?? '';

            if (tag === '')
                chosenTags.clear();
            else if (chosenTags.has(tag))
                chosenTags.delete(tag);
            else
                chosenTags.add(tag);

            renderedTags = '';
            applyFilters();
            drawTags();

        });

        search  .addEventListener('input',  () => applyFilters());
        level   .addEventListener('change', () => applyFilters());
        follow  .addEventListener('change', () => { if (follow.checked) scrollToBottom(); });
        toBottom.addEventListener('click',  () => scrollToBottom());
        clear   .addEventListener('click',  () => logs.clear());

        list.addEventListener('scroll', () => {
            if (atBottom())
                toBottom.hidden = true;
        });

        const stopListening = logs.onChange(event => {

            switch (event.type) {

                case 'entries':
                    append(event.added);
                    break;

                case 'reloaded':
                    errorNote.hidden = true;
                    redraw();
                    break;

                case 'stream':
                    showStream();
                    break;

                case 'error':
                    errorNote.innerHTML = `<span class="message">${escapeHTML(event.text)}</span>`;
                    errorNote.hidden    = false;
                    break;

            }

        });

        showStream();
        redraw();

        // The store keeps running between pages - the log goes on filling while
        // somebody reads the configuration - so only this page's listener goes.
        return stopListening;

    }

};
