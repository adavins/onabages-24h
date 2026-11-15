// Proves de la fitxa del gestor de pistes (fase 3, pas 7a). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  parseTags, trackChanges, trackProblems, guessType, parseSeconds, formatSeconds, cueProblems,
  dataSummary, classSummary, datesText, validitySummary, otherSummary, programmingRows, programmingSummary,
} from '../renderer/js/track-form.js';

test('parseSeconds i formatSeconds: segons amb coma i minuts', () => {
  assert.equal(parseSeconds('62,5'), 62500);
  assert.equal(parseSeconds('62.5'), 62500);
  assert.equal(parseSeconds('1:02,345'), 62345);
  assert.equal(parseSeconds(' 0,12 '), 120);
  assert.equal(parseSeconds('1:75'), null);
  assert.equal(parseSeconds('abc'), null);
  assert.equal(parseSeconds(''), null);
  assert.equal(formatSeconds(62345), '1:02,345');
  assert.equal(formatSeconds(7250), '7,250');
  assert.equal(formatSeconds(null), '');
  assert.equal(parseSeconds(formatSeconds(215432)), 215432);
});

test('cueProblems: inici ≤ mescla ≤ final ≤ durada', () => {
  assert.deepEqual(cueProblems(100, 200000, 210000, 215000), []);
  assert.equal(cueProblems(5000, 1000, 210000, 215000).length, 1);
  assert.equal(cueProblems(100, 200000, 190000, 215000).length, 1);
  assert.match(cueProblems(100, 200000, 220000, 215000)[0], /durada/);
  assert.equal(cueProblems(null, 1, 2, 3).length, 1);
});

test('guessType: el tipus que proposa segons la categoria de l\'emissora', () => {
  assert.equal(guessType('Music'), 'music');
  assert.equal(guessType('Sweepers'), 'sweeper');
  assert.equal(guessType('Station IDs'), 'stationId');
  assert.equal(guessType('Jingles'), 'stationId');
  assert.equal(guessType('Promos'), 'promo');
  assert.equal(guessType('Commercials'), 'commercial');
  assert.equal(guessType('Radio Shows'), 'program');
  assert.equal(guessType('Capsules'), 'program');
  assert.equal(guessType('News'), 'news');
  assert.equal(guessType('Radio Streams'), 'stream');
  assert.equal(guessType(null), 'music');
});

const track = {
  artist: 'Macy Gray', title: 'I Try', album: '', year: '1999', type: 'music', subcategoryId: 30, tags: ['català', '90s'],
  variableDuration: false, validFrom: null, validUntil: null, enabled: true, notes: null,
};

/** Els valors del formulari tal com els omple la fitxa, amb canvis. */
const values = (/** @type {object} */ changes = {}) => ({
  artist: 'Macy Gray', title: 'I Try', album: '', year: '1999', type: 'music', subcategoryId: 30, tags: ['90s', 'Català'],
  variableDuration: false, validFrom: '', validUntil: '', enabled: true, notes: '', ...changes,
});

test('parseTags: separades per comes, sense buides ni repetides', () => {
  assert.deepEqual(parseTags(' català, 80s,, Català ,nadal '), ['català', '80s', 'nadal']);
  assert.deepEqual(parseTags(''), []);
});

test('sense canvis (les etiquetes en un altre ordre o majúscules no compten)', () => {
  assert.deepEqual(trackChanges(track, values()), {});
});

test('només els camps que canvien', () => {
  assert.deepEqual(trackChanges(track, values({ title: ' I Try (edit) ', subcategoryId: null, validUntil: '2027-01-06', enabled: false, notes: 'Hola' })),
    { title: 'I Try (edit)', subcategoryId: null, validUntil: '2027-01-06', enabled: false, notes: 'Hola' });
  assert.deepEqual(trackChanges(track, values({ tags: ['90s'] })), { tags: ['90s'] });
  assert.deepEqual(trackChanges({ ...track, notes: 'Abans' }, values()), { notes: '' });
});

