// @ts-check
// Filtres i columnes de la pestanya «Pistes» (D-111): què es demana al motor (library.search) i com es diu, sense tocar
// la pàgina. Es prova amb «npm test» (app/tests/tracks-filter.test.mjs).

/**
 * @typedef {{
 *   text: string, categoryId: number | null, subcategoryId: number | null,
 *   state: string, inEvents: '' | 'yes' | 'no', validity: string, validFrom: string, validTo: string,
 *   played: '' | 'never' | 'range', playedFrom: string, playedTo: string, tagId: number | null, type: string,
 *   sort: string, desc: boolean
 * }} TrackFilters
 */

/** Els filtres per defecte: totes les pistes, per artista i títol. @returns {TrackFilters} */
export function defaultFilters() {
  return {
    text: '', categoryId: null, subcategoryId: null,
    state: '', inEvents: '', validity: '', validFrom: '', validTo: '',
    played: '', playedFrom: '', playedTo: '', tagId: null, type: '',
    sort: 'artist', desc: false,
  };
}

/** Els estats de la fitxa (el filtre «Estat»). */
export const STATE_OPTIONS = [
  ['', 'Tots els estats'], ['missing', 'El fitxer falta'], ['retired', 'Retirades'], ['disabled', 'Desactivades'], ['unclassified', 'Sense subcategoria'],
];

/** La vigència, segons les dates de la pista (un sol filtre: «vençudes» i «encara no actives» no es poden contradir). */
export const VALIDITY_OPTIONS = [
  ['', 'Qualsevol vigència'], ['valid', 'Vigents avui'], ['expired', 'Vençudes'], ['notYet', 'Encara no actives'], ['overlaps', 'Vigents dins d\'un interval…'],
];

/**
 * Els paràmetres de library.search (i library.ids) per a uns filtres.
 * @param {TrackFilters} f
 * @returns {Record<string, any>}
 */
export function filtersToParams(f) {
  /** @type {Record<string, any>} */
  const p = { text: f.text, sort: f.sort, desc: f.desc };
  if (f.subcategoryId != null) p.subcategoryId = f.subcategoryId;
  else if (f.categoryId != null) p.categoryId = f.categoryId;
  if (f.state) p.state = f.state;
  if (f.inEvents) p.inEvents = f.inEvents === 'yes';
  if (f.validity) {
    p.validity = f.validity;
    if (f.validity === 'overlaps') {
      if (f.validFrom) p.validFrom = f.validFrom;
      if (f.validTo) p.validTo = f.validTo;
    }
  }
  if (f.played === 'never') p.neverPlayed = true;
  if (f.played === 'range') {
    if (f.playedFrom) p.playedFrom = f.playedFrom;
    if (f.playedTo) p.playedTo = f.playedTo;
  }
  if (f.tagId != null) p.tagId = f.tagId;
  if (f.type) p.type = f.type;
  return p;
}

/** «2026-11-30» → «30/11/2026». @param {string} iso */
const day = (iso) => iso.split('-').reverse().join('/');

/**
 * Els filtres actius de dins de «Filtres» (no la cerca, la categoria ni la subcategoria, que sempre es veuen), com a
 * pastilles amb text: cadascuna es pot treure amb {@link withoutFilter}.
 * @param {TrackFilters} f
 * @param {{ tagName?: (id: number) => string, typeName?: (type: string) => string }} [names]
 * @returns {{ key: string, text: string }[]}
 */
