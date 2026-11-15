// @ts-check
// Calendaris desats (fase 4, pas 6a; D-145): els textos de les finestres «Desa com a calendari…» i «Aplica un calendari
// desat…». No toca la pàgina: es prova amb «npm test» (app/tests/saved-calendar-form.test.mjs).

import { DAY_SHORT } from './calendar-form.js';

/** Els dies d'una màscara (bit0 = dilluns): «Dl Dt», «Dl–Dv», «Cada dia». @param {number} mask */
export function daysText(mask) {
  if ((mask & 127) === 127) return 'Cada dia';
  if ((mask & 127) === 31) return 'Dl–Dv';
  if ((mask & 127) === 96) return 'Ds Dg';
  return DAY_SHORT.filter((_, d) => (mask & (1 << d)) !== 0).join(' ');
}

/**
 * Les franges d'un calendari desat en una línia: «09:00 Dl–Dv · 12:05 Ds». Si n'hi ha moltes, les primeres i «…».
 * @param {{ time: string, daysMask: number }[]} slots @param {number} [max]
 */
export function slotsText(slots, max = 6) {
  const parts = slots.slice(0, max).map((s) => `${s.time.slice(0, 5)} ${daysText(s.daysMask)}`);
  if (slots.length > max) parts.push(`… (${slots.length} hores)`);
  return parts.join(' · ');
}

/** «1 casella» / «3 caselles». @param {number} n */
export function cellsText(n) {
  return `${n} ${n === 1 ? 'casella' : 'caselles'}`;
}

/**
 * El calendari desat amb aquest nom (sense distingir majúscules ni espais als extrems), si n'hi ha: per avisar que es
 * substituirà.
 * @param {{ id: number, name: string }[]} calendars @param {string} name
 */
export function findByName(calendars, name) {
  const wanted = name.trim().toLocaleLowerCase('ca');
  return calendars.find((c) => c.name.trim().toLocaleLowerCase('ca') === wanted) ?? null;
}

/**
 * El resum d'aplicar un calendari desat: què s'ha fet i, si n'hi ha, les caselles que no s'han pogut marcar.
 * @param {{ marked: number, already: number, removed: number, failed: { time: string, day: number, reason: string }[] }} a
 * @returns {{ summary: string, problems: string[] }}
 */
export function appliedText(a) {
  const parts = [];
  parts.push(a.marked === 0 ? 'No s\'ha marcat cap casella nova' : `S'han marcat ${cellsText(a.marked)}`);
  if (a.already > 0) parts.push(`${cellsText(a.already)} ja ${a.already === 1 ? 'hi era' : 'hi eren'}`);
  if (a.removed > 0) parts.push(`s'${a.removed === 1 ? 'ha' : 'han'} tret ${cellsText(a.removed)}`);
  const problems = a.failed.map((f) => `${DAY_SHORT[f.day] ?? '?'} ${f.time.slice(0, 5)}: ${f.reason}`);
  return { summary: `${parts.join('; ')}.`, problems };
}
