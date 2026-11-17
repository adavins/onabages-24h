// @ts-check
// Pestanya «Registre» (D-167): tot el que ha passat a l'aplicació en una sola llista, de més nou a més antic: les
// incidències, els canvis, les sessions i (si es marca) l'execució dels esdeveniments. Substitueix les finestres
// «Incidències» i «Registre de canvis». Els canvis i les sessions només els veu l'administrador (ho decideix el motor).
// Mentre es veu, es torna a carregar quan el motor anota o tanca una incidència. L'origen es tria en un desplegable de
// selecció múltiple, i «Restableix» torna tots els filtres als de per defecte (D-169).

import { $, el, icon, statusLabel } from './dom.js';
import { KINDS, severityInfo, dateTimeText, PERIODS } from '../incidents-form.js';
import { AREAS, areaPrefixes, userText, detailText } from '../changes-form.js';
import { MultiSelect } from './multi-select.js';
import { SOURCES, DEFAULT_SOURCES, sourceIcon, sourceName, typeText, stateText, entryText, logParams, countText } from '../log-form.js';

/** Quantes es carreguen cada vegada. */
const PAGE = 100;
/** Espera després d'escriure al cercador abans de buscar. */
const SEARCH_DELAY_MS = 300;
/** El període per defecte (darrers 7 dies). */
const DEFAULT_PERIOD = '7';

export class LogView {
  /** @type {(cmd: string, params?: object) => Promise<any>} */
  #send;
  #visible = false;
  /** @type {any[]} */
  #loaded = [];
  #total = 0;
  #request = 0;
  /** @type {number | undefined} */
  #timer;
  #changesAllowed = true;
  #usersLoaded = false;

  // Filtres
  #period = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm lg-select'));
  #sources = new MultiSelect('Origen', SOURCES.map(([value, text, iconName]) => ({ value, text, icon: iconName })), DEFAULT_SOURCES);
  #severity = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm lg-select'));
  #type = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm lg-select'));
  #user = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm lg-select'));
  #search = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm lg-search'));
  #openOnly = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  #summary = el('span', 'hs-summary');
  #rows = el('tbody');
  #empty = el('div', 'empty-note d-none', 'No hi ha res amb aquests filtres.');
  #more = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  #moreBox = el('div', 'hs-more d-none');

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;

    this.#period.append(...PERIODS.map(([value, text]) => new Option(text, value)));
    this.#period.value = DEFAULT_PERIOD;
    this.#period.setAttribute('aria-label', 'Període');

    this.#severity.append(new Option('Totes les gravetats', ''), new Option('Avisos i greus', 'warning'), new Option('Només greus', 'danger'));
    this.#severity.setAttribute('aria-label', 'Gravetat');

    const incidentKinds = /** @type {HTMLOptGroupElement} */ (el('optgroup'));
    incidentKinds.label = 'Incidències';
    incidentKinds.append(...KINDS.map(([value, text]) => new Option(text, `incident:${value}`)));
    const changeAreas = /** @type {HTMLOptGroupElement} */ (el('optgroup'));
    changeAreas.label = 'Canvis';
    changeAreas.append(...AREAS.map(([value, text]) => new Option(text, `change:${value}`)));
    this.#type.append(new Option('Tots els tipus', ''), incidentKinds, changeAreas);
    this.#type.setAttribute('aria-label', 'Tipus');

    this.#user.append(new Option('Tots els usuaris', ''));
    this.#user.setAttribute('aria-label', 'Usuari');

    this.#search.type = 'search';
    this.#search.placeholder = 'Cerca un text';
    this.#search.setAttribute('aria-label', 'Cerca un text');

