// @ts-check
// Cua d'emissió: les pistes que sonaran, amb l'hora prevista, el tipus, l'origen i la durada, i un botó per treure'n una
// (amb confirmació). L'ordre i el contingut els decideix el motor: aquí només es mostren (queue.changed).
// Fase 3, pas 1: si el pròxim esdeveniment és un TALLA, es marca què es tallarà i què no sonarà (forecast.js).

import { $, el, icon } from './dom.js';
import { confirmAction } from './confirm.js';
import { formatDuration, typeName, originName } from '../format.js';
import { forecastQueue, clockOf } from '../forecast.js';
import { modeInfo, shortTime } from '../schedule.js';

/** Color de l'etiqueta de cada tipus (sempre amb el nom escrit al costat). */
const TYPE_BADGES = {
  music: 'text-bg-secondary',
  sweeper: 'type-jingle',
  stationId: 'type-jingle',
  promo: 'type-jingle',
  commercial: 'text-bg-warning',
  program: 'text-bg-info',
  news: 'text-bg-info',
  timeAnnouncement: 'type-time',
  timeSignal: 'type-time',
  stream: 'text-bg-info',
};

const ORIGIN_ICONS = { autodj: 'bi-shuffle', manual: 'bi-hand-index', event: 'bi-calendar-event' };

/**
 * Etiqueta del tipus de pista.
 * @param {string} type
 */
export function typeBadge(type) {
  return el('span', `badge ${TYPE_BADGES[type] ?? 'text-bg-secondary'}`, typeName(type));
}

export class QueueView {
  #send;

  /** @param {(cmd: string, params?: object) => Promise<any>} send  envia una ordre al motor */
  constructor(send) {
    this.#send = send;
  }

  /**
   * @param {any} queue  la cua del motor (docs/protocol.md, 4.2): current, currentEndsAt i upcoming amb plannedAt
   * @param {import('../forecast.js').UpcomingEvent[]} events  pròxims esdeveniments, per a la previsió
   */
  render(queue, events) {
    const upcoming = queue.upcoming;
    const forecast = forecastQueue(queue, events, Date.now());
    const list = $('queue-list');
    const rows = upcoming.map((/** @type {any} */ item, /** @type {number} */ index) => this.#row(item, index, forecast.items[index], forecast.cut));
    if (forecast.cut && (forecast.lost > 0 || forecast.items.some((i) => i.state !== 'ok') || forecast.markerIndex < upcoming.length)) {
      rows.splice(forecast.markerIndex, 0, this.#cutRow(forecast.cut));
    }
    list.replaceChildren(...rows);
    $('queue-empty').classList.toggle('d-none', upcoming.length > 0);
    this.#renderWarning(queue, forecast);

    const known = upcoming.filter((i) => i.durationMs != null);
    const total = known.reduce((sum, i) => sum + i.durationMs, 0);
    $('queue-summary').textContent = upcoming.length === 0
      ? ''
      : `${upcoming.length} ${upcoming.length === 1 ? 'pista' : 'pistes'} · ${formatDuration(total)}${known.length < upcoming.length ? ' o més' : ''}`;
  }

  /**
   * Avís a dalt de la cua (com a la maqueta): què passarà quan arribi el TALLA.
   * @param {any} queue  @param {import('../forecast.js').QueueForecast} forecast
   */
  #renderWarning(queue, forecast) {
    const box = $('queue-warning');
    const cut = forecast.cut;
    const cutItem = forecast.items.findIndex((i) => i.state === 'cut');
    if (!cut || (forecast.current === null && cutItem === -1 && forecast.lost === 0)) {
      box.classList.add('d-none');
      return;
    }
    /** @type {string[]} */
    const parts = [];
    if (forecast.current) {
      parts.push(`«${queue.current.title}», la que sona, sonarà ${formatDuration(forecast.current.playsMs)} de ${formatDuration(forecast.current.durationMs)}`);
    }
    if (cutItem !== -1) {
      const item = queue.upcoming[cutItem];
      parts.push(`«${item.title}» sonarà ${formatDuration(forecast.items[cutItem].playsMs)} de ${formatDuration(item.durationMs)}`);
    }
    if (forecast.lost > 0) parts.push(`${forecast.lost} ${forecast.lost === 1 ? 'pista no sonarà' : 'pistes no sonaran'}`);
    const text = el('span', '');
    text.append(`A les ${shortTime(cut.time)}, «${cut.name}» tallarà la cua: `, parts.join(' i '), '.');
    box.replaceChildren(icon('bi-exclamation-triangle-fill'), text);
    box.classList.remove('d-none');
  }

  /** La fila que marca on talla l'esdeveniment. @param {import('../forecast.js').UpcomingEvent} cut */
  #cutRow(cut) {
    const info = modeInfo(cut.mode);
    const row = el('li', `list-group-item queue-event-row ${info.css}`);
    row.append(icon(info.icon), ' ', el('span', 'queue-time', shortTime(cut.time)), ' ',
      el('span', '', `${info.label} · ${cut.name}${cut.clearQueue === false ? ' (no esborra la cua: el que hi ha sona després)' : ''}`));
    return row;
  }

