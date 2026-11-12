// Proves de la llista de col·leccions de sweepers (fase 3, pas 5b). Amb «npm test», sense Electron.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sweeperOptions } from '../renderer/js/subcategories.js';

const categories = [
  { id: 1, name: 'Music', subcategories: [{ id: 10, name: 'Dance90' }] },
  { id: 2, name: 'Sweepers', subcategories: [{ id: 16, name: 'Dance90' }, { id: 17, name: 'Ona Bages' }] },
  { id: 3, name: 'Jingles', subcategories: [] },
];

test('només surten les subcategories de la categoria de sweepers', () => {
  assert.deepEqual(sweeperOptions(categories).map((s) => s.label), ['Sweepers / Dance90', 'Sweepers / Ona Bages']);
});

test('reconeix la categoria encara que es digui diferent («SWEEPER», «Sweepers 2»)', () => {
  assert.equal(sweeperOptions([{ id: 5, name: 'SWEEPER', subcategories: [{ id: 50, name: 'X' }] }]).length, 1);
});

test('sense categories, cap opció', () => {
  assert.deepEqual(sweeperOptions([]), []);
});
