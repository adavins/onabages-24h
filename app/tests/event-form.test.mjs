// Proves de la lògica del formulari d'esdeveniments (fase 2, pas 8b). S'executen amb «npm test» (dins d'app\).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  newEvent, cloneEvent, eventToSave, sameEvent, duplicateEvent, normalizeTime, minutesToSeconds, secondsToMinutes,
  intOrNull, numberOrNull, parseHours, formatHours, parseMonthDays, formatMonthDays, setDay, moveItem, withMode, quickCheck,
  hasCustomOptions, initialSections, optionsSummary,
} from '../renderer/js/event-form.js';

test('newEvent: un TALLA cada dia que esborra la cua', () => {
  const ev = newEvent();
  assert.equal(ev.id, 0);
  assert.equal(ev.mode, 'TALLA');
  assert.equal(ev.daysMask, 127);
  assert.equal(ev.clearQueue, true);
  assert.equal(ev.enabled, true);
});

test('eventToSave: treu el que només és per mostrar', () => {
  const ev = { ...newEvent(), name: '  Magazín ', modified: '2026-11-11T10:00:00Z', excludedDates: ['2027-01-01', '2026-12-25'],
    actions: [{ type: 'PISTA', params: { songId: 3 }, label: 'El Magazín', detail: 'Programa', missing: false, durationMs: 1000 }] };
  const saved = eventToSave(ev);
  assert.equal(saved.name, 'Magazín');
  assert.equal('modified' in saved, false);
  assert.deepEqual(saved.actions, [{ type: 'PISTA', params: { songId: 3 }, daysMask: null }]);
  assert.deepEqual(saved.excludedDates, ['2026-12-25', '2027-01-01']);
  assert.equal(ev.actions[0].label, 'El Magazín'); // l'original no es toca
});

test('sameEvent: només compta el que es desa', () => {
  const a = { ...newEvent(), name: 'A', actions: [{ type: 'PISTA', params: { songId: 3 }, label: 'x' }] };
  const b = cloneEvent(a);
  b.actions[0].label = 'un altre text';
  b.modified = 'ara';
  assert.equal(sameEvent(a, b), true);
  b.startTime = '13:00:00';
  assert.equal(sameEvent(a, b), false);
});

test('duplicateEvent: un de nou amb el mateix contingut', () => {
  const copy = duplicateEvent({ ...newEvent(), id: 12, name: 'Magazín', notes: 'Importat de RadioDJ (franges 1).' });
  assert.equal(copy.id, 0);
  assert.equal(copy.name, 'Magazín (còpia)');
  assert.equal(copy.notes, null);
});

test('normalizeTime, minuts i nombres', () => {
  assert.equal(normalizeTime('10:30'), '10:30:00');
  assert.equal(normalizeTime('10:30:15'), '10:30:15');
  assert.equal(normalizeTime(''), '');
  assert.equal(minutesToSeconds('60'), 3600);
  assert.equal(minutesToSeconds('1,5'), 90);
  assert.equal(minutesToSeconds(''), null);
  assert.equal(minutesToSeconds('abc'), null);
  assert.equal(secondsToMinutes(3600), '60');
  assert.equal(secondsToMinutes(90), '1,5');
  assert.equal(secondsToMinutes(null), '');
  assert.equal(intOrNull('50'), 50);
  assert.equal(intOrNull('5,5'), null);
  assert.equal(intOrNull(''), null);
  assert.equal(numberOrNull('-12,5'), -12.5);
  assert.equal(numberOrNull(''), null);
});

test('parseHours i formatHours', () => {
  assert.equal(parseHours(''), null);
  assert.equal(parseHours('0-23'), null);
  assert.equal(parseHours('7-23'), 0xFF_FF80);
  assert.equal(parseHours('9, 12, 18'), (1 << 9) | (1 << 12) | (1 << 18));
  assert.equal(parseHours('9h'), 1 << 9);
  assert.equal(parseHours('25'), undefined);
  assert.equal(parseHours('nou'), undefined);
  assert.equal(formatHours(null), '');
  assert.equal(formatHours(0xFF_FF80), '7-23');
  assert.equal(formatHours((1 << 0) | (1 << 1) | (1 << 2) | (1 << 22)), '0-2, 22');
  assert.equal(parseHours(formatHours(0b1010_0000_0111)), 0b1010_0000_0111);
});

