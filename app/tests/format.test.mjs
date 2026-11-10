// Proves de les funcions de format de la interfície. S'executen amb «npm test» (dins d'app\), sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatDuration, formatRemaining, typeName, originName, meterFraction, formatClock, formatLongDate } from '../renderer/js/format.js';

test('formatDuration: minuts i segons, i hores si cal', () => {
  assert.equal(formatDuration(0), '0:00');
  assert.equal(formatDuration(61_500), '1:01');
  assert.equal(formatDuration(227_682), '3:47');
  assert.equal(formatDuration(3_725_000), '1:02:05');
  assert.equal(formatDuration(-5), '0:00');
  assert.equal(formatDuration(null), '–');
  assert.equal(formatDuration(NaN), '–');
});

test('formatRemaining: arrodoneix cap amunt', () => {
  assert.equal(formatRemaining(188_181), '3:09');
  assert.equal(formatRemaining(1), '0:01');
  assert.equal(formatRemaining(0), '0:00');
  assert.equal(formatRemaining(60_000), '1:00');
});

test('typeName i originName: noms en català, amb un valor per defecte', () => {
  assert.equal(typeName('music'), 'Música');
  assert.equal(typeName('stationId'), 'ID emissora');
  assert.equal(typeName('desconegut'), 'Altres');
  assert.equal(originName('autodj'), 'AutoDJ');
  assert.equal(originName(undefined), 'Manual');
});

test('meterFraction: de −60 dB (buit) a 0 dB (ple)', () => {
  assert.equal(meterFraction(-90), 0);
  assert.equal(meterFraction(-60), 0);
  assert.equal(meterFraction(-30), 0.5);
  assert.equal(meterFraction(0), 1);
  assert.equal(meterFraction(3), 1);
});

test('formatClock i formatLongDate', () => {
  const date = new Date(2026, 10, 10, 9, 5, 7);
  assert.equal(formatClock(date), '09:05:07');
  assert.match(formatLongDate(date), /dimarts, 10 de novembre/);
});
