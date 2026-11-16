// Proves dels textos de la finestra «Incidències» (fase 5, pas 1b; D-156). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  kindText, severityInfo, stateInfo, isMoment, spanText, durationText, periodFrom, countText, dateTimeText,
} from '../renderer/js/incidents-form.js';

/** @param {object} fields */
const incident = (fields) => ({
  id: 1, kind: 'SORTIDA', severity: 'danger', text: 'x', detail: null,
  startedAt: '2026-11-16T09:00:00.000Z', endedAt: null, open: true, recovered: false, ...fields,
});

test('tipus i gravetat, amb text (també els que no coneix)', () => {
  assert.equal(kindText('SORTIDA'), 'Sortida d\'àudio');
  assert.equal(kindText('NOU_TIPUS'), 'NOU_TIPUS');
  assert.deepEqual(severityInfo('danger'), { text: 'Greu', className: 'text-danger', icon: 'bi-exclamation-octagon-fill' });
  assert.equal(severityInfo('warning').text, 'Avís');
  assert.equal(severityInfo('info').text, 'Informació');
});

test('estat: oberta, resolta, recuperada sola o anotada', () => {
  assert.equal(stateInfo(incident({})).text, 'Oberta');
  const resolved = incident({ open: false, endedAt: '2026-11-16T09:05:00.000Z' });
  assert.equal(stateInfo(resolved).text, 'Resolta');
  assert.equal(isMoment(resolved), false);
  const moment = incident({ open: false, endedAt: '2026-11-16T09:00:00.000Z' });
  assert.equal(stateInfo(moment).text, 'Anotada');
  assert.equal(stateInfo({ ...moment, recovered: true }).text, 'Recuperada sola');
});

test('durada: el temps que ha durat, el que fa que és oberta o «—» si és d\'un moment', () => {
  assert.equal(spanText(45_000), '45 s');
  assert.equal(spanText(200_000), '3 min 20 s');
  assert.equal(spanText(7_500_000), '2 h 5 min');
  assert.equal(spanText(3 * 86_400_000 + 4 * 3_600_000), '3 d 4 h');
  const now = new Date('2026-11-16T09:03:00.000Z');
  assert.equal(durationText(incident({}), now), 'fa 3 min');
  assert.equal(durationText(incident({ open: false, endedAt: '2026-11-16T09:00:30.000Z' }), now), '30 s');
  assert.equal(durationText(incident({ open: false, endedAt: '2026-11-16T09:00:00.000Z' }), now), '—');
});

test('període: el primer dia, en dies de calendari', () => {
  const today = new Date(2026, 10, 16, 15, 30);
  assert.equal(periodFrom('1', today), '2026-11-16');
  assert.equal(periodFrom('7', today), '2026-11-10');
  assert.equal(periodFrom('30', today), '2026-10-18');
  assert.equal(periodFrom('365', today), '2025-11-17');
});

test('comptes i hores', () => {
  assert.equal(countText(0), 'Cap incidència');
  assert.equal(countText(1), '1 incidència');
  assert.match(countText(1234), /^1.234 incidències$/);
  assert.equal(dateTimeText(new Date(2026, 10, 16, 9, 5, 7).toISOString()), '16/11/2026 09:05:07');
});
