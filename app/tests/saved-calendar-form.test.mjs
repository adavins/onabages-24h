// Proves dels textos dels calendaris desats (fase 4, pas 6a; D-145): app/renderer/js/saved-calendar-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { daysText, slotsText, cellsText, findByName, appliedText } from '../renderer/js/saved-calendar-form.js';

test('daysText i slotsText', () => {
  assert.equal(daysText(127), 'Cada dia');
  assert.equal(daysText(31), 'Dl–Dv');
  assert.equal(daysText(96), 'Ds Dg');
  assert.equal(daysText(1 | 4 | 32), 'Dl Dc Ds');
  assert.equal(slotsText([{ time: '09:00:00', daysMask: 31 }, { time: '12:05:00', daysMask: 32 }]), '09:00 Dl–Dv · 12:05 Ds');
  const many = Array.from({ length: 8 }, (_, i) => ({ time: `${String(10 + i)}:00:00`, daysMask: 1 }));
  assert.match(slotsText(many, 2), /^10:00 Dl · 11:00 Dl · … \(8 hores\)$/);
});

test('cellsText i findByName (sense distingir majúscules ni espais)', () => {
  assert.equal(cellsText(1), '1 casella');
  assert.equal(cellsText(4), '4 caselles');
  const list = [{ id: 1, name: 'Matins' }, { id: 2, name: 'Tardes de cap de setmana' }];
  assert.equal(findByName(list, '  MATINS ')?.id, 1);
  assert.equal(findByName(list, 'Nits'), null);
});

test('appliedText: el resum i les caselles que no s\'han pogut marcar', () => {
  assert.deepEqual(appliedText({ marked: 3, already: 1, removed: 0, failed: [] }), { summary: 'S\'han marcat 3 caselles; 1 casella ja hi era.', problems: [] });
  assert.deepEqual(appliedText({ marked: 0, already: 2, removed: 1, failed: [{ time: '09:00:00', day: 5, reason: 'No es pot.' }] }), {
    summary: 'No s\'ha marcat cap casella nova; 2 caselles ja hi eren; s\'ha tret 1 casella.',
    problems: ['Ds 09:00: No es pot.'],
  });
});
