// Proves dels rols a la interfície (fase 5, pas 8b; D-164): app/renderer/js/session.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { roleAllows } from '../renderer/js/session.js';

test('cada rol pot fer el que poden fer els de sota', () => {
  assert.equal(roleAllows('operator', 'operator'), true);
  assert.equal(roleAllows('operator', 'programmer'), false);
  assert.equal(roleAllows('programmer', 'operator'), true);
  assert.equal(roleAllows('programmer', 'administrator'), false);
  assert.equal(roleAllows('administrator', 'programmer'), true);
  assert.equal(roleAllows(undefined, 'operator'), false); // sense sessió
});
