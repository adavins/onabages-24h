// @ts-check
// Quadre de cerca de la biblioteca a la pantalla d'Emissió (fase 3, pas 3b; D-079): cerca a mesura que s'escriu (sense
// accents ni majúscules, ho fa el motor), filtres per categoria, subcategoria i etiqueta, i una llista que es va
// carregant de 100 en 100 en baixar. Per afegir una pista a la cua: doble clic (al final), els botons «Següent» i
// «Al final», o arrossegar-la fins a la cua (on es deixi anar). Les que tenen el fitxer que falta surten marcades i no
// es poden afegir: a la cua se saltarien.

import { $, el, icon } from './dom.js';
import { confirmAction } from './confirm.js';
import { formatDuration, playedAgo } from '../format.js';

const PAGE = 100;
const SEARCH_DELAY_MS = 250;

/** Tipus de dada de l'arrossegament: la cua només accepta pistes de la biblioteca. */
export const TRACK_DRAG_TYPE = 'application/x-onabages-track';

export class LibraryView {
  #send;
  /** @type {any[]} */
  #categories = [];
  #offset = 0;
  #more = false;
  #loading = false;
  #token = 0;
  #timer = 0;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    $('lib-search').addEventListener('input', () => {
      clearTimeout(this.#timer);
      this.#timer = window.setTimeout(() => this.search(), SEARCH_DELAY_MS);
    });
    $('lib-category').addEventListener('change', () => {
      this.#fillSubcategories();
      this.search();
    });
    $('lib-subcategory').addEventListener('change', () => this.search());
    $('lib-tag').addEventListener('change', () => this.search());
    // Quan s'arriba a prop del final de la llista, se'n carreguen 100 més
    $('lib-results').addEventListener('scroll', () => {
      const box = $('lib-results');
      if (this.#more && !this.#loading && box.scrollTop + box.clientHeight > box.scrollHeight - 200) this.#load(false);
    });
  }

  /** En connectar-se: els filtres i la primera cerca. */
  async refresh() {
    try {
      const filters = await this.#send('library.filters');
      this.#categories = filters.categories;
      const category = /** @type {HTMLSelectElement} */ ($('lib-category'));
      const previous = category.value;
      category.replaceChildren(option('', 'Categoria'),
        ...filters.categories.filter((/** @type {any} */ c) => c.count > 0).map((/** @type {any} */ c) => option(String(c.id), `${c.name} (${c.count})`)));
      category.value = previous;
      this.#fillSubcategories();
      const tag = /** @type {HTMLSelectElement} */ ($('lib-tag'));
      const previousTag = tag.value;
      tag.replaceChildren(option('', 'Etiqueta'),
        ...filters.tags.map((/** @type {any} */ t) => option(String(t.id), `${t.name} (${t.count})`)));
      tag.value = previousTag;
    } catch { /* sense connexió: ja ho diu la capçalera */ }
    await this.search();
  }

  #fillSubcategories() {
    const select = /** @type {HTMLSelectElement} */ ($('lib-subcategory'));
    const categoryId = Number(/** @type {HTMLSelectElement} */ ($('lib-category')).value);
    const category = this.#categories.find((c) => c.id === categoryId);
    const subs = category ? category.subcategories : this.#categories.flatMap((c) => c.subcategories);
    select.replaceChildren(option('', 'Subcategoria'),
      ...subs.filter((/** @type {any} */ s) => s.count > 0).map((/** @type {any} */ s) => option(String(s.id), `${s.name} (${s.count})`)));
  }

  /** Torna a cercar des del principi (en escriure o canviar un filtre). */
  async search() {
    $('lib-results').scrollTop = 0;
    await this.#load(true);
  }

  /** @param {boolean} fromStart */
  async #load(fromStart) {
    const token = ++this.#token;
    this.#loading = true;
    const offset = fromStart ? 0 : this.#offset;
    /** @type {Record<string, any>} */
    const params = { text: /** @type {HTMLInputElement} */ ($('lib-search')).value, offset, limit: PAGE };
    const value = (/** @type {string} */ id) => /** @type {HTMLSelectElement} */ ($(id)).value;
    if (value('lib-subcategory')) params.subcategoryId = Number(value('lib-subcategory'));
    else if (value('lib-category')) params.categoryId = Number(value('lib-category'));
    if (value('lib-tag')) params.tagId = Number(value('lib-tag'));
    try {
      const result = await this.#send('library.search', params);
      if (token !== this.#token) return; // n'hi ha una de més nova
      const list = $('lib-list');
      if (fromStart) list.replaceChildren();
      const now = new Date();
      list.append(...result.tracks.map((/** @type {any} */ t) => this.#row(t, now)));
      this.#offset = offset + result.tracks.length;
      this.#more = result.more;
      const shown = this.#offset;
      $('lib-count').textContent = shown === 0
        ? 'Cap pista amb aquests criteris.'
        : `${shown}${result.more ? '+' : ''} ${shown === 1 ? 'pista' : 'pistes'}${result.more ? ' · baixa per veure\'n més' : ''}`;
    } catch (err) {
      if (token === this.#token) $('lib-count').textContent = `No s'ha pogut cercar: ${/** @type {Error} */ (err).message}`;
    } finally {
      if (token === this.#token) this.#loading = false;
    }
  }

  /** @param {any} track @param {Date} now */
  #row(track, now) {
    const missing = track.fileState === 'missing';
    const row = el('li', `lib-row${missing ? ' is-missing' : ''}`);
    const label = track.artist ? `${track.artist} - ${track.title}` : track.title;
    row.title = track.path;

    const text = el('div', 'lib-text');
    text.append(el('div', 'lib-title', track.title || '(sense títol)'), el('div', 'lib-artist', track.artist || ''));
    if (missing) {
      const warning = el('div', 'lib-missing');
      warning.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', 'El fitxer falta: no es pot afegir'));
      text.append(warning);
    }
    const played = el('span', 'lib-played', playedAgo(track.lastPlayed, now));
    played.title = track.playCount ? `Ha sonat ${track.playCount} ${track.playCount === 1 ? 'vegada' : 'vegades'}` : 'No ha sonat mai';
    const duration = el('span', 'lib-duration', formatDuration(track.durationMs));

    const tools = el('div', 'lib-tools');
    const tool = (/** @type {string} */ iconName, /** @type {string} */ title, /** @type {'next' | 'end'} */ position) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      b.type = 'button';
      b.title = title;
      b.setAttribute('aria-label', `${title}: ${label}`);
      b.disabled = missing;
      b.append(icon(iconName));
      b.addEventListener('click', () => this.add(track, position));
      tools.append(b);
    };
    tool('bi-skip-forward-fill', 'Afegeix com a següent', 'next');
    tool('bi-plus-lg', 'Afegeix al final de la cua', 'end');

    row.append(text, played, duration, tools);
    if (!missing) {
      row.draggable = true;
      row.addEventListener('dragstart', (e) => {
        e.dataTransfer?.setData(TRACK_DRAG_TYPE, JSON.stringify({ id: track.id, title: track.title }));
        if (e.dataTransfer) e.dataTransfer.effectAllowed = 'copy';
      });
      row.addEventListener('dblclick', () => this.add(track, 'end'));
    }
    return row;
  }

  /**
   * Afegeix una pista a la cua: «next», «end» o una posició (0 = la primera que sonarà).
   * @param {{ id: number, title: string }} track  @param {'next' | 'end' | number} position
   */
  async add(track, position) {
    try {
      await this.#send('queue.insert', { items: [{ trackId: track.id }], position });
      // La cua nova arriba amb queue.changed
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut afegir a la cua', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
