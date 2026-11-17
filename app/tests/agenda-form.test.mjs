// Agenda de bosses (D-170): textos, validació i ordre de les llistes.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  monthOf, monthName, monthShort, nextMonth, agendaProblem, assignmentText, shortSubcategory, moveItem, bagsThatChange, manualBagChangeText,
} from '../renderer/js/agenda-form.js';

test('mesos: el d\'una data, el nom, el curt i el següent', () => {
  assert.equal(monthOf(new Date(2026, 11, 31, 23, 59)), '2026-12');
  assert.equal(monthOf(new Date(2027, 0, 1)), '2027-01');
  assert.equal(monthName('2026-12'), 'desembre de 2026');
  assert.equal(monthShort('2027-03'), 'mar. 2027');
  assert.equal(nextMonth('2026-12'), '2027-01');
  assert.equal(nextMonth('2027-01'), '2027-02');
});

test('validació: la mateixa que el motor', () => {
  assert.equal(agendaProblem('Adult', [1, 2], [10, 11, 12], '2026-12'), null);
  assert.match(agendaProblem(' ', [1], [10], '2026-12') ?? '', /nom/);
  assert.match(agendaProblem('Adult', [1], [10], '') ?? '', /mes d'inici/);
  assert.match(agendaProblem('Adult', [], [10], '2026-12') ?? '', /com a mínim una bossa/);
  assert.match(agendaProblem('Adult', [1, 1], [10, 11], '2026-12') ?? '', /bossa hi surt dues vegades/);
  assert.match(agendaProblem('Adult', [1], [10, 10], '2026-12') ?? '', /subcategoria hi surt dues vegades/);
  assert.match(agendaProblem('Adult', [1, 2], [10], '2026-12') ?? '', /com a mínim 2 subcategories/);
});

test('el que toca, sense la categoria', () => {
  assert.equal(assignmentText([
    { bag: 'Generalista A', subcategory: 'Music / Adult 1' }, { bag: 'Generalista B', subcategory: 'Music / Adult 2' },
  ]), 'Generalista A: Adult 1 · Generalista B: Adult 2');
  assert.equal(assignmentText([]), 'encara no ha començat');
  assert.equal(shortSubcategory('Adult 3'), 'Adult 3');
});

test('moure un element de la llista', () => {
  assert.deepEqual(moveItem([1, 2, 3], 2, -1), [1, 3, 2]);
  assert.deepEqual(moveItem([1, 2, 3], 0, 1), [2, 1, 3]);
  assert.deepEqual(moveItem([1, 2, 3], 0, -1), [1, 2, 3]);
  assert.deepEqual(moveItem([1, 2, 3], 2, 1), [1, 2, 3]);
});

test('les bosses que canvien en desar', () => {
  const now = [
    { bagId: 1, bag: 'A', subcategoryId: 10, subcategory: 'Music / Adult 1' },
    { bagId: 2, bag: 'B', subcategoryId: 11, subcategory: 'Music / Adult 2' },
  ];
  const changes = bagsThatChange(now, [{ id: 1, subcategoryId: 10 }, { id: 2, subcategoryId: 15 }]);
  assert.deepEqual(changes.map((c) => c.bag), ['B']);
});

test("l'avís en canviar a mà una bossa d'una agenda activa", () => {
  const agenda = { name: 'Adult', next: [{ bagId: 1, subcategory: 'Music / Adult 3' }] };
  assert.equal(manualBagChangeText(agenda, { id: 1, name: 'Generalista A' }, '2026-12'),
    "«Generalista A» la canvia l'agenda «Adult». Aquest canvi durarà fins a l'1 de gener de 2027, quan l'agenda hi posarà Adult 3. " +
    "Si vols que el canvi duri, canvia l'agenda (Rotacions → Bosses → «Agenda…»).");
  assert.match(manualBagChangeText({ name: 'Adult', next: [] }, { id: 1, name: 'A' }, '2026-12'), /encara no ha començat/);
});
