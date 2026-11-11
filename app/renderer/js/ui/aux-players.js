// @ts-check
// Reproductors auxiliars a la pantalla d'Emissió (fase 3, pas 4b; D-081). N'hi ha 3; el 1 és el dels senyals horaris.
// Una pista s'hi carrega arrossegant-la des de la cerca de la biblioteca i no sona fins que es prem ▶ (com a RadioDJ);
// s'hi queda carregada, per tornar-la a fer sonar. Sonen per sobre de la música, sense tocar la cua.
// El temps que queda es compta aquí entre un avís i el següent: és només per mostrar-lo (l'hora la porta el motor).

import { $, el, icon, setText } from './dom.js';
import { confirmAction } from './confirm.js';
import { formatDuration } from '../format.js';

const PLAYERS = [1, 2, 3];

/** @typedef {{ row: HTMLElement, title: HTMLElement, state: HTMLElement, time: HTMLElement, play: HTMLButtonElement, stop: HTMLButtonElement, unload: HTMLButtonElement }} AuxRow */

export class AuxPlayersView {
  #send;
  /** El que sona o espera la seva hora, per reproductor (docs/protocol.md, 4.8). @type {Map<number, any>} */
  #playing = new Map();
  /** La pista carregada a cada reproductor (o null). @type {Map<number, any>} */
  #loaded = new Map();
  /** L'últim error d'un reproductor (un fitxer que no s'ha pogut obrir), fins que torni a sonar o es carregui. @type {Map<number, string>} */
  #errors = new Map();
  /** Quan s'ha rebut l'estat: per comptar el temps que queda. */
  #receivedAt = 0;
  /** @type {Map<number, AuxRow>} */
  #rows = new Map();