    this.#openOnly.type = 'checkbox';
    this.#openOnly.id = 'lg-open-only';
    const openLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Només les incidències obertes'));
    openLabel.htmlFor = this.#openOnly.id;
    const openBox = el('div', 'form-check mb-0');
    openBox.append(this.#openOnly, openLabel);

    const refresh = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    refresh.type = 'button';
    refresh.append(icon('bi-arrow-clockwise'), ' Actualitza');
    refresh.addEventListener('click', () => this.#load(false));

    const reset = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    reset.type = 'button';
    reset.title = 'Torna tots els filtres als de per defecte';
    reset.append(icon('bi-x-circle'), ' Restableix');
    reset.addEventListener('click', () => this.#resetFilters());

    const filters = el('div', 'hs-filters lg-filters');
    filters.setAttribute('role', 'search');
    filters.setAttribute('aria-label', 'Filtres del registre');
    // «Restableix» i «Actualitza», sempre a la fila de sota (lg-actions ocupa tota l'amplada)
    const actions = el('div', 'lg-actions');
    actions.append(reset, refresh);
    filters.append(this.#period, this.#sources.element, this.#severity, this.#type, this.#user, this.#search, openBox, actions);
    const status = el('div', 'hs-status');
    this.#summary.setAttribute('aria-live', 'polite');
    status.append(this.#summary);

    const table = el('table', 'tm-table lg-table');
    const head = el('tr');
    for (const [text, css] of [['Data i hora', 'lg-c-time'], ['Origen', 'lg-c-source'], ['Gravetat', 'lg-c-severity'], ['Tipus', 'lg-c-type'],
      ['Què ha passat', 'lg-c-text'], ['Usuari', 'lg-c-user'], ['Estat', 'lg-c-state']]) head.append(el('th', css, text));
    const thead = el('thead');
    thead.append(head);
    table.append(thead, this.#rows);
    this.#more.type = 'button';
    this.#more.append(icon('bi-chevron-double-down'), ' Carrega\'n més');
    this.#more.addEventListener('click', () => this.#load(true));
    this.#moreBox.append(this.#more);
    const scroll = el('div', 'hs-scroll');
    scroll.append(table, this.#empty, this.#moreBox);

    const page = el('div', 'hs-page');
    page.append(filters, status, scroll);
    $('view-registre').append(page);

    for (const control of [this.#period, this.#severity, this.#type, this.#user, this.#openOnly, this.#sources.element]) {
      control.addEventListener('change', () => this.#load(false));
    }
    this.#openOnly.addEventListener('change', () => this.#syncOpenOnly());
    this.#search.addEventListener('input', () => {
      clearTimeout(this.#timer);
      this.#timer = setTimeout(() => this.#load(false), SEARCH_DELAY_MS);
    });
  }

  /** Amb «Només les incidències obertes», el període, l'origen, el tipus i l'usuari no compten. */
  #syncOpenOnly() {
    const only = this.#openOnly.checked;
    for (const control of [this.#period, this.#user, this.#sources]) control.disabled = only;
  }

  /** «Restableix»: el període, l'origen, la gravetat, el tipus, l'usuari, el text i les obertes, com en obrir l'aplicació. */
  #resetFilters() {
    clearTimeout(this.#timer);
    this.#period.value = DEFAULT_PERIOD;
    this.#sources.values = DEFAULT_SOURCES;
    this.#severity.value = '';
    this.#type.value = '';
    this.#user.value = '';
    this.#search.value = '';
    this.#openOnly.checked = false;
    this.#syncOpenOnly();
    this.#load(false);
  }

  show() {
    this.#visible = true;
    this.#loadUsers();
    this.#load(false);
  }

  hide() {
    this.#visible = false;
  }

  /** S'ha anotat o tancat una incidència: si es veu (i és la primera pàgina), es torna a carregar. */
  refresh() {
    if (this.#visible && this.#loaded.length <= PAGE) this.#load(false);
  }

  /** Des de la barra d'avisos: només les incidències obertes. */
  showOpenIncidents() {
    this.#openOnly.checked = true;
    this.#syncOpenOnly();
    this.#load(false);
  }

  /** La llista d'usuaris per al filtre (només l'administrador la pot llegir; si no, el filtre s'amaga). */
  async #loadUsers() {
    try {
      const { users } = await this.#send('users.list');
      const current = this.#user.value;
      this.#user.replaceChildren(new Option('Tots els usuaris', ''),
        ...users.map((/** @type {{ id: number, name: string }} */ u) => new Option(u.name, String(u.id))));
      this.#user.value = current;
      this.#usersLoaded = true;
    } catch {
      this.#usersLoaded = false;
    }
    this.#showAllowed();
  }

  /** Sense permís per veure els canvis i les sessions, se n'amaguen els filtres. */
  #showAllowed() {
    for (const code of ['change', 'session']) this.#sources.setHidden(code, !this.#changesAllowed);
    this.#user.classList.toggle('d-none', !this.#changesAllowed || !this.#usersLoaded);
  }

  /** @param {boolean} append */
  async #load(append) {
    const mine = ++this.#request;
    const filters = {
      period: this.#period.value,
      sources: this.#sources.values,
      severity: this.#severity.value,
      type: this.#type.value,
      userId: this.#user.value,
      text: this.#search.value,
      openOnly: this.#openOnly.checked,
    };
    if (filters.sources.length === 0 && !filters.openOnly) {
      this.#loaded = [];
      this.#total = 0;
      this.#render();
      return;
    }
    let result;
    try {
      result = await this.#send('log.list', { ...logParams(filters, new Date(), areaPrefixes), offset: append ? this.#loaded.length : 0, limit: PAGE });
    } catch (err) {
      if (mine !== this.#request) return;
      this.#summary.replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'ha pogut llegir el registre: ${/** @type {Error} */ (err).message}`));
      return;
    }
    if (mine !== this.#request) return;
    this.#loaded = append ? [...this.#loaded, ...result.entries] : result.entries;
    this.#total = result.total;
    this.#changesAllowed = result.changesAllowed;
    this.#showAllowed();
    this.#render();
  }

  #render() {
    const now = new Date();
    this.#rows.replaceChildren(...this.#loaded.map((/** @type {import('../log-form.js').LogEntry} */ e) => {
      const tr = el('tr', e.open ? 'lg-open' : '');
      const source = el('td', 'lg-c-source');
      source.append(icon(sourceIcon(e.source)), ` ${sourceName(e.source)}`);
      const severityCell = el('td', 'lg-c-severity');
      if (e.source === 'incident' || e.severity !== 'info') {
        const s = severityInfo(e.severity);
        severityCell.append(statusLabel(s.className, s.icon, s.text));
      }
      const text = el('td', 'lg-c-text');
      text.append(el('div', '', entryText(e)));
      const detail = e.source === 'change' || e.source === 'session' ? detailText(e.detail) : (e.detail ?? '');
      if (detail) text.append(el('div', 'lg-detail', detail));
      text.title = detail ? `${entryText(e)}\n${detail}` : entryText(e);
      const stateCell = el('td', 'lg-c-state');
      const state = stateText(e, now);
      if (state) stateCell.append(statusLabel(state.className, state.icon, state.text));
      tr.append(el('td', 'lg-c-time', dateTimeText(e.at)), source, severityCell, el('td', 'lg-c-type', typeText(e)), text,
        el('td', 'lg-c-user', e.source === 'change' || e.source === 'session' ? userText(e) : ''), stateCell);
      return tr;
    }));
    this.#empty.classList.toggle('d-none', this.#loaded.length > 0);
    this.#moreBox.classList.toggle('d-none', this.#loaded.length >= this.#total);
    this.#summary.textContent = this.#loaded.length < this.#total
      ? `${countText(this.#total)} (se'n veuen ${this.#loaded.length.toLocaleString('ca-ES')})`
      : countText(this.#total);
  }
}
