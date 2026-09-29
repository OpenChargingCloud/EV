/**
 * What somebody typed into the forms of a page, carried across the page
 * drawing itself anew.
 *
 * The vehicle's pages with more than one form draw themselves anew from the
 * vehicle's answer whenever one of them is saved, or a discovery or a pairing
 * has something to say - and with them the other forms, which may hold what
 * somebody typed and has not saved yet. That was gone, and nobody was asked,
 * because the page itself threw it away. keepDrafts draws anew and puts back
 * into every other form what was typed into it: the form saved is drawn as
 * the vehicle now has it, the rest as they were left - still typed into, so
 * that leaving the page still asks first.
 */

import { typedSinceDrawn } from '@node/unsaved';


/** A form's control, as far as this needs one: what the DOM has, and what a test can stand in for. */
interface Control {
    tagName:   string;
    name:      string;
    type:      string;
    value:     string;
    checked:   boolean;
    options?:  ArrayLike<{ value: string; selected: boolean }>;
}

/** A form, as far as this needs one. */
interface Form {
    id:        string;
    elements:  ArrayLike<unknown>;
}

/** What one control was left at. */
export interface Kept {
    name:      string;
    type:      string;
    value:     string;
    checked:   boolean;
    /** What is chosen, where it is a list. */
    selected:  string[] | null;
}


/** The controls of a form that hold something typed: not its buttons, and not a file, which cannot be put back. */
function controlsOf(Form: Form): Control[] {
    return (Array.from(Form.elements) as Control[]).
               filter(control => [ 'INPUT', 'SELECT', 'TEXTAREA' ].includes(control.tagName) &&
                                 control.name !== '' &&
                                 control.type !== 'file');
}

function formsIn(Root: ParentNode): Form[] {
    return Array.from(Root.querySelectorAll('form')) as unknown as Form[];
}


/** What is typed into every form in Root but the one called Except, by the form's id. */
export function draftsIn(Root: ParentNode, Except: string | null): Map<string, Kept[]> {

    const drafts = new Map<string, Kept[]>();

    for (const form of formsIn(Root)) {

        if (form.id === '' || form.id === Except || !typedSinceDrawn(form as unknown as ParentNode))
            continue;

        drafts.set(form.id, controlsOf(form).map(control => ({
            name:      control.name,
            type:      control.type,
            value:     control.value,
            checked:   control.checked,
            selected:  control.tagName === 'SELECT'
                           ? Array.from(control.options ?? []).filter(option => option.selected).map(option => option.value)
                           : null
        })));

    }

    return drafts;

}


/**
 * Put what was typed back into the forms drawn anew, control by control. A
 * form drawn with other controls than it had is left as drawn: which of them
 * a value belonged to is no longer certain.
 */
export function putBack(Root: ParentNode, Drafts: ReadonlyMap<string, readonly Kept[]>): void {

    for (const form of formsIn(Root)) {

        const kept = Drafts.get(form.id);

        if (kept === undefined)
            continue;

        const controls = controlsOf(form);

        if (controls.length !== kept.length ||
            controls.some((control, index) => control.name !== kept[index]!.name || control.type !== kept[index]!.type))
            continue;

        controls.forEach((control, index) => {

            const was = kept[index]!;

            if (was.selected !== null)
                for (const option of Array.from(control.options ?? []))
                    option.selected = was.selected.includes(option.value);

            else if (control.type === 'checkbox' || control.type === 'radio')
                control.checked = was.checked;

            else
                control.value = was.value;

        });

    }

}


/** Draw anew, and keep what is typed into every form in Root but the one called Except. */
export function keepDrafts(Root: ParentNode, Except: string | null, Draw: () => void): void {

    const drafts = draftsIn(Root, Except);

    Draw();

    putBack(Root, drafts);

}
