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

/** Quantes files com a molt es desen en un PDF o un CSV (de 2.000 en 2.000). */
export const EXPORT_LIMIT = 20_000;

/** 'AAAA-MM-DD' → 'd/m/aaaa'. @param {string} iso */
function dayText(iso) {
  const [y, m, d] = iso.split('-').map(Number);
  return `${d}/${m}/${y}`;
}

/**
 * Els filtres en una frase, per a la capçalera del PDF: «Del 1/11/2026 al 15/11/2026 · Tipus: Falca · …».
 * @param {HistoryFilters} f
 * @param {(type: string) => string} typeName @param {(origin: string) => string} originName
 */
export function filtersText(f, typeName, originName) {
  const parts = [];
  if (f.from && f.to) parts.push(f.from === f.to ? `El ${dayText(f.from)}` : `Del ${dayText(f.from)} al ${dayText(f.to)}`);
  else if (f.from) parts.push(`Des del ${dayText(f.from)}`);
  else if (f.to) parts.push(`Fins al ${dayText(f.to)}`);
  else parts.push('Totes les dates');
  if (f.track) parts.push(`Pista: ${f.track.name}`);
  if (f.text.trim()) parts.push(`Text: «${f.text.trim()}»`);
  if (f.type) parts.push(`Tipus: ${typeName(f.type)}`);
  if (f.origin) parts.push(`Origen: ${originName(f.origin)}`);
  return parts.join(' · ');
}

/**
 * El nom proposat per al fitxer: «Historial - Ferreteria - Falca - del 2026-11-01 al 2026-11-15.csv». Sense els
 * caràcters que Windows no admet en un nom de fitxer.
 * @param {HistoryFilters} f @param {'pdf' | 'csv'} extension
 */
export function historyFileName(f, extension) {
  const parts = ['Historial'];
  if (f.track) parts.push(f.track.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '').trim().slice(0, 80));
  if (f.from && f.to) parts.push(f.from === f.to ? f.from : `del ${f.from} al ${f.to}`);
  else if (f.from) parts.push(`des del ${f.from}`);
  else if (f.to) parts.push(`fins al ${f.to}`);
  else parts.push('totes les dates');
  return `${parts.join(' - ')}.${extension}`;
}

/** Un camp del CSV: entre cometes si cal (punt i coma, cometes o salts de línia). @param {unknown} value */
function csvField(value) {
  const text = String(value ?? '');
  return /[;"\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/**
 * La llista en CSV, per obrir-la amb un full de càlcul: separada per punt i coma (com la llegeix l'Excel en català),
 * amb salts de línia de Windows. La durada va en m:ss i en segons (per poder sumar). El procés principal hi afegeix la
 * marca UTF-8 perquè els accents es vegin bé.
 * @param {any[]} entries
 * @param {(type: string) => string} typeName @param {(origin: string) => string} originName
 * @param {(ms: number) => string} formatDuration
 */
export function historyCsv(entries, typeName, originName, formatDuration) {
  const head = ['Data', 'Hora', 'Artista', 'Títol', 'Tipus', 'Origen', 'Campanya', 'Ha sonat', 'Segons', 'Sencera', 'Fitxer'];
  const rows = entries.map((e) => {
    const { date, time } = localDateTime(e.startedAt);
    return [
      date, time, e.artist ?? '', e.title ?? '', e.type ? typeName(e.type) : '', originName(e.origin), e.campaignName ?? '',
      e.playedMs == null ? '' : formatDuration(e.playedMs), e.playedMs == null ? '' : Math.round(e.playedMs / 1000),
      completeState(e).text, e.path ?? '',
    ];
  });
  return [head, ...rows].map((r) => r.map(csvField).join(';')).join('\r\n') + '\r\n';
}
