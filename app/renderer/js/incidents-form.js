// @ts-check
// Incidències (fase 5, pas 1b; D-156): textos, colors i icones de la finestra «Incidències», sense tocar la pàgina (es
// proven amb node --test). Les incidències les desa el motor (docs/protocol.md, 4.13).

/**
 * @typedef {{ id: number, kind: string, severity: 'danger' | 'warning' | 'info', text: string, detail: string | null,
 *   startedAt: string, endedAt: string | null, open: boolean, recovered: boolean }} Incident
 */

/** Els tipus, en l'ordre del desplegable. */
export const KINDS = [
  ['SORTIDA', 'Sortida d\'àudio'],
  ['FITXER', 'Fitxer que no sona'],
  ['ENCALLADA', 'Pista encallada'],
  ['TALLS', 'Talls de so'],
  ['SILENCI', 'Silenci a la sortida'],
  ['EMERGENCIA', 'Pista d\'emergència'],
  ['REINICI', 'Reinici inesperat'],
  ['ESDEVENIMENT_OMES', 'Esdeveniment omès'],
  ['ESDEVENIMENT_ERROR', 'Esdeveniment fallat'],
  ['ESDEVENIMENT_TARD', 'Esdeveniment tard'],
  ['CARPETA', 'Carpeta vigilada'],
  ['AUTODJ', 'AutoDJ'],
  ['PAUSA', 'Pausa'],
  ['BUTT', 'Títol per a BUTT'],
  ['CORREU', 'Avisos per correu'],
  ['STREAM', 'Stream'],
];

/** El nom d'un tipus («Sortida d'àudio»); si és un de nou que la interfície no coneix, el codi. @param {string} kind */
export function kindText(kind) {
  return KINDS.find(([code]) => code === kind)?.[1] ?? kind;
}

/** La gravetat: text, color i icona (sempre color + icona + text). @param {string} severity */
export function severityInfo(severity) {
  if (severity === 'danger') return { text: 'Greu', className: 'text-danger', icon: 'bi-exclamation-octagon-fill' };
  if (severity === 'info') return { text: 'Informació', className: 'text-info', icon: 'bi-info-circle-fill' };
  return { text: 'Avís', className: 'text-warning', icon: 'bi-exclamation-triangle-fill' };
}

/** Una incidència d'un moment: s'ha desat ja tancada (comença i acaba alhora). @param {Incident} i */
export function isMoment(i) {
  return !i.open && i.endedAt != null && new Date(i.endedAt).getTime() - new Date(i.startedAt).getTime() < 1000;
}

/**
 * L'estat: «Oberta», «Resolta», «Recuperada sola» (el motor ho ha resolt, p. ex. ha saltat la pista) o «Anotada» (ha
 * passat un moment i no hi ha res a resoldre, p. ex. un esdeveniment omès).
 * @param {Incident} i
 */
export function stateInfo(i) {
  if (i.open) return { text: 'Oberta', className: 'text-danger', icon: 'bi-circle-fill' };
  if (i.recovered) return { text: 'Recuperada sola', className: 'text-success', icon: 'bi-arrow-repeat' };
  if (isMoment(i)) return { text: 'Anotada', className: 'text-body-secondary', icon: 'bi-journal-text' };
  return { text: 'Resolta', className: 'text-success', icon: 'bi-check-circle' };
}

/** Dos dígits. @param {number} n */
function two(n) {
  return String(n).padStart(2, '0');
}

/** «16/11/2026 10:05:09» (hora d'aquest ordinador). @param {string} iso */
export function dateTimeText(iso) {
  const d = new Date(iso);
  return `${d.getDate()}/${d.getMonth() + 1}/${d.getFullYear()} ${two(d.getHours())}:${two(d.getMinutes())}:${two(d.getSeconds())}`;
}

/** «45 s», «3 min 20 s», «2 h 5 min», «3 d 4 h». @param {number} ms */
export function spanText(ms) {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s} s`;
  const min = Math.floor(s / 60);
  if (min < 60) return s % 60 ? `${min} min ${s % 60} s` : `${min} min`;
  const h = Math.floor(min / 60);
  if (h < 24) return min % 60 ? `${h} h ${min % 60} min` : `${h} h`;
  const d = Math.floor(h / 24);
  return h % 24 ? `${d} d ${h % 24} h` : `${d} d`;
}

/**
 * Quant ha durat: «3 min 20 s»; si és oberta, «fa 3 min 20 s»; si és d'un moment, «—».
 * @param {Incident} i @param {Date} now
 */
export function durationText(i, now) {
  if (i.open) return `fa ${spanText(now.getTime() - new Date(i.startedAt).getTime())}`;
  if (isMoment(i) || i.endedAt == null) return '—';
  return spanText(new Date(i.endedAt).getTime() - new Date(i.startedAt).getTime());
}

/** Els períodes del desplegable. */
export const PERIODS = [
  ['1', 'Avui'],
  ['7', 'Últims 7 dies'],
  ['30', 'Últims 30 dies'],
  ['365', 'Últim any'],
];

/**
 * El primer dia d'un període («AAAA-MM-DD», en dies de calendari locals): «1» = avui, «7» = avui i els 6 d'abans…
 * @param {string} period @param {Date} today
 */
export function periodFrom(period, today) {
  const days = Math.max(1, Number(period) || 1);
  const d = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (days - 1));
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`;
}

/** «Cap incidència», «1 incidència», «1.234 incidències». @param {number} n */
export function countText(n) {
  if (n === 0) return 'Cap incidència';
  return `${n.toLocaleString('ca-ES')} ${n === 1 ? 'incidència' : 'incidències'}`;
}
