// @ts-check
// Previsió de la cua fins al pròxim TALLA (fase 3, pas 1; pantalla-principal-i-encadenament.md, millora 1). Les hores
// previstes de cada pista les calcula el motor (plannedAt, currentEndsAt); aquí només es compara amb l'hora del pròxim
// TALLA per marcar què es tallarà i què no sonarà. No toca la pàgina: es prova amb «npm test» (app/tests/forecast.test.mjs).

/**
 * @typedef {{ eventId: number, name: string, mode: string, date: string, at: string, time: string, clearQueue?: boolean }} UpcomingEvent
 * @typedef {'ok' | 'cut' | 'lost' | 'after'} ItemState
 *   ok: sona sencera abans del tall · cut: comença però la tallaran · lost: no sonarà (el TALLA esborra la cua) ·
 *   after: sonarà després del contingut del TALLA (el TALLA no esborra la cua)
 * @typedef {{ state: ItemState, playsMs: number | null }} ItemForecast
 * @typedef {{
 *   cut: UpcomingEvent | null,
 *   current: { playsMs: number, durationMs: number } | null,
 *   items: ItemForecast[],
 *   lost: number,
 *   markerIndex: number,
 * }} QueueForecast
 */

/** El pròxim TALLA a partir d'ara, o null. @param {UpcomingEvent[]} events @param {number} nowMs */
export function nextCut(events, nowMs) {
  return events.find((e) => e.mode === 'TALLA' && Date.parse(e.at) > nowMs) ?? null;
}

/**
 * Què passarà amb la que sona i amb cada pista de la cua quan arribi el pròxim TALLA.
 * @param {{ current: any, currentEndsAt: string | null, upcoming: any[] }} queue  la cua del motor (docs/protocol.md, 4.2)
 * @param {UpcomingEvent[]} events  pròxims esdeveniments (scheduler.occurrences), per ordre
 * @param {number} nowMs
 * @returns {QueueForecast}
 */
export function forecastQueue(queue, events, nowMs) {
  const cut = nextCut(events, nowMs);
  const items = queue.upcoming.map(() => /** @type {ItemForecast} */ ({ state: 'ok', playsMs: null }));
  /** @type {QueueForecast} */
  const result = { cut, current: null, items, lost: 0, markerIndex: queue.upcoming.length };
  if (!cut) return result;
  const cutAt = Date.parse(cut.at);

  // La que sona: si s'acaba després del tall, se'n sentirà fins al tall
  if (queue.current && queue.currentEndsAt) {
    const endsAt = Date.parse(queue.currentEndsAt);
    const duration = queue.current.durationMs;
    if (endsAt > cutAt && duration != null) {
      result.current = { playsMs: Math.max(0, duration - (endsAt - cutAt)), durationMs: duration };
    }
  }

  let markerSet = false;
  queue.upcoming.forEach((item, index) => {
    if (item.plannedAt == null) return; // sense hora prevista: no es pot dir
    const startsAt = Date.parse(item.plannedAt);
    if (startsAt >= cutAt) {
      // Comença després del tall: si el TALLA esborra la cua, no sonarà; si no, sonarà després del seu contingut
      items[index].state = cut.clearQueue === false ? 'after' : 'lost';
      if (items[index].state === 'lost') result.lost++;
      if (!markerSet) {
        result.markerIndex = index;
        markerSet = true;
      }
      return;
    }
    if (item.durationMs != null && startsAt + item.durationMs > cutAt) {
      items[index].state = 'cut';
      items[index].playsMs = cutAt - startsAt;
    }
  });
  return result;
}

/**
 * «d'aquí 3:20», «d'aquí 2 h 11 min», «ara».
 * @param {number} ms
 */
export function untilText(ms) {
  if (ms <= 0) return 'ara';
  const totalSeconds = Math.ceil(ms / 1000);
  if (totalSeconds < 3600) {
    const m = Math.floor(totalSeconds / 60);
    const s = totalSeconds % 60;
    return `d'aquí ${m}:${String(s).padStart(2, '0')}`;
  }
  const minutes = Math.floor(totalSeconds / 60);
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m === 0 ? `d'aquí ${h} h` : `d'aquí ${h} h ${m} min`;
}

/**
 * «avui», «demà» o «dv 20/11», segons el dia d'una data «AAAA-MM-DD».
 * @param {string} date  @param {Date} now
 */
export function dayText(date, now) {
  const [y, m, d] = date.split('-').map(Number);
  const target = new Date(y, m - 1, d);
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days = Math.round((target.getTime() - today.getTime()) / 86_400_000);
  if (days === 0) return 'avui';
  if (days === 1) return 'demà';
  return `${['dg', 'dl', 'dt', 'dc', 'dj', 'dv', 'ds'][target.getDay()]} ${d}/${m}`;
}

/** «12:58:53» a partir d'una hora ISO. @param {string | null | undefined} iso */
export function clockOf(iso) {
  if (!iso) return '–';
  const d = new Date(iso);
  return [d.getHours(), d.getMinutes(), d.getSeconds()].map((n) => String(n).padStart(2, '0')).join(':');
}
