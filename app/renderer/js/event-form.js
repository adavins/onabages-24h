// @ts-check
// Lògica del formulari d'esdeveniments (fase 2, pas 8b): valors per defecte, conversions entre el que s'escriu i el que
// vol el motor, i saber si hi ha canvis sense desar. No toca la pàgina: es prova amb «npm test»
// (app/tests/event-form.test.mjs). El format de l'esdeveniment és el del motor (docs/protocol.md, 4.7).

/** Un esdeveniment nou, amb els valors per defecte de l'editor (docs/planificador.md, secció 2). */
export function newEvent() {
  return {
    id: 0,
    name: '',
    mode: 'TALLA',
    startTime: '12:00:00',
    recurrence: 'SETMANAL',
    daysMask: 127,
    intervalWeeks: null,
    anchorDate: null,
    ordinals: [],
    monthDays: [],
    startDate: null,
    endDate: null,
    repeat: 'CAP',
    intervalMinutes: null,
    hoursMask: null,
    priority: 50,
    toleranceSeconds: 60,
    fadeMs: null,
    marginSeconds: null,
    duckingDb: null,
    maxDurationSeconds: null,
    color: null,
    categoryId: null,
    clearQueue: true, // per defecte, marcat als TALLA i desmarcat als ENCUA (D-022)
    commercialBlock: false,
    isTemplate: false,
    enabled: true,
    notes: null,
    actions: [],
    includedDates: [],
    excludedDates: [],
  };
}

/** Còpia independent d'un esdeveniment (per editar-lo sense tocar l'original). @param {any} ev */
export function cloneEvent(ev) {
  return JSON.parse(JSON.stringify(ev));
}

/**
 * L'esdeveniment tal com s'envia al motor: sense els camps que només són per mostrar (`label`, `detail`… de les
 * accions) ni la data de modificació.
 * @param {any} ev
 */
export function eventToSave(ev) {
  const copy = cloneEvent(ev);
  delete copy.modified;
  copy.name = String(copy.name ?? '').trim();
  copy.actions = (copy.actions ?? []).map((/** @type {any} */ a) => ({ type: a.type, params: a.params, daysMask: a.daysMask ?? null }));
  copy.includedDates = [...(copy.includedDates ?? [])].sort();
  copy.excludedDates = [...(copy.excludedDates ?? [])].sort();
  return copy;
}

/** Si dos esdeveniments són iguals pel que fa al que es desa (per saber si hi ha canvis). @param {any} a @param {any} b */
export function sameEvent(a, b) {
  return JSON.stringify(eventToSave(a)) === JSON.stringify(eventToSave(b));
}

/** Còpia per a «Duplica»: un esdeveniment nou amb el mateix contingut. @param {any} ev */
export function duplicateEvent(ev) {
  const copy = cloneEvent(ev);
  copy.id = 0;
  copy.name = `${ev.name} (còpia)`;
  copy.notes = null; // p. ex. la marca «Importat de RadioDJ»: la còpia ja no ho és
  return copy;
}

/** «HH:MM:SS» a partir del que dona el camp d'hora («HH:MM» o «HH:MM:SS»). @param {string} value */
export function normalizeTime(value) {
  if (/^\d{2}:\d{2}$/.test(value)) return `${value}:00`;
  return /^\d{2}:\d{2}:\d{2}$/.test(value) ? value : '';
}

/** Minuts (com s'escriuen a l'editor) a segons, o null si és buit o no és vàlid. @param {string} text */
export function minutesToSeconds(text) {
  const minutes = Number(String(text).replace(',', '.'));
  return text.trim() === '' || !Number.isFinite(minutes) || minutes <= 0 ? null : Math.round(minutes * 60);
}

/** Segons a minuts per mostrar («60», «1,5»). @param {number | null | undefined} seconds */
export function secondsToMinutes(seconds) {
  if (seconds == null) return '';
  return String(Math.round((seconds / 60) * 100) / 100).replace('.', ',');
}

/** Nombre enter d'un camp de text, o null si és buit o no és vàlid. @param {string} text */
export function intOrNull(text) {
  const n = Number(text);
  return text.trim() === '' || !Number.isInteger(n) ? null : n;
}

/** Nombre (amb coma o punt) d'un camp de text, o null. @param {string} text */
export function numberOrNull(text) {
  const n = Number(String(text).replace(',', '.'));
  return text.trim() === '' || !Number.isFinite(n) ? null : n;
}

/**
 * Hores com s'escriuen («7-23», «9, 12, 18», «0-6, 22») a màscara de 24 bits. Buit o totes = null (totes les hores).
 * Retorna undefined si no s'entén.
 * @param {string} text
 * @returns {number | null | undefined}
 */
