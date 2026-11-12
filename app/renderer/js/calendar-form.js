// @ts-check
// Calendari d'una pista (fase 4, pas 2b; D-104): textos i comprovacions de la graella, sense tocar la pàgina.
// Es proven amb «npm test» (app/tests/calendar-form.test.mjs). Les caselles són les de calendar.get (docs/protocol.md).

export const DAY_NAMES = ['dilluns', 'dimarts', 'dimecres', 'dijous', 'divendres', 'dissabte', 'diumenge'];
export const DAY_SHORT = ['Dl', 'Dt', 'Dc', 'Dj', 'Dv', 'Ds', 'Dg'];

/**
 * Els avisos d'una casella, en català (buit si no n'hi ha cap).
 * @param {any} cell  una casella de calendar.get (no null)
 * @returns {string[]}
 */
export function cellWarnings(cell) {
  /** @type {string[]} */
  const list = [];
  if (!cell.enabled) list.push('L\'esdeveniment està desactivat: ara no sona.');
  if (cell.busyBy) list.push(`Franja ocupada per «${cell.busyBy}» (dins de la seva durada màxima).`);
  if (cell.others?.length > 0) list.push(`A la mateixa hora també hi ha ${cell.others.map((/** @type {string} */ o) => `«${o}»`).join(', ')}: es fa servir aquest.`);
  if (cell.repeats) list.push('L\'esdeveniment es repeteix dins del dia: la pista hi serà a totes les hores.');
  if (cell.dates?.length > 0) list.push(`Només sona en dates soltes: ${cell.dates.map(dateText).join(', ')}.`);
  else if (cell.nonWeekly) list.push('L\'esdeveniment no sona totes les setmanes (mira el planificador).');
  return list;
}

/**
 * El text d'ajuda d'una casella (en passar-hi el ratolí).
 * @param {any} cell  @param {number} day 0 = dilluns  @param {string} time 'HH:MM:SS'
 */
export function cellTitle(cell, day, time) {
  const head = `${DAY_NAMES[day]} ${time.slice(0, 5)} · ${cell.name}${cell.commercialBlock ? ' (bloc de falques)' : ''}`;
  const state = cell.marked ? 'La pista hi és. Fes clic per treure-la d\'aquest dia.' : 'Fes clic per posar-hi la pista aquest dia.';
  return [head, state, ...cellWarnings(cell)].join('\n');
}

/**
 * L'hora d'un bloc de falques nou («+ Afegeix una hora»): «HH:MM» en múltiples de 5 minuts. Retorna el motiu si no
 * és vàlida, o null.
 * @param {string} text
 */
export function blockTimeProblem(text) {
  const match = /^(\d{1,2}):(\d{2})$/.exec(text.trim());
  if (!match) return 'Escriu l\'hora com HH:MM (p. ex. 08:05).';
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  if (hours > 23 || minutes > 59) return 'Aquesta hora no existeix.';
  if (minutes % 5 !== 0) return 'Els blocs de falques van en múltiples de 5 minuts (p. ex. 08:05, 08:35).';
  return null;
}

/** Les 24 hores en punt ('00:00:00' … '23:00:00'): la graella les mostra sempre, encara que no hi hagi res. */
export const WHOLE_HOURS = Array.from({ length: 24 }, (_, h) => `${String(h).padStart(2, '0')}:00:00`);

/**
 * Les files de la graella: les de calendar.get, totes les hores en punt (buides si no hi ha cap esdeveniment) i les
 * hores provisionals (D-105: «+ Afegeix una hora» no crea res; el bloc de falques es crea en marcar-hi la primera
 * casella). Una hora que ja té fila no es repeteix. En ordre d'hora.
 * @param {{ time: string, days: any[] }[]} rows  de calendar.get
 * @param {Iterable<string>} extraTimes  'HH:MM:SS'
 */
export function withExtraRows(rows, extraTimes) {
  const have = new Set(rows.map((r) => r.time));
  const empty = () => [null, null, null, null, null, null, null];
  const hours = WHOLE_HOURS.filter((t) => !have.has(t));
  for (const t of hours) have.add(t);
  const extra = [...extraTimes].filter((t) => !have.has(t)).map((time) => ({ time, days: empty(), provisional: true }));
  return [...rows, ...hours.map((time) => ({ time, days: empty() })), ...extra].sort((a, b) => a.time.localeCompare(b.time));
}

/**
 * Vista «Només aquesta pista» (D-105): només les hores on sona la pista, i de cada dia només si hi és.
 * @param {{ time: string, days: any[] }[]} rows
 * @returns {{ time: string, days: boolean[] }[]}
 */
export function trackOnlyRows(rows) {
  return rows
    .map((r) => ({ time: r.time, days: r.days.map((c) => !!c?.marked) }))
    .filter((r) => r.days.some(Boolean));
}

/** «8:5» → «08:05». @param {string} text */
export function normalizeBlockTime(text) {
  const [h, m] = text.trim().split(':');
  return `${h.padStart(2, '0')}:${m.padStart(2, '0')}`;
}

/** «2026-11-16» → «16/11/2026». @param {string | null | undefined} iso */
export function dateText(iso) {
  if (!iso) return '';
  const [y, m, d] = iso.split('-');
  return `${d}/${m}/${y}`;
}

const MONTHS = ['gener', 'febrer', 'març', 'abril', 'maig', 'juny', 'juliol', 'agost', 'setembre', 'octubre', 'novembre', 'desembre'];

/** «2026-11» → «novembre 2026». @param {string} month */
export function monthText(month) {
  const [y, m] = month.split('-');
  return `${MONTHS[Number(m) - 1]} ${y}`;
}

/** «1 passi» / «3 passis». @param {number} n */
export function passes(n) {
  return `${n} ${n === 1 ? 'passi' : 'passis'}`;
}

/**
 * El període dels recomptes, en una frase.
 * @param {{ from: string, to: string, periodFromTrack: boolean }} counts
 */
export function periodText(counts) {
  const range = `del ${dateText(counts.from)} al ${dateText(counts.to)}`;
  return counts.periodFromTrack ? `${range} (les dates de la pista)` : `${range} (la pista no té dates d'inici i de fi: 4 setmanes des d'avui)`;
}
