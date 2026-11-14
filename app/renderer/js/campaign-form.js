// @ts-check
// Editor de campanyes (fase 4, pas 3c; D-122): la campanya tal com la mostra el formulari, i la conversió amb el que diu i
// demana el motor (campaigns.get, campaigns.save). Sense res de la pantalla: es prova amb «npm test».

/**
 * Una falca al formulari. `track`: la pista (per mostrar-la), de campaigns.get o del cercador.
 * @typedef {{ trackId: number, startDate: string, endDate: string, daysMask: number, weight: number, track: any }} FormSpot
 */

/**
 * La campanya al formulari. Les dates en 'AAAA-MM-DD' ('' = sense); els passis contractats, com a text ('' = cap).
 * @typedef {{ name: string, advertiser: string, startDate: string, endDate: string, selection: string, state: string,
 *   contractedTotal: string, contractedPerDay: string, notes: string, spots: FormSpot[] }} FormCampaign
 */

/** Els estats (D-122), amb el text, la icona i el color (sempre els tres: color + icona + text). */
export const STATES = {
  ESBORRANY: { text: 'Esborrany', icon: 'bi-pencil-square', css: 'text-body-secondary', help: 'Es pot programar, però no sona.' },
  ACTIVA: { text: 'Activa', icon: 'bi-play-circle-fill', css: 'text-success', help: 'Sona als esdeveniments que la fan sonar.' },
  ACABADA: { text: 'Acabada', icon: 'bi-stop-circle', css: 'text-secondary', help: 'Ja no sona. Es conserva per als certificats.' },
};

/** Els repartiments, en l'ordre del desplegable, amb l'explicació. */
export const SELECTIONS = [
  ['ALTERNA', 'Alterna', 'Les falques vàlides de cada passi, a parts iguals (i també repartides per hores).'],
  ['PES', 'Pes', 'Cada falca, segons el seu pes: una de pes 2 sona el doble que una de pes 1.'],
  ['SEQUENCIA', 'Seqüència', 'Sempre en l\'ordre de la llista (A, B, C, A, B, C…), per a falques que són capítols.'],
];

/** Pes màxim d'una falca (com el motor). */
export const MAX_WEIGHT = 10;

/** Una campanya nova: del dia d'avui a final de mes, esborrany, alterna. @param {string} today 'AAAA-MM-DD' @returns {FormCampaign} */
export function newCampaign(today) {
  const [y, m] = today.split('-').map(Number);
  const last = new Date(y, m, 0).getDate(); // l'últim dia del mes
  return {
    name: '', advertiser: '', startDate: today, endDate: `${today.slice(0, 8)}${String(last).padStart(2, '0')}`,
    selection: 'ALTERNA', state: 'ESBORRANY', contractedTotal: '', contractedPerDay: '', notes: '', spots: [],
  };
}

/** Una falca nova, a partir d'una pista del cercador (sense dates pròpies, tots els dies, pes 1). @param {any} track @returns {FormSpot} */
export function newSpot(track) {
  return { trackId: track.id, startDate: '', endDate: '', daysMask: 127, weight: 1, track };
}

/** Una campanya de campaigns.get al format del formulari. @param {any} c @returns {FormCampaign} */
export function campaignFromApi(c) {
  return {
    name: c.name ?? '',
    advertiser: c.advertiser ?? '',
    startDate: c.startDate ?? '',
    endDate: c.endDate ?? '',
    selection: c.selection ?? 'ALTERNA',
    state: c.state ?? 'ESBORRANY',
    contractedTotal: c.contractedTotal == null ? '' : String(c.contractedTotal),
    contractedPerDay: c.contractedPerDay == null ? '' : String(c.contractedPerDay),
    notes: c.notes ?? '',
    spots: (c.spots ?? []).map((/** @type {any} */ s) => ({
      trackId: s.trackId, startDate: s.startDate ?? '', endDate: s.endDate ?? '', daysMask: s.daysMask ?? 127, weight: s.weight ?? 1, track: s.track ?? null,
    })),
  };
}

/** El formulari al format de campaigns.save (sense `campaignId`). @param {FormCampaign} f */
export function campaignToApi(f) {
  const count = (/** @type {string} */ text) => (text.trim() === '' ? null : Number(text));
  return {
    name: f.name.trim(),
    advertiser: f.advertiser.trim() || null,
    startDate: f.startDate,
    endDate: f.endDate,
    selection: f.selection,
    state: f.state,
    contractedTotal: count(f.contractedTotal),
    contractedPerDay: count(f.contractedPerDay),
    notes: f.notes.trim() || null,
    spots: f.spots.map((s) => ({
      trackId: s.trackId, startDate: s.startDate || null, endDate: s.endDate || null, daysMask: s.daysMask, weight: s.weight,
    })),
  };
}

/**
 * Si el formulari és diferent de la campanya desada (per saber si hi ha canvis sense desar).
 * @param {any | null} saved  la de campaigns.get (null = campanya nova) @param {FormCampaign} f
 */
export function campaignChanged(saved, f) {
  if (!saved) return true;
  return JSON.stringify(campaignToApi(f)) !== JSON.stringify(campaignToApi(campaignFromApi(saved)));
}