test('problemes: sense títol ni artista, i dates al revés', () => {
  assert.deepEqual(trackProblems(values()), []);
  assert.equal(trackProblems(values({ title: ' ', artist: '' })).length, 1);
  assert.match(trackProblems(values({ validFrom: '2026-12-10', validUntil: '2026-12-01' }))[0], /data de fi/);
});

test('resums dels requadres de la fitxa, plegats (D-147)', () => {
  assert.equal(dataSummary({ artist: ' Sau ', title: 'Boig per tu' }), 'Sau · Boig per tu');
  assert.equal(dataSummary({ artist: '', title: '' }), 'sense artista ni títol');
  const name = (/** @type {string} */ t) => ({ commercial: 'Falca', music: 'Música' })[t] ?? t;
  assert.equal(classSummary({ type: 'commercial', tags: ['a', 'b'], variableDuration: true }, 'Publicitat', name),
    'Falca · Publicitat · 2 etiquetes · durada variable');
  assert.equal(classSummary({ type: 'music', tags: [], variableDuration: false }, null, name), 'Música · sense subcategoria');
  assert.equal(datesText('2026-12-01', '2026-12-31'), 'del 1/12/2026 al 31/12/2026');
  assert.equal(datesText('2026-12-01', ''), 'des del 1/12/2026');
  assert.equal(datesText('', '2026-12-31'), 'fins al 31/12/2026');
  assert.equal(validitySummary({ validFrom: '', validUntil: '' }), 'sense límit de dates');
  assert.equal(validitySummary({ validFrom: '', validUntil: '2026-12-31' }), 'fins al 31/12/2026');
  assert.equal(otherSummary({ album: 'Disc', year: '1990' }, 1), 'Disc · 1990 · ha sonat 1 vegada');
  assert.equal(otherSummary({ album: '', year: '' }, 0), 'no ha sonat mai');
  assert.equal(otherSummary({ album: '', year: '' }, 2, '3:25'), '3:25 · ha sonat 2 vegades');
});

test('requadre «Programació»: per hora, amb els dies propis i les plantilles al final (D-148)', () => {
  const ev = (/** @type {object} */ o) => ({ mode: 'ENCUA', recurrence: 'SETMANAL', daysMask: 0b111_1111, enabled: true, isTemplate: false, ...o });
  const pista = (/** @type {number | null} */ daysMask, type = 'PISTA') => ({ type, params: { songId: 7 }, daysMask });
  const rows = programmingRows([
    { id: 1, name: 'Bloc de les 12', event: ev({ name: 'Bloc de les 12', startTime: '12:00:00', actions: [pista(0b101)] }) },
    { id: 2, name: 'Plantilla', event: ev({ name: 'Plantilla', startTime: '00:00:00', isTemplate: true, actions: [pista(null)] }) },
    { id: 3, name: 'Tall de les 9', event: ev({ name: 'Tall de les 9', mode: 'TALLA', startTime: '09:00:00', daysMask: 0b11111, enabled: false, actions: [pista(null)] }) },
    { id: 4, name: 'Senyal', event: ev({ name: 'Senyal', mode: 'SUPERPOSA', startTime: '19:00:30', actions: [pista(null, 'AUXILIAR')] }) },
  ], 7);
  assert.deepEqual(rows.map((r) => r.id), [3, 1, 4, 2]);
  assert.deepEqual([rows[0].time, rows[0].enabled, rows[0].onlyDays], ['09:00', false, null]);
  assert.equal(rows[1].onlyDays, 'només dl, dc');
  assert.equal(rows[2].aux, true);
  assert.equal(rows[2].time, '19:00:30');
  assert.equal(rows[3].template, true);
  assert.equal(programmingSummary(rows), '4 esdeveniments · 09:00, 12:00, 19:00:30');
  assert.equal(programmingSummary([]), 'cap esdeveniment');
});
