// Proves dels avisos de la barra de sota (fase 4, pas 6b; D-146): app/renderer/js/warnings-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { kindInfo, subjectText, allWarnings, barState, warningsFor, removedText } from '../renderer/js/warnings-form.js';

/** @param {string} kind @param {'track' | 'campaign'} subject @param {number} id */
const w = (kind, subject = 'track', id = 1) => ({ kind, subject, id, name: 'X', text: '', events: [], canRemove: kind === 'CADUCADA' });

test('kindInfo: «Comença aviat» és informació; «No sonarà», greu', () => {
  assert.deepEqual([kindInfo('COMENCA_AVIAT').level, kindInfo('COMENCA_AVIAT').className], ['info', 'text-info']);
  assert.equal(kindInfo('NO_SONARA').level, 'danger');
  assert.equal(kindInfo('CADUCADA').level, 'warning');
  assert.equal(kindInfo('NOU').label, 'NOU'); // un tipus que encara no coneix la interfície
  assert.equal(subjectText(w('CADUCADA', 'campaign')), 'Campanya');
});

test('allWarnings: tots junts, per gravetat', () => {
  const items = allWarnings(
    [{ level: 'warning', text: 'AutoDJ' }, { level: 'danger', text: 'Sortida' }],
    [w('COMENCA_AVIAT'), w('CADUCADA'), w('NO_SONARA')],
  );
  assert.deepEqual(items.map((i) => ('general' in i ? i.general.text : i.validity.kind)),
    ['Sortida', 'NO_SONARA', 'AutoDJ', 'CADUCADA', 'COMENCA_AVIAT']);
});

test('barState: «Cap avís» o «N avisos» amb el color del més greu', () => {
  assert.equal(barState([]).text, 'Cap avís');
  assert.deepEqual(barState(allWarnings([], [w('COMENCA_AVIAT')])), { text: '1 avís', className: 'text-info', icon: 'bi-info-circle-fill' });
  assert.equal(barState(allWarnings([{ level: 'warning', text: 'a' }], [w('COMENCA_AVIAT')])).className, 'text-warning');
  assert.equal(barState(allWarnings([], [w('COMENCA_AVIAT'), w('NO_SONARA')])).text, '2 avisos');
  assert.equal(barState(allWarnings([], [w('COMENCA_AVIAT'), w('NO_SONARA')])).className, 'text-danger');
});

test('warningsFor: només els d\'aquella pista o campanya', () => {
  const list = [w('CADUCADA', 'track', 1), w('NO_SONARA', 'campaign', 1), w('COMENCA_AVIAT', 'track', 2)];
  assert.deepEqual(warningsFor(list, 'track', 1).map((x) => x.kind), ['CADUCADA']);
  assert.deepEqual(warningsFor(list, 'campaign', 1).map((x) => x.kind), ['NO_SONARA']);
});

test('removedText: el que s\'ha tret i els esdeveniments que només la tenien a ella', () => {
  assert.equal(removedText({ changed: 2, deleted: 1, kept: [] }),
    'S\'ha tret de 2 esdeveniments. S\'ha esborrat 1 bloc de falques que s\'havia quedat buit.');
  assert.equal(removedText({ changed: 0, deleted: 0, kept: [{ id: 3, name: 'Tall de les 9' }] }),
    'L\'esdeveniment «Tall de les 9» no s\'ha tocat: només feia sonar això.');
  assert.equal(removedText({ changed: 1, deleted: 0, kept: [{ id: 3, name: 'A' }, { id: 4, name: 'B' }] },
    { deleted: ['A'], failed: [{ name: 'B', message: 'No es pot.' }] }),
    'S\'ha tret d\'1 esdeveniment. S\'ha esborrat l\'esdeveniment «A». No s\'ha pogut esborrar «B»: No es pot.');
  assert.equal(removedText({ changed: 0, deleted: 0, kept: [] }), 'No era a cap esdeveniment actiu.');
});
