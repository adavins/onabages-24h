// Proves dels càlculs del planificador de la interfície (fase 2, pas 8a). S'executen amb «npm test» (dins d'app\).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  weekStart, addDays, isoDate, weekTitle, minutesOf, shortTime, daysText, hoursText, recurrenceText, durationText,
  layoutLanes, modeInfo, repeatsWithinDay,
} from '../renderer/js/schedule.js';

test('weekStart: el dilluns de la setmana, també des de diumenge', () => {
  assert.equal(isoDate(weekStart(new Date(2026, 10, 20, 15, 30))), '2026-11-16'); // divendres
  assert.equal(isoDate(weekStart(new Date(2026, 10, 22))), '2026-11-16');         // diumenge
  assert.equal(isoDate(weekStart(new Date(2026, 10, 16))), '2026-11-16');         // dilluns
});

test('addDays: dates de calendari, també amb el canvi d\'hora', () => {
  assert.equal(isoDate(addDays(new Date(2027, 2, 22), 7)), '2027-03-29'); // la setmana del canvi d'hora de primavera
  assert.equal(isoDate(addDays(new Date(2026, 9, 19), 7)), '2026-10-26'); // i la de tardor
});

test('weekTitle: dins d\'un mes, entre dos mesos i entre dos anys', () => {
  assert.equal(weekTitle(new Date(2026, 10, 16)), 'Setmana del 16 al 22 de novembre de 2026');
  assert.equal(weekTitle(new Date(2026, 10, 30)), 'Setmana del 30 de novembre al 6 de desembre de 2026');
  assert.equal(weekTitle(new Date(2027, 2, 29)), "Setmana del 29 de març al 4 d'abril de 2027");
  assert.equal(weekTitle(new Date(2026, 11, 28)), 'Setmana del 28 de desembre de 2026 al 3 de gener de 2027');
});

test('minutesOf i shortTime', () => {
  assert.equal(minutesOf('10:30:00'), 630);
  assert.equal(minutesOf('00:00:30'), 0.5);
  assert.equal(shortTime('10:30:00'), '10:30');
  assert.equal(shortTime('10:30:15'), '10:30:15');
});

test('daysText i hoursText', () => {
  assert.equal(daysText(127), 'cada dia');
  assert.equal(daysText(31), 'dl-dv');
  assert.equal(daysText(0b1010101), 'dl, dc, dv, dg');
  assert.equal(daysText(96), 'ds, dg');
  assert.equal(daysText(0), 'cap dia');
  assert.equal(hoursText(null), 'totes les hores');
  assert.equal(hoursText(0xFF_FF80), '7–23 h');
  assert.equal(hoursText((1 << 9) | (1 << 12) | (1 << 18)), '9, 12 i 18 h');
});

test('recurrenceText: les cinc recurrències i la repetició', () => {
  const base = { startTime: '10:00:00', repeat: 'CAP', daysMask: 16, ordinals: [], monthDays: [], includedDates: [] };
  assert.equal(recurrenceText({ ...base, recurrence: 'SETMANAL', daysMask: 127 }), 'Cada dia');
  assert.equal(recurrenceText({ ...base, recurrence: 'SETMANAL', daysMask: 31 }), 'Cada setmana · dl-dv');
  assert.equal(recurrenceText({ ...base, recurrence: 'CADA_N_SETMANES', intervalWeeks: 2, anchorDate: '2026-11-06' }),
    'Cada 2 setmanes · dv · des del 6/11/2026');
  assert.equal(recurrenceText({ ...base, recurrence: 'MENSUAL_ORDINAL', daysMask: 32, ordinals: [1, 3] }), 'El 1r i 3r dissabte de cada mes');
  assert.equal(recurrenceText({ ...base, recurrence: 'MENSUAL_ORDINAL', daysMask: 64, ordinals: [-1] }), "L'últim diumenge de cada mes");
  assert.equal(recurrenceText({ ...base, recurrence: 'DIES_MES', monthDays: [1, 15, -1] }), "Els dies 1, 15 i l'últim de cada mes");
  assert.equal(recurrenceText({ ...base, recurrence: 'DATES', includedDates: ['2026-12-24', '2026-12-31'] }), 'Només 24/12/2026 i 31/12/2026');
  assert.equal(recurrenceText({ ...base, recurrence: 'SETMANAL', daysMask: 127, repeat: 'CADA_HORA', startTime: '00:30:00', hoursMask: null }),
    'Cada dia · cada hora (totes les hores) al minut 30');
  assert.equal(recurrenceText({ ...base, recurrence: 'SETMANAL', daysMask: 31, startDate: '2026-11-01', endDate: '2027-06-30' }),
    'Cada setmana · dl-dv · del 1/11/2026 al 30/6/2027');
});

test('durationText', () => {
  assert.equal(durationText(3600), '1 h');
  assert.equal(durationText(5400), '1 h 30 min');
  assert.equal(durationText(1800), '30 min');
  assert.equal(durationText(null), '');
});

test('layoutLanes: els que coincideixen van en carrils diferents', () => {
  const placed = layoutLanes([
    { id: 'a', start: 420, end: 424 },   // 07:00, 4 min
    { id: 'b', start: 425, end: 430 },   // 07:05: no coincideix amb a
    { id: 'c', start: 540, end: 600 },   // 09:00–10:00
    { id: 'd', start: 570, end: 575 },   // 09:30: coincideix amb c
    { id: 'e', start: 600, end: 660 },   // 10:00: ja no
  ]);
  const by = Object.fromEntries(placed.map((p) => [p.id, p]));
  assert.deepEqual([by.a.lane, by.a.lanes, by.b.lane, by.b.lanes], [0, 1, 0, 1]);
  assert.deepEqual([by.c.lane, by.d.lane, by.c.lanes, by.d.lanes], [0, 1, 2, 2]);
  assert.deepEqual([by.e.lane, by.e.lanes], [0, 1]);
});

test('modeInfo i repeatsWithinDay', () => {
  assert.equal(modeInfo('TALLA').label, 'Talla');
  assert.equal(modeInfo('X').label, 'Sistema');
  assert.equal(repeatsWithinDay({ repeat: 'CADA_HORA' }), true);
  assert.equal(repeatsWithinDay({ repeat: 'CAP' }), false);
});
