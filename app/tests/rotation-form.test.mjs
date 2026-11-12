// Proves de l'editor de rotacions (fase 3, pas 8a). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { newStep, stepFromApi, stepToApi, rotationChanged, rotationProblems } from '../renderer/js/rotation-form.js';

const rotation = {
  id: 1, name: 'OnaBages',
  steps: [
    { subcategoryId: 30, categoryId: 1, tagId: null, selection: 'leastRecent', repeatRule: true, sweeper: false },
    { subcategoryId: null, categoryId: 3, tagId: 2, selection: 'random', repeatRule: false, sweeper: false },
  ],
};

test('passos: del motor al formulari i tornada', () => {
  const steps = rotation.steps.map(stepFromApi);
  assert.equal(steps[0].source, 's:30'); // amb subcategoria, la subcategoria (no la categoria)
  assert.equal(steps[1].source, 'c:3');
  assert.deepEqual(stepToApi(steps[0]), { selection: 'leastRecent', repeatRule: true, sweeper: false, subcategoryId: 30 });
  assert.deepEqual(stepToApi(steps[1]), { selection: 'random', repeatRule: false, sweeper: false, categoryId: 3, tagId: 2 });
});

test('passos amb bossa: la bossa, no la subcategoria que té ara', () => {
  const step = stepFromApi({ bagId: 4, bag: 'Generalista A', subcategoryId: 30, categoryId: 1, selection: 'leastRecent', repeatRule: true, sweeper: false });
  assert.equal(step.source, 'b:4');
  assert.deepEqual(stepToApi(step), { selection: 'leastRecent', repeatRule: true, sweeper: false, bagId: 4 });
});

test('canvis: el nom, un pas o l\'ordre', () => {
  const steps = rotation.steps.map(stepFromApi);
  assert.equal(rotationChanged(rotation, 'OnaBages ', steps), false);
  assert.equal(rotationChanged(rotation, 'OnaBages 2', steps), true);
  assert.equal(rotationChanged(rotation, 'OnaBages', [steps[1], steps[0]]), true);
  assert.equal(rotationChanged(rotation, 'OnaBages', [{ ...steps[0], sweeper: true }, steps[1]]), true);
  assert.equal(rotationChanged(null, 'Nova', []), true);
});

test('problemes: sense nom, sense passos o passos sense font', () => {
  assert.deepEqual(rotationProblems('OnaBages', rotation.steps.map(stepFromApi)), []);
  assert.equal(rotationProblems(' ', rotation.steps.map(stepFromApi)).length, 1);
  assert.equal(rotationProblems('X', []).length, 1);
  assert.match(rotationProblems('X', [newStep(), stepFromApi(rotation.steps[0]), newStep()])[0], /passos 1, 3/);
});
