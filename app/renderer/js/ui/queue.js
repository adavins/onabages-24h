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
   * Accepta pistes arrossegades des de la biblioteca (fase 3, pas 3b): damunt d'una pista, s'insereixen abans o després
   * segons la meitat on es deixin anar; a la resta del quadre de la cua, al final. Es marca on aniria amb una línia.
   * @param {string} dragType  el tipus de dada de l'arrossegament (només s'accepten pistes de la biblioteca)
   * @param {(track: { id: number, title: string }, position: number | 'end') => void} onDrop
   */
  acceptDrops(dragType, onDrop) {
    const area = $('queue-card');
    const clearMarks = () => {
      for (const node of area.querySelectorAll('.drop-before, .drop-after')) node.classList.remove('drop-before', 'drop-after');
      area.classList.remove('drop-end');
    };
    /** On aniria: l'índex a la cua (0 = la primera que sonarà) i la fila que es marca, o el final. @param {DragEvent} e */
    const target = (e) => {
      const row = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest?.('.queue-row'));
      if (!row || row.dataset.index == null) return { position: /** @type {'end'} */ ('end'), row: null, after: false };
      const box = row.getBoundingClientRect();
      const after = e.clientY > box.top + box.height / 2;
      return { position: Number(row.dataset.index) + (after ? 1 : 0), row, after };
    };
    area.addEventListener('dragover', (e) => {
      if (!e.dataTransfer?.types.includes(dragType)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      clearMarks();
      const t = target(e);
      if (t.row) t.row.classList.add(t.after ? 'drop-after' : 'drop-before');
      else area.classList.add('drop-end');
    });
    area.addEventListener('dragleave', (e) => {
      if (!area.contains(/** @type {Node | null} */ (e.relatedTarget))) clearMarks();
    });
    area.addEventListener('drop', (e) => {
      const data = e.dataTransfer?.getData(dragType);
      clearMarks();
      if (!data) return;
      e.preventDefault();
      const t = target(e);
      // Una posició més enllà de l'última és el mateix que al final
      onDrop(JSON.parse(data), typeof t.position === 'number' && t.position >= this.#count ? 'end' : t.position);
    });
  }

  /**
   * @param {any} queue  la cua del motor (docs/protocol.md, 4.2): current, currentEndsAt i upcoming amb plannedAt
   * @param {import('../forecast.js').UpcomingEvent[]} events  pròxims esdeveniments, per a la previsió
   */
  render(queue, events) {
    const upcoming = queue.upcoming;
    this.#items = upcoming;
    this.#count = upcoming.length;
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
    row.dataset.index = String(index); // per saber on es deixa anar una pista arrossegada

    const number = el('span', 'queue-number', String(index + 1));
    // Hora prevista d'inici (la calcula el motor); la de les que no sonaran no té sentit
    const time = el('span', 'queue-time', state === 'lost' ? '–' : clockOf(item.plannedAt));
    time.title = 'Hora prevista';
    const text = el('div', 'queue-text');
    text.append(el('div', 'queue-title', item.title || '(sense títol)'), el('div', 'queue-artist', item.artist || ''));
    // El pas de la rotació d'on l'ha triada l'AutoDJ (D-097), p. ex. «Pas 3 · Music / Adult 5»
    if (item.rotationStep != null) {
      const step = el('div', 'queue-step');
      step.append(icon('bi-arrow-repeat'), ' ',
        el('span', '', `Pas ${item.rotationStep + 1}${item.rotationStepName ? ` · ${item.rotationStepName}` : ''}`));
      step.title = 'El pas de la rotació d\'on l\'ha triada l\'AutoDJ';
      text.append(step);
    }
    // Trepitjador (fase 3, pas 5b; D-084): el sweeper que sonarà a sobre de l'inici
    if (item.sweeper) {
      const sweeper = el('div', 'queue-sweeper');
      sweeper.append(icon('bi-soundwave'), ' ', el('span', '', `Amb sweeper: ${item.sweeper.title || item.sweeper.path}`));
      sweeper.title = 'Sonarà a sobre de l\'inici d\'aquesta pista, amb la música atenuada';
      text.append(sweeper);
    }

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

    // Eines (fase 3, pas 2a): reprodueix ara, amunt, avall i treu. Sempre amb icona, i el text al títol del botó
    const tools = el('div', 'queue-tools');
    const tool = (/** @type {string} */ css, /** @type {string} */ iconName, /** @type {string} */ title, /** @type {string | null} */ label,
      /** @type {(b: HTMLButtonElement) => void} */ run, disabled = false) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', `btn btn-sm ${css}`));
      b.type = 'button';
      b.title = title;
      b.setAttribute('aria-label', title);
      b.disabled = disabled;
      b.append(icon(iconName));
      if (label) b.append(' ', el('span', '', label));
      b.addEventListener('click', () => run(b));
      tools.append(b);
    };
    tool('btn-outline-success', 'bi-play-fill', 'Reprodueix ara: la que sona baixa amb un fos i aquesta comença de seguida', null,
      () => this.#playNow(item, index));
    tool('btn-outline-secondary', 'bi-arrow-up', 'Puja (sonarà abans)', null, (b) => this.#move(item, -1, b), index === 0);
    tool('btn-outline-secondary', 'bi-arrow-down', 'Baixa (sonarà després)', null, (b) => this.#move(item, 1, b), index === this.#count - 1);
    tool('btn-outline-danger queue-remove', 'bi-x-lg', 'Treu aquesta pista de la cua', 'Treu', (b) => this.#remove(item, b));

    row.append(number, time, text, typeBadge(item.type), mark, origin, duration, tools);
    return row;
  }

  /** Quantes pistes té la cua que es mostra (per desactivar «Baixa» a l'última). */
  #count = 0;
  /** La cua que es mostra (per dir quines pistes se saltaran). @type {any[]} */
  #items = [];

  /**
   * «Reprodueix ara» (D-073): demana confirmació dient quines pistes se saltaran.
   * @param {any} item  @param {number} index
   */
  async #playNow(item, index) {
    const name = item.artist ? `«${item.title}» (${item.artist})` : `«${item.title}»`;
    const skipped = this.#items.slice(0, index);
    let message = `La pista que sona baixarà amb un fos d'1 segon i començarà ${name}.`;
    if (skipped.length > 0) {
      const titles = skipped.slice(0, 5).map((/** @type {any} */ i) => `«${i.title}»`).join(', ');
      message += `\n\nSe ${skipped.length === 1 ? 'saltarà 1 pista' : `saltaran ${skipped.length} pistes`}: ${titles}${skipped.length > 5 ? '…' : ''}.`;
      if (skipped.some((/** @type {any} */ i) => i.origin === 'event'))
        message += '\nLes que venen d\'un esdeveniment quedaran anotades com a no emeses.';
    }
    const ok = await confirmAction({ title: 'Reproduir ara', message, confirm: 'Reprodueix ara' });
    if (!ok) return;
    try {
      await this.#send('player.playItem', { itemId: item.id, fadeMs: 1000 });
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut reproduir', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** Mou una pista una posició (no demana confirmació: no interromp res). @param {any} item @param {number} delta @param {HTMLButtonElement} button */
  async #move(item, delta, button) {
    button.disabled = true;
    try {
      await this.#send('queue.move', { itemId: item.id, delta });
      // La cua nova arriba amb queue.changed
    } catch (err) {
      button.disabled = false;
      await confirmAction({ title: 'No s\'ha pogut moure', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
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
        // 0 i no sencera: el motor es va aturar de cop mentre sonava i no se sap quant va sonar (fase 3, pas 2a)
        duration = el('span', 'history-duration', e.playedMs === 0 && e.complete === false ? '–' : formatDuration(e.playedMs));
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
