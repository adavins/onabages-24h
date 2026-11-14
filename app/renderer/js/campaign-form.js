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
 * Els dies del calendari de <start> a <end> (tots dos inclosos), 'AAAA-MM-DD'; com a molt 400.
 * @param {string} start @param {string} end
 */
export function datesBetween(start, end) {
  /** @type {string[]} */
  const days = [];
  if (!start || !end || end < start) return days;
  const d = new Date(`${start}T00:00:00Z`); // en UTC: sense canvis d'hora
  for (let i = 0; i <= 400; i++) {
    const iso = d.toISOString().slice(0, 10);
    if (iso > end) break;
    days.push(iso);
    d.setUTCDate(d.getUTCDate() + 1);
  }
  return days;
}

/**
 * Les setmanes d'un mes, de dilluns a diumenge: cada setmana, 7 dies 'AAAA-MM-DD' o null (fora del mes).
 * @param {number} year @param {number} month 1–12
 */
export function monthWeeks(year, month) {
  const first = new Date(Date.UTC(year, month - 1, 1));
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const offset = (first.getUTCDay() + 6) % 7; // 0 = dilluns
  /** @type {(string | null)[][]} */
  const weeks = [];
  let week = /** @type {(string | null)[]} */ (Array(offset).fill(null));
  for (let day = 1; day <= daysInMonth; day++) {
    week.push(`${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`);
    if (week.length === 7) {
      weeks.push(week);
      week = [];
    }
  }
  if (week.length > 0) weeks.push([...week, ...Array(7 - week.length).fill(null)]);
  return weeks;
}

/**
 * Resum del pla d'una campanya (campaigns.plan; pas 3d-2): el que **ja ha sonat** (de l'historial: un passi d'abans que
 * no va sonar no compta) i el que **està previst** (el pla d'ara endavant), dia a dia, i si quadra amb els passis
 * contractats (en total i per dia, dins de les dates de la campanya).
 * - `byDay`: per a cada dia amb alguna cosa, quants n'han sonat, quants en són previstos, quants queden buits i quins
 *   (hora, títol i si ha sonat), en ordre.
 * - `pastOff` / `futureOff`: dies de la campanya (ja passats / d'avui en endavant) que no tenen els passis per dia contractats.
 * @param {{ passes: { at: string, trackId: number | null, title?: string | null }[], played?: { at: string, trackId: number | null, title?: string | null }[] }} plan
 * @param {{ startDate: string, endDate: string, contractedTotal?: number | null, contractedPerDay?: number | null }} campaign
 * @param {string} today  'AAAA-MM-DD'
 */
export function planSummary(plan, campaign, today) {
  const played = plan.played ?? [];
  const planned = plan.passes;
  const empty = planned.filter((p) => p.trackId == null).length;
  /** @type {Map<string, { played: number, planned: number, empty: number, items: { time: string, title: string | null, played: boolean }[] }>} */
  const byDay = new Map();
  const day = (/** @type {string} */ at) => {
    const key = at.slice(0, 10); // hora local: el dia és el del text
    let d = byDay.get(key);
    if (!d) byDay.set(key, d = { played: 0, planned: 0, empty: 0, items: [] });
    return d;
  };
  for (const p of played) {
    const d = day(p.at);
    d.played++;
    d.items.push({ time: p.at.slice(11, 16), title: p.title ?? null, played: true });
  }
  for (const p of planned) {
    const d = day(p.at);
    d.planned++;
    if (p.trackId == null) d.empty++;
    d.items.push({ time: p.at.slice(11, 16), title: p.trackId == null ? null : p.title ?? null, played: false });
  }
  for (const d of byDay.values()) d.items.sort((a, b) => a.time.localeCompare(b.time));

  const total = played.length + planned.length;
  const perDay = campaign.contractedPerDay ?? null;
  let pastOff = 0;
  let futureOff = 0;
  if (perDay != null) {
    for (const date of datesBetween(campaign.startDate, campaign.endDate)) {
      const d = byDay.get(date);
      if ((d ? d.played + d.planned : 0) === perDay) continue;
      if (date < today) pastOff++;
      else futureOff++;
    }
  }

  /** @type {string[]} */
  const warnings = [];
  const contracted = campaign.contractedTotal ?? null;
  if (contracted != null && total !== contracted) {
    const diff = total > contracted ? `${total - contracted} de més` : `${contracted - total} de menys`;
    warnings.push(`Entre els que han sonat (${played.length}) i els previstos (${planned.length}) en surten ${total}, i se n'han contractat ${contracted} (${diff}).`);
  }
  if (pastOff > 0)
    warnings.push(`${pastOff} ${pastOff === 1 ? 'dia que ja ha passat no va tenir' : 'dies que ja han passat no van tenir'} els ${perDay} passis per dia contractats.`);
  if (futureOff > 0)
    warnings.push(`${futureOff} ${futureOff === 1 ? 'dia no tindrà' : 'dies no tindran'} els ${perDay} passis per dia contractats.`);
  if (empty > 0)
    warnings.push(`${empty} ${empty === 1 ? 'passi previst queda buit' : 'passis previstos queden buits'}: aquell dia no hi ha cap falca vàlida.`);
  return { played: played.length, planned: planned.length, total, empty, byDay, days: byDay.size, pastOff, futureOff, warnings };
}
