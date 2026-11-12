// Proves de les funcions de format de la interfície. S'executen amb «npm test» (dins d'app\), sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatDuration, formatRemaining, typeName, originName, meterFraction, formatClock, formatLongDate, formatGainDb } from '../renderer/js/format.js';

test('formatGainDb: amb signe i coma decimal', () => {
  assert.equal(formatGainDb(0), '0 dB');
  assert.equal(formatGainDb(2), '+2 dB');
  assert.equal(formatGainDb(-3.5), '−3,5 dB');
  assert.equal(formatGainDb(-0.04), '0 dB');
  assert.equal(formatGainDb(1.25), '+1,3 dB');
});

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

test('playedAgo: quan va sonar per última vegada', async () => {
  const { playedAgo } = await import('../renderer/js/format.js');
  const now = new Date(2026, 10, 19, 12, 0);
  const ago = (/** @type {number} */ ms) => new Date(now.getTime() - ms).toISOString();
  assert.equal(playedAgo(null, now), 'mai');
  assert.equal(playedAgo(ago(20_000), now), 'ara');
  assert.equal(playedAgo(ago(5 * 60_000), now), 'fa 5 min');
  assert.equal(playedAgo(ago(3 * 3600_000), now), 'fa 3 h');
  assert.equal(playedAgo(new Date(2026, 10, 18, 23, 30).toISOString(), now), 'ahir');   // dia de calendari
  assert.equal(playedAgo(new Date(2026, 10, 15, 12, 0).toISOString(), now), 'fa 4 dies');
  assert.equal(playedAgo(new Date(2026, 7, 19, 12, 0).toISOString(), now), 'fa 3 mesos');
  assert.equal(playedAgo(new Date(2025, 9, 1).toISOString(), now), 'fa 1 any');
});
