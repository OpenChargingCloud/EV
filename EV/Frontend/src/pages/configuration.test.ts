/**
 * The Libraries card of the Configuration page: a line per repository the
 * vehicle was built from, as the node sends them - with an assembly named
 * only where two lines share a repository's name, as the banner does it.
 *
 * The page's modules read the base they are served under from the page when
 * they are loaded, so there is a page before they are imported.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

(globalThis as unknown as { document: unknown }).document = {
    querySelector: () => null
};

const { libraries } = await import('./configuration.ts');


const commit = (Character: string) => Character.repeat(40);


describe('the Libraries card', () => {

    it('names no assembly where every repository has a name of its own', () => {

        const card = libraries([
            { name: 'EV',             assembly: 'EV',                                          version: '0.1.0',  commit: commit('a') },
            { name: 'Hermod',         assembly: 'org.GraphDefined.Vanaheimr.Hermod',           version: '1.0.0',  commit: commit('b') },
            { name: 'WWCP_ISO15118',  assembly: 'cloud.charging.open.protocols.ISO15118.SDP',  version: '1.0.0',  commit: commit('c') }
        ]).value;

        assert.ok(!card.includes('SDP'),        'one assembly of WWCP_ISO15118 reads as if it were all of it');
        assert.ok(!card.includes('Vanaheimr'),  'nor is an assembly named beside Hermod');

        for (const [ name, character ] of [ [ 'EV', 'a' ], [ 'Hermod', 'b' ], [ 'WWCP_ISO15118', 'c' ] ] as const)
            assert.ok(card.includes(`<span class="k">${name}</span>`) &&
                      card.includes(`<span class="muted small commit">${commit(character)}</span>`),
                      `${name} is not there with its whole commit`);

    });

    it('names the assembly beside each of two repositories of one name', () => {

        const card = libraries([
            { name: 'ModbusTLSEnergyMeter',  assembly: 'ModbusTLSEnergyMeterCLI',            version: '1.0.0',  commit: commit('d') },
            { name: 'ModbusTLSEnergyMeter',  assembly: 'ModbusTLSEnergyMeter',               version: '1.0.0',  commit: commit('e') },
            { name: 'Hermod',                assembly: 'org.GraphDefined.Vanaheimr.Hermod',  version: '1.0.0',  commit: commit('b') }
        ]).value;

        assert.ok(card.includes('<span class="muted small">ModbusTLSEnergyMeterCLI</span>'),  'the tool is not told apart from its library');
        assert.ok(card.includes('<span class="muted small">ModbusTLSEnergyMeter</span>'),     'the library is not told apart from its tool');
        assert.ok(!card.includes('Vanaheimr'),                                               'an assembly is named beside a repository whose name is its own');

    });

    it('writes no commit where none was sent', () => {

        const card = libraries([ { name: 'Norn', assembly: 'org.GraphDefined.Vanaheimr.Norn', version: '1.0.0' } ]).value;

        assert.ok(card.includes('<span class="k">Norn</span>'));
        assert.ok(!card.includes('commit'), 'an empty commit reads like an answer, and it is not one');

    });

});
