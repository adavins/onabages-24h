// @ts-check
// Càlculs del planificador per a la interfície (fase 2, pas 8a): setmanes, textos de les recurrències i com es
// col·loquen els blocs a la graella. No toquen la pàgina: es proven amb «npm test» (app/tests/schedule.test.mjs).
// Els esdeveniments i les emissions són els del motor (docs/protocol.md, 4.7).

/** Modes d'esdeveniment: sempre color + icona + text (CLAUDE.md; colors de docs/maquetes/Main.dc.html). */
export const MODES = {
  TALLA: { label: 'Talla', icon: 'bi-scissors', css: 'mode-talla' },
  ENCUA: { label: 'Encua', icon: 'bi-list-ol', css: 'mode-encua' },
  SUPERPOSA: { label: 'Superposa', icon: 'bi-layers', css: 'mode-superposa' },
  SISTEMA: { label: 'Sistema', icon: 'bi-sliders', css: 'mode-sistema' },
};

/** @param {string} mode */
export function modeInfo(mode) {
  return MODES[/** @type {keyof typeof MODES} */ (mode)] ?? MODES.SISTEMA;
}

const DAY_SHORT = ['dl', 'dt', 'dc', 'dj', 'dv', 'ds', 'dg'];
const DAY_LONG = ['dilluns', 'dimarts', 'dimecres', 'dijous', 'divendres', 'dissabte', 'diumenge'];
const MONTHS = ['gener', 'febrer', 'març', 'abril', 'maig', 'juny', 'juliol', 'agost', 'setembre', 'octubre', 'novembre', 'desembre'];

/** Noms curts dels dies, de dilluns a diumenge (per a les capçaleres). */
export const WEEKDAYS = DAY_SHORT.map((d) => d.toUpperCase());

// --- Dates (sempre en hora local i amb dates de calendari, com el motor) ---

/** El dilluns de la setmana de <date>, a les 00:00. @param {Date} date */
export function weekStart(date) {
  const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const offset = (d.getDay() + 6) % 7; // dilluns = 0
  d.setDate(d.getDate() - offset);
  return d;
}

/** @param {Date} date  @param {number} days */
export function addDays(date, days) {
  const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  d.setDate(d.getDate() + days);
  return d;
}

