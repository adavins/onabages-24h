// @ts-check
// Avisos de la barra de sota (fase 4, pas 6b; D-146): els de la sortida, l'AutoDJ, la pausa i les carpetes, i els de
// vigència, junts i per gravetat. Textos, colors i icones, sense tocar la pàgina (es proven amb node --test).

/**
 * @typedef {{ kind: string, subject: 'track' | 'campaign', id: number, name: string, text: string,
 *   events: { id: number, name: string }[], canRemove: boolean }} ValidityWarning  docs/protocol.md, validity.list
 * @typedef {'danger' | 'warning' | 'info'} Level  de més a menys greu
 * @typedef {{ level: Level, text: string }} GeneralWarning  un avís de la sortida, l'AutoDJ, la pausa o les carpetes
 * @typedef {{ level: Level, general: GeneralWarning } | { level: Level, validity: ValidityWarning }} WarningItem
 */

/** L'ordre de gravetat. */
const LEVEL_ORDER = { danger: 0, warning: 1, info: 2 };

/** Cada nivell: el color (classe de Bootstrap) i la icona. Sempre color + icona + text. */
const LEVELS = {
  danger: { className: 'text-danger', icon: 'bi-exclamation-octagon-fill' },
  warning: { className: 'text-warning', icon: 'bi-exclamation-triangle-fill' },
  info: { className: 'text-info', icon: 'bi-info-circle-fill' },
};

/** @param {Level} level @returns {{ className: string, icon: string }} */
export function levelInfo(level) {
  return LEVELS[level] ?? LEVELS.warning;
}

/**
 * Cada tipus d'avís de vigència: el text curt, la gravetat i la icona. «Comença aviat» només és informació;
 * «Caducada» no fa mal a l'emissió (l'esdeveniment se la salta), però s'ha de netejar; «No sonarà» sí que és greu.
 */
const KINDS = {
  NO_SONARA: { label: 'No sonarà', level: /** @type {Level} */ ('danger'), icon: 'bi-volume-mute-fill' },
  CADUCADA: { label: 'Caducada', level: /** @type {Level} */ ('warning'), icon: 'bi-calendar-x' },
  DATES_INCORRECTES: { label: 'Dates incorrectes', level: /** @type {Level} */ ('warning'), icon: 'bi-exclamation-triangle-fill' },
  COMENCA_AVIAT: { label: 'Comença aviat', level: /** @type {Level} */ ('info'), icon: 'bi-hourglass-split' },
};

/** @param {string} kind @returns {{ label: string, level: Level, icon: string, className: string }} */
export function kindInfo(kind) {
  const info = KINDS[/** @type {keyof typeof KINDS} */ (kind)] ?? { label: kind, level: /** @type {Level} */ ('warning'), icon: 'bi-exclamation-triangle-fill' };
  return { ...info, className: levelInfo(info.level).className };
}

/** «Pista» o «Campanya». @param {ValidityWarning} w */
export function subjectText(w) {
  return w.subject === 'campaign' ? 'Campanya' : 'Pista';
}

/**
 * Tots els avisos junts, per gravetat (dins del mateix nivell, primer els generals i després els de vigència, en
 * l'ordre del motor).
 * @param {GeneralWarning[]} general @param {ValidityWarning[]} validity
 * @returns {WarningItem[]}
 */
export function allWarnings(general, validity) {
  /** @type {WarningItem[]} */
  const items = [
    ...general.map((g) => ({ level: g.level, general: g })),
    ...validity.map((v) => ({ level: kindInfo(v.kind).level, validity: v })),
  ];
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => (LEVEL_ORDER[a.item.level] - LEVEL_ORDER[b.item.level]) || (a.index - b.index))
    .map((x) => x.item);
}

/**
 * El que diu la barra de sota: «Cap avís» o «N avisos», amb el color i la icona del més greu.
 * @param {WarningItem[]} items  de {@link allWarnings}
 * @returns {{ text: string, className: string, icon: string }}
 */
export function barState(items) {
  if (items.length === 0) return { text: 'Cap avís', className: 'text-success', icon: 'bi-check-circle' };
  const worst = levelInfo(items[0].level);
  return { text: items.length === 1 ? '1 avís' : `${items.length} avisos`, ...worst };
}

/** Els avisos de vigència d'una pista o d'una campanya. @param {ValidityWarning[]} warnings @param {'track' | 'campaign'} subject @param {number} id */
export function warningsFor(warnings, subject, id) {
  return warnings.filter((w) => w.subject === subject && w.id === id);
}

/**
 * El resum de «Treu-la dels esdeveniments»: el que ha fet el motor i, si s'han esborrat després, els esdeveniments que
 * només la tenien a ella.
 * @param {{ changed: number, deleted: number, kept: { id: number, name: string }[] }} result
 * @param {{ deleted: string[], failed: { name: string, message: string }[] }} [events]  els esdeveniments esborrats (pel nom) i els que no s'han pogut esborrar, amb el motiu
 */
export function removedText(result, events) {
  const parts = [];
  if (result.changed > 0) parts.push(result.changed === 1 ? 'S\'ha tret d\'1 esdeveniment.' : `S'ha tret de ${result.changed} esdeveniments.`);
  if (result.deleted > 0) parts.push(result.deleted === 1 ? 'S\'ha esborrat 1 bloc de falques que s\'havia quedat buit.' : `S'han esborrat ${result.deleted} blocs de falques que s'havien quedat buits.`);
  if (events) {
    if (events.deleted.length > 0) parts.push(`${events.deleted.length === 1 ? 'S\'ha esborrat l\'esdeveniment' : 'S\'han esborrat els esdeveniments'} ${quoted(events.deleted)}.`);
    for (const f of events.failed) parts.push(`No s'ha pogut esborrar «${f.name}»: ${f.message}`);
  } else if (result.kept.length > 0) {
    parts.push(`${result.kept.length === 1 ? 'L\'esdeveniment' : 'Els esdeveniments'} ${quoted(result.kept.map((e) => e.name))} no ${result.kept.length === 1 ? 's\'ha' : 's\'han'} tocat: només ${result.kept.length === 1 ? 'feia' : 'feien'} sonar això.`);
  }
  return parts.length > 0 ? parts.join(' ') : 'No era a cap esdeveniment actiu.';
}

/** @param {string[]} names */
function quoted(names) {
  return names.map((n) => `«${n}»`).join(', ');
}
