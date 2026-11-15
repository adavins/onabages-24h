// Proves dels textos dels avisos de vigència (fase 4, pas 6b; D-146): app/renderer/js/validity-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { kindInfo, subjectText, barText, warningsFor, removedText } from '../renderer/js/validity-form.js';

/** @param {string} kind @param {'track' | 'campaign'} subject @param {number} id */
const w = (kind, subject = 'track', id = 1) => ({ kind, subject, id, name: 'X', text: '', events: [], canRemove: kind === 'CADUCADA' });

test('kindInfo: cada tipus amb text, color i icona', () => {
  assert.equal(kindInfo('CADUCADA').label, 'Caducada');
  assert.equal(kindInfo('NO_SONARA').className, 'text-danger');
  assert.equal(kindInfo('COMENCA_AVIAT').label, 'Comença aviat');
  assert.equal(kindInfo('NOU').label, 'NOU'); // un tipus que encara no coneix la interfície
  assert.equal(subjectText(w('CADUCADA', 'campaign')), 'Campanya');
});

test('barText: res si no n\'hi ha; greu si alguna és caducada o no sonarà', () => {
  assert.equal(barText([]), null);
  assert.deepEqual(barText([w('COMENCA_AVIAT')]), { text: '1 avís de vigència', serious: false });
  assert.deepEqual(barText([w('COMENCA_AVIAT'), w('NO_SONARA')]), { text: '2 avisos de vigència', serious: true });
});

test('warningsFor: només els d\'aquella pista o campanya', () => {
  const list = [w('CADUCADA', 'track', 1), w('NO_SONARA', 'campaign', 1), w('COMENCA_AVIAT', 'track', 2)];
  assert.deepEqual(warningsFor(list, 'track', 1).map((x) => x.kind), ['CADUCADA']);
  assert.deepEqual(warningsFor(list, 'campaign', 1).map((x) => x.kind), ['NO_SONARA']);
});

test('removedText: el resum de treure-la dels esdeveniments', () => {
  assert.equal(removedText({ changed: 2, deleted: 1, kept: [] }),
    'S\'ha tret de 2 esdeveniments. S\'ha esborrat 1 bloc de falques que s\'havia quedat buit.');
  assert.equal(removedText({ changed: 0, deleted: 0, kept: [{ id: 3, name: 'Tall de les 9' }] }),
    'No s\'ha pogut treure de «Tall de les 9»: és l\'única acció de l\'esdeveniment. Canvia-ho o esborra\'l al Planificador.');
  assert.equal(removedText({ changed: 0, deleted: 0, kept: [] }), 'No era a cap esdeveniment actiu.');
});
