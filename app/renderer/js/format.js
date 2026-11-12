// @ts-check
// Funcions de format per a la interfície. No toquen la pàgina: es proven amb «npm test» (app/tests/format.test.mjs).

/**
 * Temps en mil·lisegons com a «m:ss» (o «h:mm:ss» si passa d'una hora). Arrodoneix cap avall, com un rellotge:
 * per al temps que falta, fes servir {@link formatRemaining}.
 * @param {number | null | undefined} ms
 */
export function formatDuration(ms) {
  if (ms == null || !Number.isFinite(ms)) return '–';
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const ss = String(s).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
}

/**
 * Temps que falta, arrodonit cap amunt: es veu «0:01» fins a l'últim instant i «0:00» només quan s'ha acabat.
 * @param {number | null | undefined} ms
 */
export function formatRemaining(ms) {
  if (ms == null || !Number.isFinite(ms)) return '–';
  return formatDuration(Math.ceil(Math.max(0, ms) / 1000) * 1000);
}

/** Noms dels tipus de pista (docs/protocol.md, 4.2), com a les maquetes. */
const TYPE_NAMES = {
  music: 'Música',
  sweeper: 'Sweeper',
  stationId: 'ID emissora',
  promo: 'Promo',
  commercial: 'Falca',
  program: 'Programa',
  news: 'Informatiu',
  timeAnnouncement: 'Locució horària',
  timeSignal: 'Senyal horari',
  stream: 'Stream',
  other: 'Altres',
};

/** @param {string | null | undefined} type */
export function typeName(type) {
  return (type && TYPE_NAMES[type]) || 'Altres';
}

/** D'on ve un element de la cua (D-034). */
const ORIGIN_NAMES = { autodj: 'AutoDJ', manual: 'Manual', event: 'Esdeveniment' };

/** @param {string | null | undefined} origin */
export function originName(origin) {
  return (origin && ORIGIN_NAMES[origin]) || 'Manual';
}

/**
 * Nivell en dBFS (−90 = silenci) a fracció de la barra del vúmetre (0..1). L'escala va de −60 dB a 0 dB.
 * @param {number} db
 */
export function meterFraction(db) {
  const MIN_DB = -60;
  if (!Number.isFinite(db) || db <= MIN_DB) return 0;
  return Math.min(1, (db - MIN_DB) / -MIN_DB);
}

/**
 * Un guany en dB, amb signe i coma decimal: «+2 dB», «0 dB», «−3,5 dB» (el volum del processador, fase 3, pas 6).
 * @param {number} db
 */
export function formatGainDb(db) {
  const rounded = Math.round(db * 10) / 10;
  if (rounded === 0) return '0 dB';
  const text = String(Math.abs(rounded)).replace('.', ',');
  return `${rounded > 0 ? '+' : '−'}${text} dB`;
}

/**
 * Data llarga en català: «dimarts, 10 de novembre de 2026».
 * @param {Date} date
 */
export function formatLongDate(date) {
  return new Intl.DateTimeFormat('ca', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }).format(date);
}

/**
 * Hora «HH:MM:SS».
 * @param {Date} date
 */
export function formatClock(date) {
  return [date.getHours(), date.getMinutes(), date.getSeconds()].map((n) => String(n).padStart(2, '0')).join(':');
}

/**
 * Quan va sonar per última vegada, curt (fase 3, pas 3b): «fa 5 min», «fa 3 h», «ahir», «fa 4 dies», «fa 2 mesos»,
 * «mai». Comptant dies de calendari (no de 24 h): «ahir» és el dia d'abans, sigui l'hora que sigui.
 * @param {string | null | undefined} iso  @param {Date} now
 */
export function playedAgo(iso, now) {
  if (!iso) return 'mai';
  const then = new Date(iso);
  const minutes = Math.floor((now.getTime() - then.getTime()) / 60_000);
  if (minutes < 1) return 'ara';
  if (minutes < 60) return `fa ${minutes} min`;
  const day = (/** @type {Date} */ d) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((day(now) - day(then)) / 86_400_000);
  if (days === 0) return `fa ${Math.floor(minutes / 60)} h`;
  if (days === 1) return 'ahir';
  if (days < 45) return `fa ${days} dies`;
  const months = Math.round(days / 30);
  return months < 12 ? `fa ${months} mesos` : `fa ${Math.floor(days / 365)} ${Math.floor(days / 365) === 1 ? 'any' : 'anys'}`;
}
