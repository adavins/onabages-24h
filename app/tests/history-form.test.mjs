// Proves de la cerca a l'historial (fase 4, pas 5b; D-143): app/renderer/js/history-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  historyParams, periodProblem, localDateTime, completeState, summaryText, PAGE_SIZE, filtersText, historyFileName, historyCsv,
} from '../renderer/js/history-form.js';
import { typeName, originName, formatDuration } from '../renderer/js/format.js';

const none = { from: '', to: '', text: '', type: '', origin: '', track: null };

test('historyParams: només els filtres que hi ha; la primera pàgina demana el total', () => {
  assert.deepEqual(historyParams(none), { limit: PAGE_SIZE, count: true });
  assert.deepEqual(historyParams({ from: '2026-11-01', to: '2026-11-15', text: '  macy ', type: 'commercial', origin: 'manual', track: { id: 7, name: 'X' } }), {
    limit: PAGE_SIZE, from: '2026-11-01', to: '2026-11-15', search: 'macy', types: ['commercial'], origins: ['manual'], trackId: 7, count: true,
  });
  // La pàgina següent: després de l'última, sense tornar a comptar
  assert.deepEqual(historyParams(none, 812), { limit: PAGE_SIZE, beforeId: 812 });
});

test('periodProblem: «Des de» no pot ser posterior a «Fins a»', () => {
  assert.equal(periodProblem({ ...none, from: '2026-11-15', to: '2026-11-15' }), null);
  assert.equal(periodProblem({ ...none, from: '2026-11-15' }), null);
  assert.match(periodProblem({ ...none, from: '2026-11-16', to: '2026-11-15' }) ?? '', /posterior/);
});

test('localDateTime: el dia i l\'hora en hora local', () => {
  const at = new Date(2026, 10, 5, 9, 4, 7); // 5/11/2026 9:04:07 local
  assert.deepEqual(localDateTime(at.toISOString()), { date: '5/11/2026', time: '09:04:07' });
});

test('completeState: sonant, sencera, tallada o no se sap, sempre amb icona i text', () => {
  assert.equal(completeState({ playedMs: null, complete: null }).text, 'Sonant');
  assert.equal(completeState({ playedMs: 200_000, complete: true }).text, 'Sí');
  assert.equal(completeState({ playedMs: 12_000, complete: false }).text, 'No');
  assert.equal(completeState({ playedMs: 0, complete: false }).text, 'No se sap');
  for (const s of [completeState({ playedMs: 12_000, complete: false }), completeState({ playedMs: null, complete: null })]) assert.ok(s.icon && s.className);
});

test('summaryText', () => {
  assert.equal(summaryText(1, 1), '1 emissió');
  assert.equal(summaryText(3, null), '3 emissions');
  assert.equal(summaryText(200, 350), 'Se\'n mostren 200 de 350 emissions');
});

test('filtersText i historyFileName (pas 5c)', () => {
  const f = { from: '2026-11-01', to: '2026-11-15', text: ' fira ', type: 'commercial', origin: 'manual', track: { id: 1, name: 'Ferreteria: Falca/1' } };
  assert.equal(filtersText(f, typeName, originName), 'Del 1/11/2026 al 15/11/2026 · Pista: Ferreteria: Falca/1 · Text: «fira» · Tipus: Falca · Origen: Manual');
  assert.equal(filtersText({ ...none, from: '2026-11-15', to: '2026-11-15' }, typeName, originName), 'El 15/11/2026');
  assert.equal(filtersText(none, typeName, originName), 'Totes les dates');
  assert.equal(historyFileName(f, 'csv'), 'Historial - Ferreteria Falca1 - del 2026-11-01 al 2026-11-15.csv');
  assert.equal(historyFileName(none, 'pdf'), 'Historial - totes les dates.pdf');
});

test('historyCsv: punt i coma, cometes quan cal i salts de línia de Windows', () => {
  const at = new Date(2026, 10, 5, 9, 4, 7).toISOString();
  const csv = historyCsv([
    { startedAt: at, artist: 'Sopa; de Cabra', title: 'L\'"Empordà"', type: 'music', origin: 'autodj', campaignName: null, playedMs: 214_350, complete: true, path: 'O:\\m.mp3' },
    { startedAt: at, artist: 'Ferreteria', title: 'Falca', type: 'commercial', origin: 'event', campaignName: 'Nadal', playedMs: null, complete: null, path: '' },
  ], typeName, originName, formatDuration);
  const lines = csv.split('\r\n');
  assert.equal(lines[0], 'Data;Hora;Artista;Títol;Tipus;Origen;Campanya;Ha sonat;Segons;Sencera;Fitxer');
  assert.equal(lines[1], `5/11/2026;09:04:07;"Sopa; de Cabra";"L'""Empordà""";Música;AutoDJ;;${formatDuration(214_350)};214;Sí;O:\\m.mp3`);
  assert.equal(lines[2], '5/11/2026;09:04:07;Ferreteria;Falca;Falca;Esdeveniment;Nadal;;;Sonant;');
  assert.equal(lines[3], ''); // acaba amb un salt de línia
});
