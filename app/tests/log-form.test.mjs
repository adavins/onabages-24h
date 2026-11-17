// Proves dels textos de la pestanya «Registre» (D-167): app/renderer/js/log-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { typeText, stateText, entryText, logParams, countText, sourceName, eventResultText, DEFAULT_SOURCES } from '../renderer/js/log-form.js';
import { areaPrefixes } from '../renderer/js/changes-form.js';

const entry = (over) => ({ source: 'incident', id: 1, at: '2026-11-17T12:00:00Z', endedAt: null, open: false, kind: 'SORTIDA',
  severity: 'danger', text: 'Sortida d\'emergència', detail: null, recovered: false, userId: null, user: '', ...over });

test('el tipus i el text de cada origen', () => {
  assert.equal(typeText(entry({})), 'Sortida d\'àudio');
  assert.equal(typeText(entry({ source: 'change', kind: 'scheduler.save' })), 'Esdeveniments i calendaris');
  assert.equal(typeText(entry({ source: 'session', kind: 'session.login' })), 'Usuaris i sessions');
  assert.equal(typeText(entry({ source: 'event', kind: 'OK' })), 'Planificador');
  assert.equal(entryText(entry({ source: 'event', text: 'Butlletí' })), 'Esdeveniment «Butlletí»');
  assert.equal(sourceName('session'), 'Sessió');
  assert.equal(eventResultText('OMES_TARD'), 'Omès: el motor hi va arribar tard');
  assert.deepEqual(DEFAULT_SOURCES, ['incident', 'change', 'session']);
});

test('l\'estat: la incidència amb la durada, l\'esdeveniment amb el resultat, el canvi sense', () => {
  const now = new Date('2026-11-17T12:05:00Z');
  assert.equal(stateText(entry({ open: true }), now).text, 'Oberta (fa 5 min)');
  assert.equal(stateText(entry({ endedAt: '2026-11-17T12:00:40Z' }), now).text, 'Resolta (40 s)');
  assert.equal(stateText(entry({ source: 'event', kind: 'OK', severity: 'info' }), now).text, 'Executat');
  assert.equal(stateText(entry({ source: 'event', kind: 'ERROR', severity: 'danger' }), now).className, 'text-danger');
  assert.equal(stateText(entry({ source: 'change' }), now), null);
});

test('els paràmetres de log.list', () => {
  const today = new Date(2026, 10, 17);
  const base = { period: '7', sources: ['incident'], severity: '', type: '', userId: '', text: '', openOnly: false };
  assert.deepEqual(logParams(base, today, areaPrefixes), { sources: ['incident'], from: '2026-11-11' });
  assert.deepEqual(logParams({ ...base, openOnly: true, severity: 'danger' }, today, areaPrefixes), { sources: ['incident'], openOnly: true, minSeverity: 'danger' });
  assert.deepEqual(logParams({ ...base, type: 'incident:FITXER', text: ' a.mp3 ' }, today, areaPrefixes).kinds, ['FITXER']);
  assert.deepEqual(logParams({ ...base, type: 'change:campanyes', userId: '3' }, today, areaPrefixes),
    { sources: ['incident'], from: '2026-11-11', commands: ['campaigns.', 'season.'], userId: 3 });
  assert.equal(countText(1), '1 entrada');
  assert.equal(countText(2500), '2.500 entrades');
});
