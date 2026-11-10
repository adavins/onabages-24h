// @ts-check
// Cua d'emissió: les pistes que sonaran, amb el tipus, l'origen i la durada, i un botó per treure'n una
// (amb confirmació). L'ordre i el contingut els decideix el motor: aquí només es mostren (queue.changed).

import { $, el, icon } from './dom.js';
import { confirmAction } from './confirm.js';
import { formatDuration, typeName, originName } from '../format.js';

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

  /** @param {any[]} upcoming  pistes que sonaran (docs/protocol.md, 4.2) */
  render(upcoming) {
    const list = $('queue-list');
    list.replaceChildren(...upcoming.map((item, index) => this.#row(item, index)));
    $('queue-empty').classList.toggle('d-none', upcoming.length > 0);

    const known = upcoming.filter((i) => i.durationMs != null);
    const total = known.reduce((sum, i) => sum + i.durationMs, 0);
    $('queue-summary').textContent = upcoming.length === 0
      ? ''
      : `${upcoming.length} ${upcoming.length === 1 ? 'pista' : 'pistes'} · ${formatDuration(total)}${known.length < upcoming.length ? ' o més' : ''}`;
  }

  /** @param {any} item  @param {number} index */
  #row(item, index) {
    const row = el('li', 'list-group-item queue-row');

    const number = el('span', 'queue-number', String(index + 1));
    const text = el('div', 'queue-text');
    text.append(el('div', 'queue-title', item.title || '(sense títol)'), el('div', 'queue-artist', item.artist || ''));

    const origin = el('span', 'queue-origin');
    origin.append(icon(ORIGIN_ICONS[item.origin] ?? 'bi-hand-index'), ' ',
      el('span', '', item.eventName ? `Esdeveniment «${item.eventName}»` : originName(item.origin)));

    const duration = el('span', 'queue-duration', formatDuration(item.durationMs));

    const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-danger queue-remove'));
    remove.type = 'button';
    remove.title = 'Treu aquesta pista de la cua';
    remove.append(icon('bi-x-lg'), ' ', el('span', '', 'Treu'));
    remove.addEventListener('click', () => this.#remove(item, remove));

    row.append(number, text, typeBadge(item.type), origin, duration, remove);
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
