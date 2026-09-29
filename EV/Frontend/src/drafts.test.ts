/**
 * What keepDrafts carries across a page drawing itself anew: what is typed
 * into the forms not saved, and nothing else.
 *
 * Run with `npm test`. The forms are stand-ins with the properties the DOM
 * gives a form and its controls, since Node has no DOM.
 */

import { strict as assert } from 'node:assert';
import { describe, it }     from 'node:test';

import { typedSinceDrawn }  from '@node/unsaved.ts';

import { draftsIn, keepDrafts } from './drafts.ts';


interface Option {
    value:            string;
    selected:         boolean;
    defaultSelected:  boolean;
}

interface Control {
    tagName:         string;
    name:            string;
    type:            string;
    value:           string;
    defaultValue:    string;
    checked:         boolean;
    defaultChecked:  boolean;
    multiple?:       boolean;
    size?:           number;
    options?:        Option[];
}

/** A text field drawn with one value, and typed into to hold another. */
const text = (name: string, drawn: string, typed: string = drawn): Control =>
    ({ tagName: 'INPUT', name, type: 'text', value: typed, defaultValue: drawn, checked: false, defaultChecked: false });

/** A box drawn ticked or not, and left ticked or not. */
const box = (name: string, drawn: boolean, ticked: boolean = drawn): Control =>
    ({ tagName: 'INPUT', name, type: 'checkbox', value: 'on', defaultValue: 'on', checked: ticked, defaultChecked: drawn });

/** A list drawn with one choice, and left at another. */
const list = (name: string, values: string[], drawn: string, chosen: string = drawn): Control =>
    ({ tagName: 'SELECT', name, type: 'select-one', value: chosen, defaultValue: '', checked: false, defaultChecked: false,
       multiple: false, size: 0,
       options: values.map(value => ({ value, selected: value === chosen, defaultSelected: value === drawn })) });

const button = (): Control =>
    ({ tagName: 'BUTTON', name: '', type: 'submit', value: '', defaultValue: '', checked: false, defaultChecked: false });

const form = (id: string, controls: Control[]) =>
    ({ id,
       elements:          controls,
       querySelectorAll:  (selector: string) => selector === 'input, textarea, select'
                                                    ? controls.filter(control => control.tagName !== 'BUTTON')
                                                    : [] });

type Form = ReturnType<typeof form>;

/** A page, whose forms a draw replaces with new ones - as rendering it anew does. */
const page = (forms: Form[]) => {
    const it = { forms, querySelectorAll: (selector: string) => selector === 'form' ? it.forms : [] };
    return it;
};

const asRoot = (Page: ReturnType<typeof page> | Form) => Page as unknown as ParentNode;


describe('a page drawn anew with a draft on it', () => {

    it('keeps what is typed into a form other than the one saved', () => {

        const shown = page([ form('identity-form', [ text('name', 'EV', 'EV of the day'), button() ]),
                             form('battery-form',  [ text('soc',  '30', '31'),           button() ]) ]);

        keepDrafts(asRoot(shown), 'battery-form', () => {
            shown.forms = [ form('identity-form', [ text('name', 'EV'), button() ]),
                            form('battery-form',  [ text('soc',  '31'), button() ]) ];
        });

        assert.equal(shown.forms[0]!.elements[0]!.value, 'EV of the day', 'the name typed and not saved');
        assert.equal(typedSinceDrawn(asRoot(shown.forms[0]!)), true, 'and it still asks before it is left');
        assert.equal(shown.forms[1]!.elements[0]!.value, '31', 'the form saved is what the vehicle now has');
        assert.equal(typedSinceDrawn(asRoot(shown.forms[1]!)), false);

    });

    it('leaves a form nobody typed into as it is drawn, the vehicle\'s answer in it', () => {

        const shown = page([ form('interface-form', [ text('interface', 'eth0') ]),
                             form('settings-form',  [ text('maxRetries', '15') ]) ]);

        keepDrafts(asRoot(shown), null, () => {
            shown.forms = [ form('interface-form', [ text('interface', 'eth1') ]),
                            form('settings-form',  [ text('maxRetries', '15') ]) ];
        });

        assert.equal(shown.forms[0]!.elements[0]!.value, 'eth1');

    });

    it('keeps a box ticked and something else chosen from a list', () => {

        const shown = page([ form('link-form', [ box('renegotiate', false, true),
                                                 list('protocol', [ 'both', '20', '2' ], 'both', '20') ]),
                             form('goals-form', [ text('targetEnergyKWh', '') ]) ]);

        keepDrafts(asRoot(shown), 'goals-form', () => {
            shown.forms = [ form('link-form', [ box('renegotiate', false),
                                                list('protocol', [ 'both', '20', '2' ], 'both') ]),
                            form('goals-form', [ text('targetEnergyKWh', '42') ]) ];
        });

        const [ renegotiate, protocol ] = shown.forms[0]!.elements;

        assert.equal(renegotiate!.checked, true);
        assert.deepEqual(protocol!.options!.filter(option => option.selected).map(option => option.value), [ '20' ]);
        assert.equal(typedSinceDrawn(asRoot(shown.forms[0]!)), true);

    });

    it('leaves a form drawn with other controls as drawn, where what went where is no longer certain', () => {

        const shown = page([ form('servers-form', [ text('address', '192.0.2.1', '192.0.2.9') ]) ]);

        keepDrafts(asRoot(shown), null, () => {
            shown.forms = [ form('servers-form', [ text('address', '192.0.2.1'), text('address', '192.0.2.2') ]) ];
        });

        assert.deepEqual(shown.forms[0]!.elements.map(control => control.value), [ '192.0.2.1', '192.0.2.2' ]);

    });

    it('holds no draft of a form that was only drawn, or of the one saved', () => {

        const shown = page([ form('identity-form', [ text('name', 'EV') ]),
                             form('battery-form',  [ text('soc', '30', '31') ]) ]);

        assert.deepEqual([ ...draftsIn(asRoot(shown), 'battery-form').keys() ], []);
        assert.deepEqual([ ...draftsIn(asRoot(shown), null).keys() ], [ 'battery-form' ]);

    });

});