  /**
   * @param {any} item  @param {number} index
   * @param {import('../forecast.js').ItemForecast} [forecast]  @param {any} [cut]
   */
  #row(item, index, forecast, cut) {
    const state = forecast?.state ?? 'ok';
    const row = el('li', `list-group-item queue-row${state === 'lost' ? ' is-lost' : ''}${state === 'cut' ? ' is-cut' : ''}`);

    const number = el('span', 'queue-number', String(index + 1));
    // Hora prevista d'inici (la calcula el motor); la de les que no sonaran no té sentit
    const time = el('span', 'queue-time', state === 'lost' ? '–' : clockOf(item.plannedAt));
    time.title = 'Hora prevista';
    const text = el('div', 'queue-text');
    text.append(el('div', 'queue-title', item.title || '(sense títol)'), el('div', 'queue-artist', item.artist || ''));

    const origin = el('span', 'queue-origin');
    origin.append(icon(ORIGIN_ICONS[item.origin] ?? 'bi-hand-index'), ' ',
      el('span', '', item.eventName ? `Esdeveniment «${item.eventName}»` : originName(item.origin)));

    const duration = el('span', 'queue-duration', formatDuration(item.durationMs));

    // Què li passarà amb el pròxim TALLA (sempre amb icona i text)
    /** @type {HTMLElement | string} */
    let mark = '';
    if (state === 'cut' && forecast?.playsMs != null) {
      mark = el('span', 'queue-mark text-warning');
      mark.append(icon('bi-scissors'), ' ', el('span', '', `Es tallarà a ${formatDuration(forecast.playsMs)}`));
    } else if (state === 'lost') {
      mark = el('span', 'queue-mark text-body-secondary');
      mark.append(icon('bi-scissors'), ' ', el('span', '', 'No sonarà'));
      mark.title = `«${cut?.name}» esborra la cua a les ${shortTime(cut?.time ?? '')}`;
    } else if (state === 'after') {
      mark = el('span', 'queue-mark text-body-secondary');
      mark.append(icon('bi-hourglass-split'), ' ', el('span', '', 'Després del Talla'));
    }

    const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-danger queue-remove'));
    remove.type = 'button';
    remove.title = 'Treu aquesta pista de la cua';
    remove.append(icon('bi-x-lg'), ' ', el('span', '', 'Treu'));
    remove.addEventListener('click', () => this.#remove(item, remove));

    row.append(number, time, text, typeBadge(item.type), mark, origin, duration, remove);
    return row;
  }

  /** @param {any} item  @param {HTMLButtonElement} button */
  async #remove(item, button) {
    const name = item.artist ? `«${item.title}» (${item.artist})` : `«${item.title}»`;
    const ok = await confirmAction({
      title: 'Treure de la cua',
      message: `Vols treure ${name} de la cua? No sonarà.`,
      confirm: 'Treu de la cua',
    });
    if (!ok) return;
    button.disabled = true;
    try {
      await this.#send('queue.remove', { itemId: item.id });
      // La cua nova arriba amb queue.changed
    } catch (err) {
      button.disabled = false;
      await confirmAction({
        title: 'No s\'ha pogut treure',
        message: /** @type {Error} */ (err).message,
        confirm: 'D\'acord',
        info: true,
      });
    }
  }
}

export class HistoryView {
  #send;
  #loading = false;
  #again = false;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
  }

  /** Torna a demanar les últimes pistes al motor (si ja s'estan demanant, ho torna a fer en acabar). */
  async refresh() {
    if (this.#loading) {
      this.#again = true;
      return;
    }
    this.#loading = true;
    try {
      const { entries } = await this.#send('history.list', { limit: 10 });
      this.#render(entries);
    } catch (err) {
      console.error('No s\'ha pogut llegir l\'historial:', err);
    } finally {
      this.#loading = false;
      if (this.#again) {
        this.#again = false;
        this.refresh();
      }
    }
  }

  /** @param {any[]} entries */
  #render(entries) {
    $('history-list').replaceChildren(...entries.map((e) => {
      const row = el('li', 'list-group-item history-row');
      const time = new Date(e.startedAt);
      const hhmmss = [time.getHours(), time.getMinutes(), time.getSeconds()].map((n) => String(n).padStart(2, '0')).join(':');
      const text = el('span', 'history-text');
      text.append(el('span', 'history-title', e.title || '(sense títol)'));
      if (e.artist) text.append(el('span', 'history-artist', ` · ${e.artist}`));

      let duration;
      if (e.playedMs == null) {
        duration = el('span', 'history-duration text-success');
        duration.append(icon('bi-volume-up-fill'), ' ', el('span', '', 'Sona'));
      } else {
        duration = el('span', 'history-duration', formatDuration(e.playedMs));
        if (e.complete === false) {
          duration.prepend(el('span', 'history-cut', 'tallada · '));
        }
      }
      row.append(el('span', 'history-time', hhmmss), text, duration);
      return row;
    }));
    $('history-empty').classList.toggle('d-none', entries.length > 0);
  }
}
