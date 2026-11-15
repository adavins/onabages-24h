// Proves dels textos del paquet de campanya (fase 4, pas 7a; D-150): app/renderer/js/campaign-package-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { packageFileName, spotStatusInfo, exportSummary, importPlan } from '../renderer/js/campaign-package-form.js';

test('packageFileName: sense caràcters que Windows no accepta', () => {
  assert.equal(packageFileName('Ferreteria Puig'), 'Ferreteria Puig.json');
  assert.equal(packageFileName('  Bar: "La Plaça" / 2026 '), 'Bar La Plaça 2026.json');
  assert.equal(packageFileName(''), 'Campanya.json');
});

test('spotStatusInfo: color + icona + text', () => {
  assert.equal(spotStatusInfo('existing').className, 'text-success');
  assert.equal(spotStatusInfo('missing').label, 'No es troba: no hi entrarà');
  assert.equal(spotStatusInfo('nou').label, 'nou');
});

test('exportSummary: avisa de les falques que no són a la carpeta', () => {
  assert.deepEqual(exportSummary({ spots: 2, cells: 4, notInFolder: [] }),
    { summary: 'S\'ha desat el paquet amb 2 falques i el calendari (4 caselles).', warning: null });
  assert.match(/** @type {string} */ (exportSummary({ spots: 1, cells: 1, notInFolder: ['a.mp3'] }).warning),
    /^Aquesta falca no és a la mateixa carpeta que el paquet: a\.mp3\. Copia-la-hi/);
});

test('importPlan: què es farà', () => {
  const plan = importPlan({ spots: [{ status: 'existing' }, { status: 'new' }, { status: 'missing' }], calendar: { cells: 3 } });
  assert.deepEqual([plan.found, plan.created, plan.missing], [2, 1, 1]);
  assert.equal(plan.text, 'Es crearà la campanya en esborrany amb 2 falques, es crearà 1 pista nova, es marcaran 3 caselles del calendari. 1 falca no hi entrarà: mira\'n el motiu a la llista.');
  assert.equal(importPlan({ spots: [{ status: 'existing' }], calendar: { cells: 0 } }).text,
    'Es crearà la campanya en esborrany amb 1 falca, no hi ha cap casella del calendari per marcar.');
});