/**
 * Problemes que impedeixen desar (en català), els mateixos que comprova el motor. Buit si es pot desar.
 * @param {FormCampaign} f @returns {string[]}
 */
export function campaignProblems(f) {
  const problems = [];
  if (f.name.trim() === '') problems.push('Falta el nom de la campanya.');
  if (!f.startDate || !f.endDate) problems.push('Falten les dates de la campanya.');
  else if (f.endDate < f.startDate) problems.push('La data de fi de la campanya és anterior a la d\'inici.');
  for (const [text, what] of [[f.contractedTotal, 'Els passis contractats'], [f.contractedPerDay, 'Els passis contractats per dia']]) {
    if (text.trim() !== '' && !(Number.isInteger(Number(text)) && Number(text) >= 0)) problems.push(`${what} han de ser un nombre enter, 0 o més.`);
  }
  f.spots.forEach((s, i) => {
    const which = `La falca ${i + 1}`;
    if (s.daysMask === 0) problems.push(`${which} no té cap dia de la setmana.`);
    if (!(Number.isInteger(s.weight) && s.weight >= 1 && s.weight <= MAX_WEIGHT)) problems.push(`${which}: el pes ha de ser entre 1 i ${MAX_WEIGHT}.`);
    const start = s.startDate || f.startDate;
    const end = s.endDate || f.endDate;
    if (start && end && end < start) problems.push(`${which}: la data de fi és anterior a la d'inici.`);
    else if (f.startDate && f.endDate && (start < f.startDate || end > f.endDate)) problems.push(`${which}: les dates han de ser dins de les de la campanya.`);
  });
  return problems;
}

/** Mou un element d'una llista a una altra posició (arrossegant). @template T @param {T[]} list @param {number} from @param {number} to */
export function moveSpot(list, from, to) {
  if (from === to || from < 0 || from >= list.length || to < 0 || to >= list.length) return list;
  const copy = [...list];
  const [item] = copy.splice(from, 1);
  copy.splice(to, 0, item);
  return copy;
}

/**
 * Avisos d'una falca (no impedeixen desar): la pista no existeix, el fitxer falta, està desactivada o les seves dates no
 * cobreixen les de la campanya.
 * @param {FormSpot} s @param {FormCampaign} f @returns {string[]}
 */
export function spotWarnings(s, f) {
  const t = s.track;
  if (!t) return ['La pista ja no existeix.'];
  const warnings = [];
  if (t.fileState === 'missing') warnings.push('El fitxer falta: no sonarà fins que hi torni (en sonarà una altra).');
  if (t.enabled === false) warnings.push('La pista està desactivada: no sonarà.');
  const start = s.startDate || f.startDate;
  const end = s.endDate || f.endDate;
  if (t.validFrom && start && t.validFrom > start) warnings.push(`La pista només sona a partir del ${dmy(t.validFrom)}.`);
  if (t.validUntil && end && t.validUntil < end) warnings.push(`La pista només sona fins al ${dmy(t.validUntil)}.`);
  return warnings;
}

/** 'AAAA-MM-DD' → '24/11/2026'. @param {string} iso */
export function dmy(iso) {
  const [y, m, d] = iso.split('-');
  return `${Number(d)}/${Number(m)}/${y}`;
}

/** «del 1/11/2026 al 30/11/2026». @param {string} start @param {string} end */
export function datesText(start, end) {
  return start && end ? `del ${dmy(start)} al ${dmy(end)}` : '';
}

/**
 * Resum del pla de passis (campaigns.plan) per a l'editor: quants n'hi ha, quants queden buits i si quadren amb els
 * contractats (en total i per dia). `days`: quants dies tenen passis; `daysOff`: quants dies en tenen més o menys dels
 * contractats per dia.
 * @param {{ at: string, trackId: number | null }[]} passes @param {number | null} contractedTotal @param {number | null} contractedPerDay
 */
export function planSummary(passes, contractedTotal, contractedPerDay) {
  const total = passes.length;
  const empty = passes.filter((p) => p.trackId == null).length;
  /** @type {Map<string, number>} */
  const perDay = new Map();
  for (const p of passes) perDay.set(p.at.slice(0, 10), (perDay.get(p.at.slice(0, 10)) ?? 0) + 1); // hora local: el dia és el del text
  const daysOff = contractedPerDay == null ? 0 : [...perDay.values()].filter((n) => n !== contractedPerDay).length;
  /** @type {string[]} */
  const warnings = [];
  if (contractedTotal != null && total !== contractedTotal)
    warnings.push(`El pla té ${total} ${total === 1 ? 'passi' : 'passis'} i se n'han contractat ${contractedTotal} (${total > contractedTotal ? `${total - contractedTotal} de més` : `${contractedTotal - total} de menys`}).`);
  if (daysOff > 0)
    warnings.push(`${daysOff} ${daysOff === 1 ? 'dia no té' : 'dies no tenen'} els ${contractedPerDay} passis per dia contractats.`);
  if (empty > 0)
    warnings.push(`${empty} ${empty === 1 ? 'passi queda buit' : 'passis queden buits'}: aquell dia no hi ha cap falca vàlida.`);
  return { total, empty, days: perDay.size, daysOff, warnings };
}
