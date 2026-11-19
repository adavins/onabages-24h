// Proves dels textos de les còpies de seguretat (fase 4, pas 7b; D-152): app/renderer/js/backups-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { backupDateText, kindText, sizeText, inspectLines, restoredText } from '../renderer/js/backups-form.js';

test('dates, tipus i mides', () => {
  const local = new Date(2026, 10, 12, 3, 5, 0).toISOString();
  assert.equal(backupDateText(local), '12/11/2026 03:05');
  assert.equal(kindText('safety'), 'Abans de restaurar');
  assert.equal(kindText('auto'), 'Diària o feta a mà');
  assert.equal(kindText('maintenance'), 'Abans del manteniment');
  assert.equal(sizeText(850 * 1024), '850 kB');
  assert.equal(sizeText(13_000_000), '12,4 MB');
  assert.equal(sizeText(null), '—');
});

test('inspectLines: què hi ha a la còpia', () => {
  const lines = inspectLines({ version: 7, currentVersion: 8, tracks: 1, events: 84, rotations: 3, campaigns: 0, lastPlayed: null });
  assert.equal(lines[0], '1 pista, 84 esdeveniments, 3 rotacions i 0 campanyes.');
  assert.equal(lines[1], 'L\'historial és buit.');
  assert.match(lines[2], /versió anterior/);
  assert.equal(inspectLines({ version: 8, currentVersion: 8, tracks: 2, events: 1, rotations: 1, campaigns: 1, lastPlayed: null }).length, 2);
});

test('restoredText: el resultat i com desfer-ho', () => {
  const text = restoredText({ safety: 'D:\dades\copies\onabages24-abans-de-restaurar-2026-11-15_120000.db', keptHistory: 3, keptExecutions: 1 }, true);
  assert.match(text, /S'han conservat 3 entrades de l'historial i 1 línia del registre/);
  assert.match(text, /onabages24-abans-de-restaurar-2026-11-15_120000\.db$/);
  assert.doesNotMatch(restoredText({ safety: 'x.db', keptHistory: 0, keptExecutions: 0 }, false), /historial/);
});
