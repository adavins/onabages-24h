// @ts-check
// Cerca a l'historial (fase 4, pas 5b; D-143): els paràmetres de history.list a partir dels filtres de la pestanya
// «Historial» i els textos de cada fila. No toca la pàgina: es prova amb «npm test» (app/tests/history-form.test.mjs).

/** Quantes files es demanen cada vegada («Carrega'n més» en demana tantes com aquestes). */
export const PAGE_SIZE = 200;

/**
 * Els filtres de la pestanya. Les dates són dies locals 'AAAA-MM-DD' (totes dues incloses) o '' (sense límit).
 * @typedef {{
 *   from: string, to: string, text: string, type: string, origin: string,
 *   track: { id: number, name: string } | null,
 * }} HistoryFilters
 */

/**
 * Els paràmetres de history.list. Amb <beforeId>, la pàgina següent (les que van sonar abans d'aquesta entrada); sense,
 * la primera, amb el total.
 * @param {HistoryFilters} f @param {number | null} [beforeId]
 */
export function historyParams(f, beforeId = null) {
  /** @type {Record<string, unknown>} */
  const params = { limit: PAGE_SIZE };
  if (f.from) params.from = f.from;
  if (f.to) params.to = f.to;
  if (f.text.trim()) params.search = f.text.trim();
  if (f.type) params.types = [f.type];
  if (f.origin) params.origins = [f.origin];
  if (f.track) params.trackId = f.track.id;
  if (beforeId != null) params.beforeId = beforeId;
  else params.count = true;
  return params;
}

/** Les dates del filtre, si no es contradiuen; si no, el motiu. @param {HistoryFilters} f */
export function periodProblem(f) {
  if (f.from && f.to && f.from > f.to) return 'La data «Des de» és posterior a «Fins a».';
  return null;
}

/** @param {number} n */
const two = (n) => String(n).padStart(2, '0');

/**
 * L'hora d'inici (UTC, del motor) en hora local: el dia 'dd/mm/aaaa' i l'hora 'hh:mm:ss'.
 * @param {string} iso
 */
export function localDateTime(iso) {
  const d = new Date(iso);
  return {
    date: `${d.getDate()}/${d.getMonth() + 1}/${d.getFullYear()}`,
    time: `${two(d.getHours())}:${two(d.getMinutes())}:${two(d.getSeconds())}`,
  };
}

/**
 * Si va sonar sencera: «Sí», «No» (es va tallar o saltar), «Sonant» (encara sona) o «No se sap» (el motor es va aturar
 * mentre sonava: durada 0). Amb la classe de color i la icona (mai només el color).
 * @param {{ playedMs: number | null, complete: boolean | null }} e
 * @returns {{ text: string, className: string, icon: string }}
 */
export function completeState(e) {
  if (e.playedMs == null) return { text: 'Sonant', className: 'text-success', icon: 'bi-play-fill' };
  if (e.complete) return { text: 'Sí', className: 'text-body-secondary', icon: 'bi-check-lg' };
  if (e.playedMs === 0) return { text: 'No se sap', className: 'text-body-secondary', icon: 'bi-question-circle' };
  return { text: 'No', className: 'text-warning', icon: 'bi-scissors' };
}

/**
 * El resum de sota els filtres: «350 emissions» o «Se'n mostren 200 de 350».
 * @param {number} shown @param {number | null} total
 */
export function summaryText(shown, total) {
  const word = (/** @type {number} */ n) => (n === 1 ? 'emissió' : 'emissions');
  if (total == null || total === shown) return `${shown} ${word(shown)}`;
  return `Se'n mostren ${shown} de ${total} ${word(total)}`;
}