export function parseHours(text) {
  const clean = text.replace(/h/gi, '').trim();
  if (clean === '') return null;
  let mask = 0;
  for (const part of clean.split(/[,;\s]+/).filter(Boolean)) {
    const range = part.match(/^(\d{1,2})(?:[-–](\d{1,2}))?$/);
    if (!range) return undefined;
    const from = Number(range[1]);
    const to = range[2] === undefined ? from : Number(range[2]);
    if (from > 23 || to > 23 || from > to) return undefined;
    for (let h = from; h <= to; h++) mask |= 1 << h;
  }
  return mask === 0xFF_FFFF ? null : mask;
}

/** Màscara d'hores com s'escriu: «7-23», «9, 12, 18». Totes = buit. @param {number | null | undefined} mask */
export function formatHours(mask) {
  if (mask == null || mask === 0xFF_FFFF) return '';
  /** @type {string[]} */
  const parts = [];
  for (let h = 0; h < 24; h++) {
    if ((mask & (1 << h)) === 0) continue;
    let end = h;
    while (end + 1 < 24 && (mask & (1 << (end + 1))) !== 0) end++;
    parts.push(end > h ? `${h}-${end}` : String(h));
    h = end;
  }
  return parts.join(', ');
}

/**
 * Dies del mes com s'escriuen («1, 15, últim») a la llista del motor (−1 = l'últim). Retorna undefined si no s'entén.
 * @param {string} text
 * @returns {number[] | undefined}
 */
