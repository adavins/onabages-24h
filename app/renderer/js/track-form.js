// @ts-check
// Fitxa d'una pista al gestor de pistes (fase 3, pas 7a; D-087): què ha canviat respecte del que diu el motor (per
// enviar només això amb library.update) i si els valors tenen sentit. Sense res de la pantalla: es prova amb «npm test».

/** Tipus de pista, en l'ordre en què surten al desplegable (docs/protocol.md, 4.4). */
export const TRACK_TYPES = ['music', 'sweeper', 'stationId', 'promo', 'commercial', 'program', 'news', 'timeAnnouncement', 'timeSignal', 'stream', 'other'];

/**
 * @typedef {{
 *   artist: string, title: string, album: string, year: string, type: string, subcategoryId: number | null,
 *   tags: string[], variableDuration: boolean, validFrom: string, validUntil: string, enabled: boolean, notes: string
 * }} TrackFormValues  el que hi ha escrit a la fitxa (dates 'AAAA-MM-DD' o buides)
 */

/**
 * El tipus de pista que segurament tenen les pistes d'una categoria (per proposar-lo en importar; es pot canviar).
 * Pels noms de les categories de l'emissora (Sweepers, Jingles, Station IDs, Promos, Commercials, Radio Shows…).
 * @param {string | null | undefined} categoryName
 */
export function guessType(categoryName) {
  const name = (categoryName ?? '').toLowerCase();
  if (/sweep/.test(name)) return 'sweeper';
  if (/jingle|station|identifica|indicatiu/.test(name)) return 'stationId';
  if (/promo/.test(name)) return 'promo';
  if (/commercial|falca|falques|publicitat/.test(name)) return 'commercial';
  if (/show|programa|capsul|càpsul|interview|entrevist/.test(name)) return 'program';
  if (/news|informatiu|notíci|notici/.test(name)) return 'news';
  if (/stream/.test(name)) return 'stream';
  if (/effect|efecte/.test(name)) return 'other';
  return 'music';
}

/**
 * Etiquetes escrites separades per comes: sense espais als extrems, sense buides i sense repetides (sense distingir
 * majúscules; es queda la primera).
 * @param {string} text
 * @returns {string[]}
 */
export function parseTags(text) {
  /** @type {string[]} */
  const tags = [];
  for (const raw of text.split(',')) {
    const tag = raw.trim();
    if (tag && !tags.some((t) => t.toLowerCase() === tag.toLowerCase())) tags.push(tag);
  }
  return tags;
}

/**
 * Els camps que han canviat, a punt per a library.update (sense trackId). Buit si no ha canviat res.
 * @param {any} track  la fitxa del motor (library.get)
 * @param {TrackFormValues} values
 * @returns {Record<string, any>}
 */
export function trackChanges(track, values) {
  /** @type {Record<string, any>} */
  const changes = {};
  for (const field of /** @type {const} */ (['artist', 'title', 'album', 'year'])) {
    const value = values[field].trim();
    if (value !== (track[field] ?? '')) changes[field] = value;
  }
  if (values.type !== track.type) changes.type = values.type;
  if (values.subcategoryId !== (track.subcategoryId ?? null)) changes.subcategoryId = values.subcategoryId;
  const key = (/** @type {string[]} */ list) => list.map((t) => t.toLowerCase()).sort().join('\n');
  if (key(values.tags) !== key(track.tags ?? [])) changes.tags = values.tags;
  if (values.variableDuration !== track.variableDuration) changes.variableDuration = values.variableDuration;
  for (const field of /** @type {const} */ (['validFrom', 'validUntil'])) {
    const value = values[field] || null;
    if (value !== (track[field] ?? null)) changes[field] = value;
  }
  if (values.enabled !== track.enabled) changes.enabled = values.enabled;
  const notes = values.notes.trim();
  if (notes !== (track.notes ?? '')) changes.notes = notes;
  return changes;
}

/**
 * Problemes que impedeixen desar (en català, per mostrar-los).
 * @param {TrackFormValues} values
 * @returns {string[]}
 */
export function trackProblems(values) {
  const problems = [];
  if (!values.title.trim() && !values.artist.trim()) problems.push('Cal un títol o un artista.');
  if (values.validFrom && values.validUntil && values.validUntil < values.validFrom)
    problems.push('La data de fi no pot ser anterior a la d\'inici.');
  if (values.tags.some((t) => t.length > 100)) problems.push('Una etiqueta pot tenir com a molt 100 caràcters.');
  return problems;
}
