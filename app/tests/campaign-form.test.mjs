// Proves de l'editor de campanyes (fase 4, pas 3c). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  newCampaign, newSpot, campaignFromApi, campaignToApi, campaignChanged, campaignProblems, moveSpot, spotWarnings, datesText,
} from '../renderer/js/campaign-form.js';

const saved = {
  id: 3, name: 'Ferreteria', advertiser: 'Ferreteria Santpedor', startDate: '2026-11-01', endDate: '2026-11-30',
  selection: 'PES', state: 'ACTIVA', contractedTotal: 66, contractedPerDay: null, notes: null,
  spots: [
    { trackId: 1, startDate: null, endDate: null, daysMask: 127, weight: 1, track: { id: 1, title: 'A', fileState: 'ok', enabled: true } },
    { trackId: 3, startDate: '2026-11-24', endDate: '2026-11-28', daysMask: 31, weight: 2, track: { id: 3, title: 'C', fileState: 'ok', enabled: true } },
  ],
};

test('campanya nova: d\'avui a final de mes, esborrany', () => {
  const c = newCampaign('2026-11-13');
  assert.equal(c.startDate, '2026-11-13');
  assert.equal(c.endDate, '2026-11-30');
  assert.equal(newCampaign('2028-02-10').endDate, '2028-02-29'); // any de traspàs
  assert.equal(c.state, 'ESBORRANY');
  assert.deepEqual(campaignProblems(c), ['Falta el nom de la campanya.']);
});

test('del motor al formulari i tornada, sense canvis', () => {
  const f = campaignFromApi(saved);
  assert.equal(f.contractedTotal, '66');
  assert.equal(f.contractedPerDay, '');
  assert.equal(f.spots[0].startDate, '');
  assert.deepEqual(campaignToApi(f), {
    name: 'Ferreteria', advertiser: 'Ferreteria Santpedor', startDate: '2026-11-01', endDate: '2026-11-30', selection: 'PES',
    state: 'ACTIVA', contractedTotal: 66, contractedPerDay: null, notes: null,
    spots: [
      { trackId: 1, startDate: null, endDate: null, daysMask: 127, weight: 1 },
      { trackId: 3, startDate: '2026-11-24', endDate: '2026-11-28', daysMask: 31, weight: 2 },
    ],
  });
  assert.equal(campaignChanged(saved, f), false);
  f.spots = moveSpot(f.spots, 1, 0);
  assert.equal(campaignChanged(saved, f), true); // l'ordre compta (seqüència)
  assert.equal(campaignChanged(null, campaignFromApi(saved)), true);
});

test('problemes: els mateixos que comprova el motor', () => {
  const f = campaignFromApi(saved);
  f.endDate = '2026-10-31';
  assert.match(campaignProblems(f).join(' '), /anterior a la d'inici/);
  const g = campaignFromApi(saved);
  g.spots[1].endDate = '2026-12-05';
  g.spots[0].daysMask = 0;
  g.spots[0].weight = 11;
  g.contractedTotal = '-3';
  assert.deepEqual(campaignProblems(g), [
    'Els passis contractats han de ser un nombre enter, 0 o més.',
    'La falca 1 no té cap dia de la setmana.',
    'La falca 1: el pes ha de ser entre 1 i 10.',
    'La falca 2: les dates han de ser dins de les de la campanya.',
  ]);
});

test('avisos d\'una falca: fitxer, desactivada i dates de la pista', () => {
  const f = campaignFromApi(saved);
  assert.deepEqual(spotWarnings(f.spots[0], f), []);
  const spot = newSpot({ id: 9, title: 'X', fileState: 'missing', enabled: false, validUntil: '2026-11-15' });
  assert.equal(spot.daysMask, 127);
  const warnings = spotWarnings(spot, f);
  assert.equal(warnings.length, 3);
  assert.match(warnings[2], /fins al 15\/11\/2026/);
  assert.deepEqual(spotWarnings({ ...spot, track: null }, f), ['La pista ja no existeix.']);
  assert.equal(datesText('2026-11-01', '2026-11-30'), 'del 1/11/2026 al 30/11/2026');
});

test('resum del pla: el que ha sonat i el que està previst, dia a dia, i els passis contractats', async () => {
  const { planSummary } = await import('../renderer/js/campaign-form.js');
  const campaign = { startDate: '2026-11-01', endDate: '2026-11-04', contractedTotal: 8, contractedPerDay: 2 };
  const plan = {
    // Ja ha sonat (historial): l'1 dues vegades, el 2 només una (l'altra es va ometre)
    played: [
      { at: '2026-11-01T08:30:05.0000000+01:00', trackId: 1, title: 'A' },
      { at: '2026-11-01T19:30:02.0000000+01:00', trackId: 2, title: 'B' },
      { at: '2026-11-02T19:30:01.0000000+01:00', trackId: 1, title: 'A' },
    ],
    // Previstos: el 3 dos (un de buit) i el 4 cap
    passes: [
      { at: '2026-11-03T08:30:00.0000000+01:00', trackId: 2, title: 'B' },
      { at: '2026-11-03T19:30:00.0000000+01:00', trackId: null, title: null },
    ],
  };
  const s = planSummary(plan, campaign, '2026-11-03');
  assert.deepEqual([s.played, s.planned, s.total, s.empty, s.pastOff, s.futureOff], [3, 2, 5, 1, 1, 1]);
  assert.deepEqual(s.byDay.get('2026-11-01')?.items, [
    { time: '08:30', title: 'A', played: true }, { time: '19:30', title: 'B', played: true },
  ]);
  assert.equal(s.byDay.get('2026-11-03')?.empty, 1);
  assert.deepEqual(s.warnings, [
    'Entre els que han sonat (3) i els previstos (2) en surten 5, i se n\'han contractat 8 (3 de menys).',
    '1 dia que ja ha passat no va tenir els 2 passis per dia contractats.',
    '1 dia no tindrà els 2 passis per dia contractats.',
    '1 passi previst queda buit: aquell dia no hi ha cap falca vàlida.',
  ]);
  // Sense passis contractats, només l'avís dels buits
  assert.deepEqual(planSummary(plan, { startDate: '2026-11-01', endDate: '2026-11-04' }, '2026-11-03').warnings.length, 1);
});

test('datesBetween i monthWeeks: els dies de la campanya i les setmanes del mes, de dilluns a diumenge', async () => {
  const { datesBetween, monthWeeks } = await import('../renderer/js/campaign-form.js');
  assert.deepEqual(datesBetween('2026-10-24', '2026-10-27'), ['2026-10-24', '2026-10-25', '2026-10-26', '2026-10-27']); // canvi d'hora
  assert.deepEqual(datesBetween('2026-11-05', '2026-11-01'), []);
  const nov = monthWeeks(2026, 11); // l'1 de novembre de 2026 és diumenge
  assert.equal(nov.length, 6);
  assert.deepEqual(nov[0], [null, null, null, null, null, null, '2026-11-01']);
  assert.deepEqual(nov[5], ['2026-11-30', null, null, null, null, null, null]);
});