  /** @param {(cmd: string, params?: object) => Promise<any>} send  @param {string} dragType  el de les pistes de la biblioteca */
  constructor(send, dragType) {
    this.#send = send;
    const list = $('aux-list');
    for (const player of PLAYERS) {
      const parts = this.#buildRow(player, dragType);
      this.#rows.set(player, parts);
      list.append(parts.row);
    }
    // El temps que queda, 4 vegades per segon i només si sona alguna cosa
    setInterval(() => {
      if (this.#playing.size > 0) this.#renderTimes();
    }, 250);
    this.#render();
  }

  /** Torna a demanar l'estat al motor (en connectar-se i amb cada avís aux.*). */
  async refresh() {
    try {
      const result = await this.#send('aux.get');
      this.#playing = new Map(result.aux.map((/** @type {any} */ a) => [a.player, a]));
      this.#loaded = new Map(result.loaded.map((/** @type {any} */ l) => [l.player, l.item]));
      this.#receivedAt = performance.now();
    } catch {
      this.#playing.clear(); // sense connexió: ja ho diu la capçalera
    }
    this.#render();
  }

  /** Un auxiliar no s'ha pogut fer sonar (aux.failed). @param {number} player @param {string} message */
  failed(player, message) {
    this.#errors.set(player, message);
    this.#render();
  }

  /** Ha començat a sonar (aux.started): s'oblida l'error anterior. @param {number} player */
  started(player) {
    this.#errors.delete(player);
  }

  /** @param {number} player @param {string} dragType */
  #buildRow(player, dragType) {
    const row = el('li', 'aux-row');
    const number = el('span', 'aux-number', String(player));
    number.title = player === 1 ? 'Reproductor 1: el dels senyals horaris' : `Reproductor ${player}`;

    const button = (/** @type {string} */ cls, /** @type {string} */ iconName, /** @type {string} */ label) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', `btn btn-sm ${cls}`));
      b.type = 'button';
      b.title = label;
      b.setAttribute('aria-label', label);
      b.append(icon(iconName));
      return b;
    };
    const play = button('btn-outline-success aux-btn', 'bi-play-fill', `Reprodueix el reproductor auxiliar ${player}`);
    const stop = button('btn-outline-danger aux-btn', 'bi-stop-fill', `Atura el reproductor auxiliar ${player}`);
    const unload = button('btn-outline-secondary aux-unload', 'bi-x-lg', `Buida el reproductor auxiliar ${player}`);
    play.addEventListener('click', () => this.#command('aux.play', { player }, 'No s\'ha pogut reproduir'));
    stop.addEventListener('click', () => this.#command('aux.stop', { player }, 'No s\'ha pogut aturar'));
    unload.addEventListener('click', () => this.#command('aux.unload', { player }, 'No s\'ha pogut buidar'));

    const text = el('div', 'aux-text');
    const title = el('div', 'aux-title');
    const state = el('div', 'aux-state');
    text.append(title, state);
    const time = el('span', 'aux-time');

    row.append(number, play, stop, text, time, unload);

    // Deixar-hi anar una pista de la cerca: es carrega (no sona fins a ▶)
    row.addEventListener('dragover', (e) => {
      if (!e.dataTransfer?.types.includes(dragType)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      row.classList.add('drop-here');
    });
    row.addEventListener('dragleave', (e) => {
      if (!row.contains(/** @type {Node | null} */ (e.relatedTarget))) row.classList.remove('drop-here');
    });
    row.addEventListener('drop', (e) => {
      row.classList.remove('drop-here');
      const data = e.dataTransfer?.getData(dragType);
      if (!data) return;
      e.preventDefault();
      const track = JSON.parse(data);
      this.#errors.delete(player);
      this.#command('aux.load', { player, trackId: track.id }, 'No s\'ha pogut carregar la pista');
    });
    return { row, title, state, time, play, stop, unload };
  }

  /** @param {string} cmd @param {object} params @param {string} errorTitle */
  async #command(cmd, params, errorTitle) {
    try {
      await this.#send(cmd, params);
      await this.refresh(); // els avisos també ho fan, però així es veu de seguida
    } catch (err) {
      await confirmAction({ title: errorTitle, message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  #render() {
    for (const player of PLAYERS) {
      const r = /** @type {AuxRow} */ (this.#rows.get(player));
      const playing = this.#playing.get(player);
      const loaded = this.#loaded.get(player) ?? null;
      const error = this.#errors.get(player);

      const shown = playing?.item ?? loaded;
      setText(r.title, shown ? label(shown) : (player === 1 ? 'Senyals horaris' : 'Buit'));
      r.title.classList.toggle('is-empty', !shown);
      r.row.title = shown?.path ?? 'Arrossega-hi una pista de la cerca per carregar-la';

      // Estat: color + icona + text
      r.state.replaceChildren();
      /** @type {[string, string, string]} */
      let state;
      if (playing?.started) state = ['text-success', 'bi-volume-up-fill', playing.item.origin === 'event' ? `Sona · ${playing.item.eventName ?? 'esdeveniment'}` : 'Sona'];
      else if (playing) state = ['text-info', 'bi-hourglass-split', `Preparat · ${playing.item.eventName ?? 'esdeveniment'}`];
      else if (error) state = ['text-warning', 'bi-exclamation-triangle-fill', error];
      else if (loaded) state = ['text-body-secondary', 'bi-disc', 'Carregada · prem ▶ per fer-la sonar'];
      else state = ['text-body-secondary', 'bi-box-arrow-in-down', player === 1 ? 'Automàtic (esdeveniments) · arrossega-hi una pista' : 'Arrossega-hi una pista de la cerca'];
      r.state.className = `aux-state ${state[0]}`;
      r.state.append(icon(state[1]), ' ', el('span', '', state[2]));

      r.play.disabled = !loaded;
      r.stop.disabled = !playing;
      r.unload.classList.toggle('invisible', !loaded);
      r.row.classList.toggle('is-playing', !!playing?.started);
    }
    this.#renderSummary();
    this.#renderTimes();
  }

  /** Resum a la capçalera del requadre, que es veu encara que estigui plegat (D-083): color + icona + text. */
  #renderSummary() {
    const sounding = PLAYERS.filter((p) => this.#playing.get(p)?.started);
    const prepared = PLAYERS.filter((p) => this.#playing.get(p) && !this.#playing.get(p).started);
    const loaded = PLAYERS.filter((p) => this.#loaded.get(p));
    const errors = PLAYERS.filter((p) => this.#errors.has(p));
    const numbers = (/** @type {number[]} */ list) => list.join(', ');

    /** @type {[string, string, string]} */
    let summary;
    if (sounding.length > 0) summary = ['text-success', 'bi-volume-up-fill', `Sona el ${numbers(sounding)}`];
    else if (errors.length > 0) summary = ['text-warning', 'bi-exclamation-triangle-fill', `Error al ${numbers(errors)}`];
    else if (prepared.length > 0) summary = ['text-info', 'bi-hourglass-split', `Preparat el ${numbers(prepared)}`];
    else if (loaded.length > 0) summary = ['text-body-secondary', 'bi-disc', loaded.length === 1 ? `Carregat el ${loaded[0]}` : `Carregats ${numbers(loaded)}`];
    else summary = ['text-body-secondary', 'bi-box-arrow-in-down', 'Buits'];

    const target = $('aux-summary');
    target.className = `aux-summary ms-auto ${summary[0]}`;
    target.replaceChildren(icon(summary[1]), ' ', el('span', '', summary[2]));
  }

  /** Temps que queda del que sona; si no sona res, la durada de la carregada. */
  #renderTimes() {
    const passed = performance.now() - this.#receivedAt;
    for (const player of PLAYERS) {
      const r = /** @type {AuxRow} */ (this.#rows.get(player));
      const playing = this.#playing.get(player);
      const loaded = this.#loaded.get(player);
      let text = '—';
      if (playing) text = '−' + formatDuration(Math.max(0, playing.remainingMs - (playing.started ? passed : 0)));
      else if (loaded) text = formatDuration(loaded.durationMs);
      setText(r.time, text);
    }
  }
}

/** @param {any} item */
function label(item) {
  return item.artist ? `${item.artist} - ${item.title}` : (item.title || item.path);
}
