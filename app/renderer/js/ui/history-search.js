// @ts-check
// Pestanya «Historial» (fase 4, pas 5b; D-143): què ha sonat i quan. Filtres de període, text, tipus i origen (i, des
// de la fitxa d'una pista, només aquella pista); la llista, les més recents primer, per pàgines («Carrega'n més»). Un
// clic a una fila obre la fitxa de la pista. Les dades les dona el motor (history.list): aquí només es mostren.

import { $, el, icon, statusLabel } from './dom.js';
import { formatDuration, typeName, originName } from '../format.js';
import { todayIso } from '../event-form.js';
import { historyParams, periodProblem, localDateTime, completeState, summaryText, PAGE_SIZE } from '../history-form.js';

/** D'on ve cada emissió: icona i text (mai només el color). */
const ORIGIN_ICONS = /** @type {Record<string, string>} */ ({ autodj: 'bi-shuffle', manual: 'bi-hand-index', event: 'bi-calendar-event' });

/** Espera després d'escriure al cercador abans de buscar. */
const SEARCH_DELAY_MS = 300;

export class HistorySearchView {
  /** @type {(cmd: string, params?: object) => Promise<any>} */
  #send;
  /** @type {(trackId: number) => void} */
  #openTrack;
  /** @type {import('../history-form.js').HistoryFilters} */
  #f;
  /** Les entrades que es veuen. @type {any[]} */
  #entries = [];
  /** @type {number | null} */
  #total = null;
  #more = false;
  #visible = false;
  /** Per descartar les respostes d'una cerca que ja no és la d'ara. */
  #token = 0;
  /** @type {number | undefined} */
  #timer;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(trackId: number) => void} openTrack  obre la fitxa de la pista (a la pestanya Pistes)
   */
  constructor(send, openTrack) {
    this.#send = send;
    this.#openTrack = openTrack;
    const today = todayIso();
    this.#f = { from: today, to: today, text: '', type: '', origin: '', track: null };
    this.#syncControls();

    const input = (/** @type {string} */ id) => /** @type {HTMLInputElement} */ ($(id));
    input('hs-from').addEventListener('change', () => this.#set({ from: input('hs-from').value }));
    input('hs-to').addEventListener('change', () => this.#set({ to: input('hs-to').value }));
    input('hs-search').addEventListener('input', () => {
      clearTimeout(this.#timer);
      this.#timer = window.setTimeout(() => this.#set({ text: input('hs-search').value }), SEARCH_DELAY_MS);
    });
    $('hs-type').addEventListener('change', () => this.#set({ type: /** @type {HTMLSelectElement} */ ($('hs-type')).value }));
    $('hs-origin').addEventListener('change', () => this.#set({ origin: /** @type {HTMLSelectElement} */ ($('hs-origin')).value }));
    $('hs-today').addEventListener('click', () => {
      const day = todayIso();
      this.#set({ from: day, to: day });
    });
    $('hs-all-dates').addEventListener('click', () => this.#set({ from: '', to: '' }));
    $('hs-refresh').addEventListener('click', () => this.#load());
    $('hs-more').addEventListener('click', () => this.#loadMore());
  }

  show() {
    this.#visible = true;
    this.#load();
  }

  hide() {
    this.#visible = false;
  }

  /**
   * Ha començat o s'ha acabat una pista: si es veu la primera pàgina, es torna a demanar (si ja se n'han carregat més,
   * no, perquè la llista no salti mentre es mira).
   */
  refresh() {
    if (this.#visible && this.#entries.length <= PAGE_SIZE) this.#load();
  }

  /**
   * Des de la fitxa d'una pista: tot el seu historial (sense límit de dates ni els altres filtres).
   * @param {{ id: number, name: string }} track
   */
  openTrack(track) {
    this.#f = { from: '', to: '', text: '', type: '', origin: '', track };
    this.#syncControls();
    this.show();
  }

  /** @param {Partial<import('../history-form.js').HistoryFilters>} change */
  #set(change) {
    this.#f = { ...this.#f, ...change };
    this.#syncControls();
    this.#load();
  }

  /** Els controls, com diuen els filtres (també el de la pista, que es pot treure). */
  #syncControls() {
    /** @type {HTMLInputElement} */ ($('hs-from')).value = this.#f.from;
    /** @type {HTMLInputElement} */ ($('hs-to')).value = this.#f.to;
    const search = /** @type {HTMLInputElement} */ ($('hs-search'));
    if (search.value !== this.#f.text) search.value = this.#f.text;
    /** @type {HTMLSelectElement} */ ($('hs-type')).value = this.#f.type;
    /** @type {HTMLSelectElement} */ ($('hs-origin')).value = this.#f.origin;
    const box = $('hs-track');
    if (!this.#f.track) {
      box.replaceChildren();
      box.classList.add('d-none');
      return;
    }
    const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link p-0 hs-track-remove'));
    remove.type = 'button';
    remove.title = 'Treu aquest filtre: totes les pistes';
    remove.append(icon('bi-x-lg'), el('span', 'visually-hidden', 'Treu el filtre de la pista'));
    remove.addEventListener('click', () => this.#set({ track: null }));
    box.replaceChildren(icon('bi-music-note-beamed'), ' Només la pista ', el('strong', '', this.#f.track.name), ' ', remove);
    box.classList.remove('d-none');
  }

  /** La primera pàgina, amb el total. */
  async #load() {
    const mine = ++this.#token;
    const problem = periodProblem(this.#f);
    if (problem) {
      this.#entries = [];
      this.#total = null;
      this.#more = false;
      this.#render();
      $('hs-summary').replaceChildren(statusLabel('text-warning', 'bi-exclamation-triangle-fill', problem));
      return;
    }
    $('hs-summary').replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Buscant…'));
    try {
      const result = await this.#send('history.list', historyParams(this.#f));
      if (mine !== this.#token) return;
      this.#entries = result.entries;
      this.#total = result.total ?? null;
      this.#more = result.more;
      this.#render();
    } catch (err) {
      if (mine !== this.#token) return;
      $('hs-summary').replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'ha pogut llegir l'historial: ${/** @type {Error} */ (err).message}`));
    }
  }

  /** La pàgina següent: les que van sonar abans de l'última que es veu. */
  async #loadMore() {
    const last = this.#entries.at(-1);
    if (!last) return;
    const mine = ++this.#token;
    const button = /** @type {HTMLButtonElement} */ ($('hs-more'));
    button.disabled = true;
    try {
      const result = await this.#send('history.list', historyParams(this.#f, last.id));
      if (mine !== this.#token) return;
      this.#entries = [...this.#entries, ...result.entries];
      this.#more = result.more;
      this.#render();
    } catch (err) {
      if (mine !== this.#token) return;
      $('hs-summary').replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'ha pogut llegir l'historial: ${/** @type {Error} */ (err).message}`));
    } finally {
      button.disabled = false;
    }
  }

  #render() {
    $('hs-summary').textContent = summaryText(this.#entries.length, this.#total);
    $('hs-empty').classList.toggle('d-none', this.#entries.length > 0);
    $('hs-more-box').classList.toggle('d-none', !this.#more);
    $('hs-rows').replaceChildren(...this.#entries.map((e) => this.#row(e)));
  }

  /** @param {any} e */
  #row(e) {
    const tr = el('tr', 'hs-row');
    const { date, time } = localDateTime(e.startedAt);
    const cell = (/** @type {string} */ text, /** @type {string} */ className = '') => {
      const td = el('td', className, text);
      td.title = text;
      return td;
    };
    const origin = el('td', 'hs-origin');
    origin.append(icon(ORIGIN_ICONS[e.origin] ?? 'bi-hand-index'), ` ${originName(e.origin)}`);
    const state = completeState(e);
    const complete = el('td', state.className);
    complete.append(icon(state.icon), ` ${state.text}`);
    tr.append(
      cell(date), cell(time, 'hs-num'), cell(e.artist ?? ''), cell(e.title || '(sense títol)'),
      cell(e.type ? typeName(e.type) : '—'), origin, cell(e.campaignName ?? ''),
      cell(e.playedMs == null ? '' : formatDuration(e.playedMs), 'hs-num'), complete,
    );
    if (e.trackId != null) {
      tr.classList.add('hs-row-link');
      tr.tabIndex = 0;
      tr.title = 'Obre la fitxa de la pista';
      const open = () => this.#openTrack(e.trackId);
      tr.addEventListener('click', open);
      tr.addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter') open();
      });
    } else tr.title = 'Aquesta pista ja no és a la biblioteca';
    return tr;
  }
}