export function parseMonthDays(text) {
  /** @type {number[]} */
  const days = [];
  for (const part of text.split(/[,;]+/).map((p) => p.trim().toLowerCase()).filter(Boolean)) {
    if (/^(l')?[uú]ltim$/.test(part)) days.push(-1);
    else if (/^\d{1,2}$/.test(part) && Number(part) >= 1 && Number(part) <= 31) days.push(Number(part));
    else return undefined;
  }
  return [...new Set(days)].sort((a, b) => (a === -1 ? 99 : a) - (b === -1 ? 99 : b));
}

/** @param {number[] | null | undefined} days */
export function formatMonthDays(days) {
  return (days ?? []).map((d) => (d === -1 ? 'últim' : String(d))).join(', ');
}

/** Posa o treu un dia (0 = dilluns) d'una màscara. @param {number} mask @param {number} day @param {boolean} on */
export function setDay(mask, day, on) {
  return on ? mask | (1 << day) : mask & ~(1 << day);
}

/** Mou un element d'una llista una posició amunt (−1) o avall (+1). Retorna una llista nova. @template T @param {T[]} list @param {number} index @param {number} delta */
export function moveItem(list, index, delta) {
  const target = index + delta;
  if (target < 0 || target >= list.length) return list;
  const copy = [...list];
  [copy[index], copy[target]] = [copy[target], copy[index]];
  return copy;
}

/**
 * En canviar de mode a l'editor: «Esborra la cua» passa al valor per defecte del mode nou (D-022) i es treuen les
 * opcions que el mode nou no fa servir.
 * @param {any} ev  @param {string} mode
 */
export function withMode(ev, mode) {
  const copy = { ...ev, mode };
  copy.clearQueue = mode === 'TALLA';
  if (mode !== 'TALLA') copy.fadeMs = null;
  return copy;
}

/**
 * Comprovacions que es poden fer sense el motor (la resta les fa ell en desar): retorna el primer problema, o null.
 * @param {any} ev
 */
export function quickCheck(ev) {
  if (!String(ev.name ?? '').trim()) return 'Cal posar-li un nom.';
  if (!normalizeTime(ev.startTime ?? '')) return "L'hora d'inici no és vàlida.";
  if (ev.recurrence === 'DATES' && (ev.includedDates ?? []).length === 0) return 'Cal afegir alguna data.';
  if (ev.startDate && ev.endDate && ev.startDate > ev.endDate) return "La data d'inici ha de ser anterior a la de fi.";
  if (ev.mode === 'SUPERPOSA' && !(ev.actions ?? []).some((/** @type {any} */ a) => a.type === 'AUXILIAR'))
    return 'Un esdeveniment Superposa necessita alguna acció «Auxiliar» (el que sona per sobre).';
  if ((ev.actions ?? []).length === 0) return "Cal afegir alguna acció (què ha de fer l'esdeveniment).";
  const stream = (ev.actions ?? []).find((/** @type {any} */ a) => a.type === 'STREAM' && !(a.params?.durada_seg > 0));
  if (stream) return `Cal posar la durada del stream «${stream.label ?? 'Stream'}» (si no, no s'acabaria mai).`;
  return null;
}

/**
 * Les opcions de l'esdeveniment que no són les per defecte del seu mode (per saber si la secció «Opcions» està omplerta).
 * @param {any} ev
 */
export function hasCustomOptions(ev) {
  return ev.clearQueue !== (ev.mode === 'TALLA') || ev.fadeMs != null || ev.duckingDb != null
    || ev.priority !== 50 || ev.toleranceSeconds !== 60;
}

/**
 * Les seccions de l'editor que s'obren en obrir un esdeveniment (redisseny del planificador): d'un de nou, només
 * «General» (la resta s'obre a mesura que s'omple); d'un de desat, les que estan omplertes. «Pròximes» sempre tancada.
 * @param {any} ev  @param {boolean} isNew
 * @returns {Set<string>}  'general', 'when', 'options', 'actions'
 */
export function initialSections(ev, isNew) {
  if (isNew) return new Set(['general']);
  const open = new Set(['general', 'when']);
  if (hasCustomOptions(ev)) open.add('options');
  if ((ev.actions ?? []).length > 0) open.add('actions');
  return open;
}

/**
 * Resum d'una línia de la secció «Opcions» (per veure-ho amb la secció tancada).
 * @param {any} ev
 */
export function optionsSummary(ev) {
  const parts = [];
  if (ev.mode === 'TALLA' || ev.mode === 'ENCUA') parts.push(ev.clearQueue ? 'esborra la cua' : 'no esborra la cua');
  if (ev.mode === 'TALLA' && ev.fadeMs != null) parts.push(ev.fadeMs === 0 ? 'tall sec' : `fos ${ev.fadeMs} ms`);
  if (ev.duckingDb != null) parts.push(`atenuació ${String(ev.duckingDb).replace('.', ',')} dB`);
  if (ev.priority !== 50) parts.push(`prioritat ${ev.priority}`);
  if (ev.toleranceSeconds !== 60) parts.push(`tolerància ${ev.toleranceSeconds} s`);
  return parts.join(' · ') || 'per defecte';
}

const WEEKDAY_NAMES = ['dilluns', 'dimarts', 'dimecres', 'dijous', 'divendres', 'dissabte', 'diumenge'];

/**
 * Els dies de la setmana triats que no s'executaran mai per les dates d'inici i de fi (D-109): si entre totes dues hi
 * ha menys d'una setmana, només hi caben uns quants dies. Només per a les recurrències amb dies de la setmana.
 * @param {any} ev
 * @returns {number[]}  0 = dilluns; buit si no n'hi ha cap (o no es pot saber)
 */
export function unreachableDays(ev) {
  if (!['SETMANAL', 'CADA_N_SETMANES', 'MENSUAL_ORDINAL'].includes(ev.recurrence)) return [];
  if (!ev.startDate || !ev.endDate || ev.startDate > ev.endDate) return [];
  const start = new Date(`${ev.startDate}T12:00:00`);
  const end = new Date(`${ev.endDate}T12:00:00`);
  let inRange = 0;
  for (let d = new Date(start); d <= end && inRange !== 127; d.setDate(d.getDate() + 1)) inRange |= 1 << ((d.getDay() + 6) % 7);
  const result = [];
  for (let day = 0; day < 7; day++) if ((ev.daysMask & (1 << day)) && !(inRange & (1 << day))) result.push(day);
  return result;
}

/** L'avís dels dies que no s'executaran, en una frase, o null. @param {any} ev */
export function unreachableDaysText(ev) {
  const days = unreachableDays(ev);
  if (days.length === 0) return null;
  const names = days.map((d) => WEEKDAY_NAMES[d]);
  const list = names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} i ${names[names.length - 1]}`;
  const all = days.length === [...Array(7).keys()].filter((d) => ev.daysMask & (1 << d)).length;
  return all
    ? `No s'executarà mai: entre les dates d'inici i de fi no hi ha cap ${list}.`
    : `${list[0].toUpperCase()}${list.slice(1)} no s'executar${days.length === 1 ? 'à' : 'an'} mai: entre les dates d'inici i de fi no n'hi ha cap.`;
}

/** Avui, com a «AAAA-MM-DD» (hora local). @param {Date} [now] */
export function todayIso(now = new Date()) {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

/** «2026-11-30» → «30/11/2026». @param {string} iso */
const dayText = (iso) => iso.split('-').reverse().join('/');

/**
 * Les dates de la pista d'una acció (D-110) respecte d'avui, per marcar-la a la llista d'accions:
 * - 'ended': ja s'ha acabat; 'notYet': encara no ha començat (totes dues: avui no sonarà);
 * - 'until': sona, però té data de fi; null: cap problema (o l'acció no té pista amb dates).
 * @param {{ validFrom?: string | null, validUntil?: string | null }} action  @param {string} today 'AAAA-MM-DD'
 * @returns {{ kind: 'ended' | 'notYet' | 'until', text: string } | null}
 */
export function trackDatesState(action, today) {
  const { validFrom, validUntil } = action;
  if (validUntil && validUntil < today) return { kind: 'ended', text: `No sonarà: la data de fi de la pista era el ${dayText(validUntil)}` };
  if (validFrom && validFrom > today) {
    return { kind: 'notYet', text: `Encara no sonarà: la pista comença el ${dayText(validFrom)}${validUntil ? ` (fins al ${dayText(validUntil)})` : ''}` };
  }
  if (validUntil) return { kind: 'until', text: `Sona fins al ${dayText(validUntil)} (data de fi de la pista)` };
  return null;
}