test('parseMonthDays i formatMonthDays', () => {
  assert.deepEqual(parseMonthDays('15, 1, últim'), [1, 15, -1]);
  assert.deepEqual(parseMonthDays("l'ultim"), [-1]);
  assert.deepEqual(parseMonthDays('1, 1'), [1]);
  assert.equal(parseMonthDays('32'), undefined);
  assert.equal(formatMonthDays([1, 15, -1]), '1, 15, últim');
});

test('setDay i moveItem', () => {
  assert.equal(setDay(0, 4, true), 16);
  assert.equal(setDay(127, 6, false), 63);
  assert.deepEqual(moveItem(['a', 'b', 'c'], 0, 1), ['b', 'a', 'c']);
  assert.deepEqual(moveItem(['a', 'b', 'c'], 2, -1), ['a', 'c', 'b']);
  assert.deepEqual(moveItem(['a', 'b', 'c'], 0, -1), ['a', 'b', 'c']);
});

test('withMode: «Esborra la cua» segueix el mode (D-022)', () => {
  const talla = { ...newEvent(), fadeMs: 2000 };
  const encua = withMode(talla, 'ENCUA');
  assert.equal(encua.clearQueue, false);
  assert.equal(encua.fadeMs, null);
  assert.equal(withMode(encua, 'TALLA').clearQueue, true);
});

test('quickCheck: el primer problema, o null', () => {
  const ok = { ...newEvent(), name: 'Magazín', actions: [{ type: 'PISTA', params: { songId: 1 } }] };
  assert.equal(quickCheck(ok), null);
  assert.match(quickCheck({ ...ok, actions: [] }) ?? '', /alguna acció/);
  assert.match(quickCheck({ ...ok, actions: [{ type: 'STREAM', params: { songId: 2 }, label: 'Directe' }] }) ?? '', /durada del stream «Directe»/);
  assert.equal(quickCheck({ ...ok, actions: [{ type: 'STREAM', params: { songId: 2, durada_seg: 3600 } }] }), null);
  assert.equal(quickCheck({ ...ok, name: '  ' }), 'Cal posar-li un nom.');
  assert.equal(quickCheck({ ...ok, recurrence: 'DATES' }), 'Cal afegir alguna data.');
  assert.match(quickCheck({ ...ok, startDate: '2027-01-01', endDate: '2026-01-01' }) ?? '', /anterior/);
  assert.match(quickCheck({ ...ok, mode: 'SUPERPOSA' }) ?? '', /Auxiliar/);
  assert.equal(quickCheck({ ...ok, mode: 'SUPERPOSA', actions: [{ type: 'AUXILIAR', params: { songId: 1 } }] }), null);
});

test('initialSections: un de nou només obre General; un de desat, les omplertes', () => {
  const ev = newEvent();
  assert.deepEqual([...initialSections(ev, true)], ['general']);
  assert.deepEqual([...initialSections(ev, false)], ['general', 'when']);
  const full = { ...ev, priority: 80, actions: [{ type: 'PISTA' }] };
  assert.deepEqual([...initialSections(full, false)], ['general', 'when', 'options', 'actions']);
  // Un ENCUA que no esborra la cua té les opcions per defecte
  assert.equal(hasCustomOptions(withMode(ev, 'ENCUA')), false);
  assert.equal(hasCustomOptions({ ...withMode(ev, 'ENCUA'), clearQueue: true }), true);
});

test('optionsSummary: el resum de les opcions', () => {
  assert.equal(optionsSummary(newEvent()), 'esborra la cua');
  assert.equal(optionsSummary({ ...newEvent(), fadeMs: 0, priority: 70 }), 'esborra la cua · tall sec · prioritat 70');
  assert.equal(optionsSummary(withMode(newEvent(), 'SISTEMA')), 'per defecte');
});
