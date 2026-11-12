// Proves dels textos de les carpetes vigilades (fase 3, pas 7c-2). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { folderWarnings, folderAction } from '../renderer/js/folders-text.js';

test('avisos: les carpetes amb problemes i les que tenen fitxers que falten', () => {
  const folders = [
    { name: 'Music / Actualitat', problem: 'La carpeta surt buida (potser no està disponible): no s\'hi fa res.', missing: 3 },
    { name: 'Music / Adult 5', problem: null, missing: 1 },
    { name: 'Music / Adult 9', problem: null, missing: 0 },
  ];
  assert.deepEqual(folderWarnings(folders), [
    'Carpeta vigilada de «Music / Actualitat»: La carpeta surt buida (potser no està disponible): no s\'hi fa res.',
    'Carpeta vigilada de «Music / Adult 5»: 1 pista té el fitxer que falta',
  ]);
  assert.deepEqual(folderWarnings([]), []);
  assert.deepEqual(folderWarnings(null), []);
});

test('accions del registre amb text, icona i color', () => {
  assert.equal(folderAction('imported').text, 'Importada');
  assert.equal(folderAction('missing').css, 'text-warning');
  assert.equal(folderAction('desconeguda').text, 'desconeguda');
});