export function filterChips(f, names = {}) {
  const chips = [];
  if (f.state) chips.push({ key: 'state', text: STATE_OPTIONS.find(([v]) => v === f.state)?.[1] ?? f.state });
  if (f.inEvents) chips.push({ key: 'inEvents', text: f.inEvents === 'yes' ? 'Dins d\'esdeveniments' : 'Fora d\'esdeveniments' });
  if (f.validity === 'overlaps') {
    const range = [f.validFrom && `del ${day(f.validFrom)}`, f.validTo && `al ${day(f.validTo)}`].filter(Boolean).join(' ');
    chips.push({ key: 'validity', text: `Vigents ${range || 'en algun moment'}` });
  } else if (f.validity) {
    chips.push({ key: 'validity', text: VALIDITY_OPTIONS.find(([v]) => v === f.validity)?.[1] ?? f.validity });
  }
  if (f.played === 'never') chips.push({ key: 'played', text: 'No han sonat mai' });
  if (f.played === 'range' && (f.playedFrom || f.playedTo)) {
    const range = [f.playedFrom && `del ${day(f.playedFrom)}`, f.playedTo && `al ${day(f.playedTo)}`].filter(Boolean).join(' ');
    chips.push({ key: 'played', text: `Última vegada ${range}` });
  }
  if (f.tagId != null) chips.push({ key: 'tagId', text: `Etiqueta: ${names.tagName?.(f.tagId) ?? f.tagId}` });
  if (f.type) chips.push({ key: 'type', text: `Tipus: ${names.typeName?.(f.type) ?? f.type}` });
  return chips;
}

/** Quants filtres de dins de «Filtres» hi ha actius (per al botó). @param {TrackFilters} f */
export function activeFilterCount(f) {
  return filterChips(f).length;
}

/**
 * Els filtres sense un dels de dins de «Filtres» (en treure'n la pastilla).
 * @param {TrackFilters} f @param {string} key
 * @returns {TrackFilters}
 */
export function withoutFilter(f, key) {
  const d = defaultFilters();
  switch (key) {
    case 'validity': return { ...f, validity: '', validFrom: '', validTo: '' };
    case 'played': return { ...f, played: '', playedFrom: '', playedTo: '' };
    default: return { ...f, [key]: /** @type {any} */ (d)[key] };
  }
}

/** Hi ha algun filtre diferent del per defecte (sense comptar l'ordre)? Per activar «Restableix». @param {TrackFilters} f */
export function isFiltered(f) {
  return !!(f.text.trim() || f.categoryId != null || f.subcategoryId != null || activeFilterCount(f) > 0);
}

/**
 * En clicar una capçalera: la mateixa columna canvia el sentit; una altra, el seu sentit natural (les reproduccions i
 * l'última vegada, de més a menys; la resta, de menys a més).
 * @param {TrackFilters} f @param {string} column
 * @returns {TrackFilters}
 */
export function sortBy(f, column) {
  if (f.sort === column) return { ...f, desc: !f.desc };
  return { ...f, sort: column, desc: column === 'plays' || column === 'lastPlayed' };
}

/**
 * La columna «Estat» d'una pista, sempre amb color + icona + text: el fitxer, si està desactivada i la vigència.
 * @param {any} track  de library.search
 * @param {string} today  'AAAA-MM-DD'
 * @returns {{ css: string, icon: string, text: string, title?: string }[]}
 */
export function trackStates(track, today) {
  const list = [];
  if (track.fileState === 'missing') list.push({ css: 'text-warning', icon: 'bi-exclamation-triangle-fill', text: 'Falta el fitxer' });
  if (track.fileState === 'retired') list.push({ css: 'text-body-secondary', icon: 'bi-archive', text: 'Retirada' });
  if (track.enabled === false) list.push({ css: 'text-body-secondary', icon: 'bi-slash-circle', text: 'Desactivada' });
  if (track.validUntil && track.validUntil < today) {
    list.push({ css: 'text-danger', icon: 'bi-calendar-x', text: 'Vençuda', title: `La data de fi era el ${day(track.validUntil)}` });
  } else if (track.validFrom && track.validFrom > today) {
    list.push({ css: 'text-info', icon: 'bi-calendar-plus', text: 'Encara no', title: `Comença el ${day(track.validFrom)}` });
  }
  return list;
}

/** «30/11/2026», o «» si no en té. @param {string | null | undefined} iso */
export function dateCell(iso) {
  return iso ? day(iso) : '';
}

/** «1.234 pistes» / «1 pista» / «Cap pista amb aquests filtres.». @param {number} total */
export function totalText(total) {
  if (total === 0) return 'Cap pista amb aquests filtres.';
  return `${total.toLocaleString('ca-ES')} ${total === 1 ? 'pista' : 'pistes'}`;
}
