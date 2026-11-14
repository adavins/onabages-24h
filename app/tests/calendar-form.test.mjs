// Proves del calendari d'una pista (fase 4, pas 2b; D-104): textos i comprovacions, sense pàgina.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cellWarnings, cellTitle, blockTimeProblem, normalizeBlockTime, dateText, monthText, passes, periodText, withExtraRows, trackOnlyRows, contentTimes } from '../renderer/js/calendar-form.js';

const cell = { eventId: 1, name: 'Magazín', mode: 'TALLA', enabled: true, commercialBlock: false, repeats: false, nonWeekly: false, marked: false, busyBy: null, others: [] };

test('cellWarnings: cap avís en una casella normal i tots els que toquen', () => {
  assert.deepEqual(cellWarnings(cell), []);
  const all = cellWarnings({ ...cell, enabled: false, busyBy: 'El Magazín', others: ['Promo'], repeats: true, nonWeekly: true });
  assert.equal(all.length, 5);
  assert.match(all[0], /desactivat/);
  assert.match(all[1], /«El Magazín»/);
  assert.match(all[2], /«Promo»/);
});

test('cellWarnings: una data solta diu quina és', () => {
  const list = cellWarnings({ ...cell, nonWeekly: true, dates: ['2026-11-14'] });
  assert.deepEqual(list, ['Només sona en dates soltes: 14/11/2026.']);
});

test('cellTitle: dia, hora, nom i què fa el clic', () => {
  const title = cellTitle({ ...cell, marked: true, commercialBlock: true }, 2, '08:35:00');
  assert.match(title, /^dimecres 08:35 · Magazín \(bloc de falques\)/);
  assert.match(title, /treure-la/);
});

test('blockTimeProblem: HH:MM en múltiples de 5 minuts', () => {
  assert.equal(blockTimeProblem('08:05'), null);
  assert.equal(blockTimeProblem('8:35'), null);
  assert.match(blockTimeProblem('08:07') ?? '', /múltiples de 5/);
  assert.match(blockTimeProblem('25:00') ?? '', /no existeix/);
  assert.match(blockTimeProblem('vuit') ?? '', /HH:MM/);
  assert.equal(normalizeBlockTime('8:5'), '08:05');
});

test('withExtraRows: totes les hores en punt, les provisionals, en ordre i sense repetir les que ja hi són', () => {
  const rows = [{ time: '09:00:00', days: [cell, null, null, null, null, null, null] }, { time: '08:30:00', days: [cell, null, null, null, null, null, null] }];
  const all = withExtraRows(rows, ['08:05:00', '09:00:00', '10:00:00']);
  assert.equal(all.length, 24 + 2); // 24 hores en punt (les 9 ja hi eren) + 08:30 + 08:05
  assert.deepEqual(all.slice(8, 12).map((r) => r.time), ['08:00:00', '08:05:00', '08:30:00', '09:00:00']);
  assert.equal(all[0].time, '00:00:00');
  assert.equal(all[0].provisional, undefined); // una hora en punt buida no és provisional
  assert.equal(all[9].provisional, true);
  assert.deepEqual(all[9].days, [null, null, null, null, null, null, null]);
  assert.equal(all[11].days[0], cell); // la de calendar.get es manté
  assert.equal(all.find((r) => r.time === '10:00:00')?.provisional, undefined);
});

test('trackOnlyRows: només les hores i els dies on sona la pista', () => {
  const marked = { ...cell, marked: true };
  const rows = [
    { time: '08:30:00', days: [cell, cell, null, null, null, null, null] },   // no hi és cap dia
    { time: '09:00:00', days: [marked, cell, marked, null, null, null, null] },
  ];
  assert.deepEqual(trackOnlyRows(rows), [{ time: '09:00:00', days: [true, false, true, false, false, false, false] }]);
});

test('contentTimes: hores previstes sumant les durades, i desconegudes després d\'una durada que no se sap', () => {
  const items = [
    { type: 'ROTACIO', durationMs: null, missing: false },
    { type: 'PISTA', durationMs: 20_000, missing: false },
    { type: 'PISTA', durationMs: 30_000, missing: true },  // falta: no sona
    { type: 'PISTA', durationMs: 45_500, missing: false },
    { type: 'STREAM', durationMs: null, missing: false },
    { type: 'PISTA', durationMs: 10_000, missing: false },
  ];
  assert.deepEqual(contentTimes(items, '10:59:50'), {
    starts: [null, '10:59:50', '11:00:10', '11:00:10', '11:00:56', null],
    end: null,
  });
  assert.deepEqual(contentTimes(items.slice(0, 4), '23:59:30'), { starts: [null, '23:59:30', '23:59:50', '23:59:50'], end: '00:00:36' });
});

test('dates, mesos i passis', () => {
  assert.equal(dateText('2026-11-16'), '16/11/2026');
  assert.equal(dateText(null), '');
  assert.equal(monthText('2026-03'), 'març 2026');
  assert.equal(passes(1), '1 passi');
  assert.equal(passes(3), '3 passis');
  assert.match(periodText({ from: '2026-11-01', to: '2026-11-30', periodFromTrack: true }), /^del 01\/11\/2026 al 30\/11\/2026 \(les dates/);
  assert.match(periodText({ from: '2026-11-12', to: '2026-12-09', periodFromTrack: false }), /4 setmanes/);
});

test('calendari d\'una campanya (pas 3d): els textos diuen «campanya» i la campanya ocupa temps al contingut', () => {
  const cell = { name: 'Bloc de falques 10:50', commercialBlock: true, marked: false, enabled: true, others: [], dates: [] };
  assert.match(cellTitle(cell, 0, '10:50:00', 'campanya'), /posar-hi la campanya aquest dia/);
  assert.match(cellTitle(cell, 0, '10:50:00'), /posar-hi la pista aquest dia/);
  assert.match(periodText({ from: '2026-11-01', to: '2026-11-30', periodFromTrack: true }, 'campanya'), /les dates de la campanya/);
  const items = [
    { type: 'CAMPANYA', durationMs: 30_000, missing: false },
    { type: 'PISTA', durationMs: 20_000, missing: false },
  ];
  assert.deepEqual(contentTimes(items, '10:50:00'), { starts: ['10:50:00', '10:50:30'], end: '10:50:50' });
});
