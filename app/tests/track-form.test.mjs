// Proves de la fitxa del gestor de pistes (fase 3, pas 7a). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTags, trackChanges, trackProblems, guessType } from '../renderer/js/track-form.js';

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
