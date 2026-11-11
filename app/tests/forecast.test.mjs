// Proves de la previsió de la cua (fase 3, pas 1). S'executen amb «npm test» (dins d'app\).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { nextCut, forecastQueue, untilText, dayText, clockOf } from '../renderer/js/forecast.js';

const at = (/** @type {string} */ hhmmss) => new Date(`2026-11-19T${hhmmss}`).toISOString();
const NOW = Date.parse(at('12:56:40'));
const talla = { eventId: 1, name: 'Informatiu migdia', mode: 'TALLA', at: at('13:00:00'), time: '13:00:00', clearQueue: true };

/** La cua de la maqueta: una que sona, un sweeper, una cançó que es tallarà i dues que no sonaran. */
function queue() {
  return {
    current: { title: 'Blue Monday', durationMs: 240_000 },
    currentEndsAt: at('12:58:57'),
    upcoming: [
      { title: 'Ona Bages', durationMs: 4_000, plannedAt: at('12:58:53') },
      { title: 'Living Next Door To Alice', durationMs: 208_000, plannedAt: at('12:58:57') },
      { title: 'First Original Thought', durationMs: 165_000, plannedAt: at('13:02:20') },
      { title: 'Ràdio Santpedor', durationMs: 10_000, plannedAt: at('13:05:00') },
    ],
  };
}

test('nextCut: el primer TALLA que encara no ha passat', () => {
  const superposa = { ...talla, eventId: 2, mode: 'SUPERPOSA', at: at('12:58:00') };
  const passat = { ...talla, eventId: 3, at: at('12:00:00') };
  assert.equal(nextCut([passat, superposa, talla], NOW), talla);
  assert.equal(nextCut([superposa], NOW), null);
});

test('forecastQueue: com a la maqueta, una es tallarà i dues no sonaran', () => {
  const f = forecastQueue(queue(), [talla], NOW);
  assert.equal(f.cut, talla);
  assert.equal(f.current, null); // la que sona s'acaba abans de les 13:00
  assert.deepEqual(f.items.map((i) => i.state), ['ok', 'cut', 'lost', 'lost']);
  assert.equal(f.items[1].playsMs, 63_000); // de 12:58:57 a 13:00:00
  assert.equal(f.lost, 2);
  assert.equal(f.markerIndex, 2); // la fila del TALLA va abans de la primera que no sonarà
});

test('forecastQueue: si el TALLA no esborra la cua, les de després sonen després', () => {
  const f = forecastQueue(queue(), [{ ...talla, clearQueue: false }], NOW);
  assert.deepEqual(f.items.map((i) => i.state), ['ok', 'cut', 'after', 'after']);
  assert.equal(f.lost, 0);
});

test('forecastQueue: la que sona també es pot tallar', () => {
  const q = { ...queue(), currentEndsAt: at('13:01:00'), upcoming: [] };
  const f = forecastQueue(q, [talla], NOW);
  assert.deepEqual(f.current, { playsMs: 180_000, durationMs: 240_000 }); // se'n perden els últims 60 s
});

test('forecastQueue: sense TALLA o sense hora prevista, res marcat', () => {
  assert.deepEqual(forecastQueue(queue(), [], NOW).items.map((i) => i.state), ['ok', 'ok', 'ok', 'ok']);
  const q = queue();
  q.upcoming[2].plannedAt = null;
  q.upcoming[3].plannedAt = null;
  assert.deepEqual(forecastQueue(q, [talla], NOW).items.map((i) => i.state), ['ok', 'cut', 'ok', 'ok']);
});

test('untilText, dayText i clockOf', () => {
  assert.equal(untilText(200_000), "d'aquí 3:20");
  assert.equal(untilText(59_001), "d'aquí 1:00");
  assert.equal(untilText(2 * 3600_000 + 11 * 60_000), "d'aquí 2 h 11 min");
  assert.equal(untilText(3 * 3600_000), "d'aquí 3 h");
  assert.equal(untilText(0), 'ara');
  const now = new Date(2026, 10, 19, 12, 0);
  assert.equal(dayText('2026-11-19', now), 'avui');
  assert.equal(dayText('2026-11-20', now), 'demà');
  assert.equal(dayText('2026-11-23', now), 'dl 23/11');
  assert.equal(clockOf(new Date(2026, 10, 19, 12, 58, 53).toISOString()), '12:58:53');
  assert.equal(clockOf(null), '–');
});