/** «AAAA-MM-DD» (data local). @param {Date} date */
export function isoDate(date) {
  const p = (/** @type {number} */ n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}`;
}

/** Llegeix «AAAA-MM-DD» com a data local. @param {string} text */
export function parseDate(text) {
  const [y, m, d] = text.split('-').map(Number);
  return new Date(y, m - 1, d);
}

/** «20/11/2026». @param {string} text  «AAAA-MM-DD» */
export function shortDate(text) {
  const [y, m, d] = text.split('-').map(Number);
  return `${d}/${m}/${y}`;
}

/** «Setmana del 16 al 22 de novembre de 2026» (o «del 30 de novembre al 6 de desembre de 2026»). @param {Date} monday */
export function weekTitle(monday) {
  const sunday = addDays(monday, 6);
  const month = (/** @type {Date} */ d) => `${d.getDate()} ${MONTHS[d.getMonth()].match(/^[aeiou]/) ? "d'" : 'de '}${MONTHS[d.getMonth()]}`;
  if (monday.getFullYear() !== sunday.getFullYear())
    return `Setmana del ${month(monday)} de ${monday.getFullYear()} al ${month(sunday)} de ${sunday.getFullYear()}`;
  if (monday.getMonth() !== sunday.getMonth())
    return `Setmana del ${month(monday)} al ${month(sunday)} de ${sunday.getFullYear()}`;
  return `Setmana del ${monday.getDate()} al ${month(sunday)} de ${sunday.getFullYear()}`;
}

/** Minuts des de mitjanit de «HH:MM:SS». @param {string} time */
export function minutesOf(time) {
  const [h, m, s] = time.split(':').map(Number);
  return h * 60 + m + (s || 0) / 60;
}

/** «HH:MM» (o «HH:MM:SS» si els segons no són zero). @param {string} time */
export function shortTime(time) {
  return time.endsWith(':00') ? time.slice(0, 5) : time;
}

// --- Textos ---

/**
 * Dies de la setmana d'una màscara (bit0 = dilluns): «cada dia», «dl-dv», «dl, dc, dv».
 * @param {number} mask
 */
export function daysText(mask) {
  if (mask === 0b111_1111) return 'cada dia';
  const days = [0, 1, 2, 3, 4, 5, 6].filter((i) => (mask & (1 << i)) !== 0);
  if (days.length === 0) return 'cap dia';
  if (days.length >= 3 && days[days.length - 1] - days[0] === days.length - 1)
    return `${DAY_SHORT[days[0]]}-${DAY_SHORT[days[days.length - 1]]}`;
  return days.map((d) => DAY_SHORT[d]).join(', ');
}

/** Llista en català: «a», «a i b», «a, b i c». @param {string[]} items */
function joinAnd(items) {
  return items.length <= 1 ? items.join('') : `${items.slice(0, -1).join(', ')} i ${items[items.length - 1]}`;
}

const ORDINALS = { 1: '1r', 2: '2n', 3: '3r', 4: '4t', 5: '5è', '-1': 'últim' };

/** Hores d'una màscara de 24 bits: «totes les hores», «7–23 h», «9, 12 i 18 h». @param {number | null} mask */
export function hoursText(mask) {
  if (mask == null || mask === 0xFF_FFFF) return 'totes les hores';
  const hours = Array.from({ length: 24 }, (_, h) => h).filter((h) => (mask & (1 << h)) !== 0);
  if (hours.length === 0) return 'cap hora';
  if (hours.length >= 3 && hours[hours.length - 1] - hours[0] === hours.length - 1) return `${hours[0]}–${hours[hours.length - 1]} h`;
  return `${joinAnd(hours.map(String))} h`;
}

/**
 * Quan sona un esdeveniment, en una frase (docs/planificador.md, secció 12).
 * @param {any} ev  esdeveniment del motor
 */
export function recurrenceText(ev) {
  let text;
  switch (ev.recurrence) {
    case 'CADA_N_SETMANES':
      text = `Cada ${ev.intervalWeeks} setmanes · ${daysText(ev.daysMask)}` + (ev.anchorDate ? ` · des del ${shortDate(ev.anchorDate)}` : '');
      break;
    case 'MENSUAL_ORDINAL': {
      const which = joinAnd((ev.ordinals ?? []).map((/** @type {number} */ o) => ORDINALS[/** @type {keyof typeof ORDINALS} */ (String(o))] ?? String(o)));
      const days = [0, 1, 2, 3, 4, 5, 6].filter((i) => (ev.daysMask & (1 << i)) !== 0).map((d) => DAY_LONG[d]);
      text = `${which.startsWith('últim') ? "L'" : 'El '}${which} ${joinAnd(days)} de cada mes`;
      break;
    }
    case 'DIES_MES':
      text = `Els dies ${joinAnd((ev.monthDays ?? []).map((/** @type {number} */ d) => (d === -1 ? "l'últim" : String(d))))} de cada mes`;
      break;
    case 'DATES': {
      const dates = (ev.includedDates ?? []).map(shortDate);
      text = dates.length <= 3 ? `Només ${joinAnd(dates)}` : `${dates.length} dates soltes (de ${dates[0]} a ${dates[dates.length - 1]})`;
      break;
    }
    default:
      text = ev.daysMask === 0b111_1111 ? 'Cada dia' : `Cada setmana · ${daysText(ev.daysMask)}`;
  }
  if (ev.repeat === 'CADA_HORA') text += ` · cada hora (${hoursText(ev.hoursMask)}) al minut ${ev.startTime.slice(3, 5)}`;
  if (ev.repeat === 'CADA_N_MIN') text += ` · cada ${ev.intervalMinutes} min des de les ${shortTime(ev.startTime)}`;
  if (ev.startDate && ev.endDate) text += ` · del ${shortDate(ev.startDate)} al ${shortDate(ev.endDate)}`;
  else if (ev.startDate) text += ` · a partir del ${shortDate(ev.startDate)}`;
  else if (ev.endDate) text += ` · fins al ${shortDate(ev.endDate)}`;
  return text;
}

/** «60 min», «1 h 30 min». @param {number | null | undefined} seconds */
export function durationText(seconds) {
  if (seconds == null) return '';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}

// --- Graella ---

/** Els esdeveniments que es repeteixen dins del dia no van a la graella: tenen una fila pròpia («Cada hora»). @param {any} ev */
export function repeatsWithinDay(ev) {
  return ev.repeat === 'CADA_HORA' || ev.repeat === 'CADA_N_MIN';
}

/**
 * Col·loca els blocs d'un dia en carrils perquè no es tapin: dos blocs que coincideixen en el temps van un al costat
 * de l'altre. Cada bloc té `start` i `end` (minuts); se li afegeixen `lane` i `lanes` (quants carrils té el seu grup).
 * Els blocs han d'anar ordenats per inici.
 * @template {{ start: number, end: number }} T
 * @param {T[]} blocks
 * @returns {(T & { lane: number, lanes: number })[]}
 */
export function layoutLanes(blocks) {
  /** @type {(T & { lane: number, lanes: number })[]} */
  const result = [];
  /** @type {(T & { lane: number, lanes: number })[]} */
  let group = [];
  let groupEnd = -Infinity;
  /** @type {number[]} */
  let laneEnds = [];

  const closeGroup = () => {
    const lanes = Math.max(1, laneEnds.length);
    for (const b of group) b.lanes = lanes;
    group = [];
    laneEnds = [];
  };

  for (const block of blocks) {
    if (block.start >= groupEnd) closeGroup();
    let lane = laneEnds.findIndex((end) => end <= block.start);
    if (lane === -1) {
      lane = laneEnds.length;
      laneEnds.push(block.end);
    } else {
      laneEnds[lane] = block.end;
    }
    const placed = { ...block, lane, lanes: 1 };
    group.push(placed);
    result.push(placed);
    groupEnd = Math.max(groupEnd, block.end);
  }
  closeGroup();
  return result;
}
