// @ts-check
// Pantalla del planificador (fase 2, pas 8a): graella per setmanes reals o setmana tipus, i detall de l'esdeveniment.
// Només llegeix: les dades són les del motor (scheduler.list i scheduler.occurrences); l'edició arriba al pas 8b.

import { $, el, icon, statusLabel } from './dom.js';
import {
  MODES, modeInfo, WEEKDAYS, weekStart, addDays, isoDate, parseDate, weekTitle, minutesOf, shortTime, shortDate,
  daysText, hoursText, recurrenceText, durationText, repeatsWithinDay, layoutLanes,
} from '../schedule.js';

/** Píxels per minut segons el zoom (com a la maqueta: 30 min = 48 px). */
const PX_PER_MIN = { 30: 1.6, 15: 3.2, 5: 8 };
/** Alçada mínima d'un bloc (els esdeveniments sense durada són una marca). */
const MIN_BLOCK_PX = 22;
const DAY_NAMES = ['dl', 'dt', 'dc', 'dj', 'dv', 'ds', 'dg'];

/**
 * @typedef {{ eventId: number, name: string, mode: string, date: string, time: string, at: string,
 *   skippedClockChange: boolean, enabled: boolean, repeat: string, maxDurationSeconds: number | null }} Occurrence
 * @typedef {{ ev: any, day: number, start: number, end: number, time: string, skipped?: boolean }} Block
 */

export class PlannerView {
  #send;
  /** @type {Map<number, any>} */
  #events = new Map();
  /** @type {Occurrence[]} */
  #occurrences = [];
  #view = /** @type {'week' | 'template'} */ ('week');
  #monday = weekStart(new Date());
  #zoom = /** @type {30 | 15 | 5} */ (30);
  #showDisabled = true;
  /** @type {Set<string>} */
  #hiddenModes = new Set();
  /** @type {number | null} */
  #selected = null;
  #visible = false;
  #scrolled = false;
  #loadToken = 0;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;

    for (const [id, view] of /** @type {const} */ ([['pl-view-week', 'week'], ['pl-view-template', 'template']])) {
      $(id).addEventListener('click', () => {
        this.#view = view;
        this.#syncToolbar();
        this.load();
      });
    }
    $('pl-prev').addEventListener('click', () => this.#moveWeek(-7));
    $('pl-next').addEventListener('click', () => this.#moveWeek(7));
    $('pl-today').addEventListener('click', () => {
      this.#monday = weekStart(new Date());
      this.#scrolled = false;
      this.load();
    });
    for (const button of document.querySelectorAll('#pl-zoom button')) {
      button.addEventListener('click', () => {
        this.#zoom = /** @type {30 | 15 | 5} */ (Number(/** @type {HTMLElement} */ (button).dataset.zoom));
        this.#scrolled = false;
        this.#syncToolbar();
        this.#render();
      });
    }
    /** @type {HTMLInputElement} */ ($('pl-show-disabled')).addEventListener('change', (e) => {
      this.#showDisabled = /** @type {HTMLInputElement} */ (e.target).checked;
      this.#render();
    });

    // La línia de l'hora actual es mou cada minut
    setInterval(() => {
      if (this.#visible && this.#view === 'week') this.#drawNowLine();
    }, 30_000);
    this.#syncToolbar();
  }

  /** La pantalla es mostra: es carrega (i es torna a carregar cada vegada, per si ha canviat res). */
  show() {
    this.#visible = true;
    this.load();
  }

  hide() {
    this.#visible = false;
  }

  /** Els esdeveniments han canviat al motor (scheduler.changed) o s'ha tornat a connectar. */
  refresh() {
    if (this.#visible) this.load();
  }

  async load() {
    const token = ++this.#loadToken;
    try {
      const listRequest = this.#send('scheduler.list');
      const occurrencesRequest = this.#view === 'week'
        ? this.#send('scheduler.occurrences', {
          from: isoDate(this.#monday), to: isoDate(addDays(this.#monday, 6)), includeDisabled: true, limit: 2000,
        })
        : Promise.resolve({ occurrences: [] });
      const [list, occurrences] = await Promise.all([listRequest, occurrencesRequest]);
      if (token !== this.#loadToken) return; // n'hi ha una de més nova
      this.#events = new Map(list.events.map((/** @type {any} */ e) => [e.id, e]));
      this.#occurrences = occurrences.occurrences;
      $('pl-error').classList.add('d-none');
    } catch (err) {
      if (token !== this.#loadToken) return;
      $('pl-error').classList.remove('d-none');
      $('pl-error-text').textContent = `No s'ha pogut llegir la programació del motor: ${/** @type {Error} */ (err).message}`;
    }
    this.#render();
    if (this.#selected != null) this.#showDetail(this.#selected);
  }

  #moveWeek(/** @type {number} */ days) {
    this.#monday = addDays(this.#monday, days);
    this.load();
  }

  #syncToolbar() {
    const week = this.#view === 'week';
    $('pl-view-week').classList.toggle('active', week);
    $('pl-view-week').setAttribute('aria-pressed', String(week));
    $('pl-view-template').classList.toggle('active', !week);
    $('pl-view-template').setAttribute('aria-pressed', String(!week));
    for (const id of ['pl-prev', 'pl-next', 'pl-today']) /** @type {HTMLButtonElement} */ ($(id)).disabled = !week;
    for (const button of document.querySelectorAll('#pl-zoom button')) {
      const active = Number(/** @type {HTMLElement} */ (button).dataset.zoom) === this.#zoom;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    }
  }

  // --- Dibuix ---

  #render() {
    $('pl-title').textContent = this.#view === 'week' ? weekTitle(this.#monday) : 'Setmana tipus (sense dates)';
    this.#renderLegend();
    this.#renderGrid();
    this.#renderOthers();
  }

  /** @param {any} ev */
  #isShown(ev) {
    return (this.#showDisabled || ev.enabled) && !this.#hiddenModes.has(ev.mode) && !ev.isTemplate;
  }

  /** Modes (es poden amagar) i esdeveniments que es repeteixen dins del dia (fila pròpia). */
  #renderLegend() {
    const legend = $('pl-legend');
    legend.replaceChildren();
    legend.append(el('span', 'pl-legend-title', 'Modes'));
    for (const [mode, info] of Object.entries(MODES)) {
      const hidden = this.#hiddenModes.has(mode);
      const chip = el('button', `pl-chip ${info.css}${hidden ? ' is-off' : ''}`);
      chip.type = 'button';
      chip.setAttribute('aria-pressed', String(!hidden));
      chip.title = hidden ? `Mostra els esdeveniments ${info.label}` : `Amaga els esdeveniments ${info.label}`;
      chip.append(icon(info.icon), ' ', el('span', '', info.label));
      if (hidden) chip.append(' ', icon('bi-eye-slash'));
      chip.addEventListener('click', () => {
        if (hidden) this.#hiddenModes.delete(mode);
        else this.#hiddenModes.add(mode);
        this.#render();
      });
      legend.append(chip);
    }

    const repeating = [...this.#events.values()].filter((ev) => repeatsWithinDay(ev) && this.#isShown(ev));
    if (repeating.length === 0) return;
    legend.append(el('span', 'pl-legend-sep'), el('span', 'pl-legend-title', 'Es repeteixen'));
    for (const ev of repeating) {
      const info = modeInfo(ev.mode);
      const chip = el('button', `pl-repeat ${info.css}${ev.enabled ? '' : ' is-disabled'}${ev.id === this.#selected ? ' is-selected' : ''}`);
      chip.type = 'button';
      const when = ev.repeat === 'CADA_HORA'
        ? `:${ev.startTime.slice(3, 5)} · ${hoursText(ev.hoursMask)}`
        : `cada ${ev.intervalMinutes} min`;
      chip.append(icon(info.icon), ' ', el('span', '', `${when} · ${ev.name}${ev.enabled ? '' : ' · desactivat'}`));
      chip.addEventListener('click', () => this.#select(ev.id));
      legend.append(chip);
    }
  }

  /** Els blocs de la graella: les emissions de la setmana o, a la setmana tipus, els esdeveniments setmanals. */
  #blocks() {
    /** @type {Block[]} */
    const blocks = [];
    if (this.#view === 'week') {
      for (const o of this.#occurrences) {
        const ev = this.#events.get(o.eventId);
        if (!ev || repeatsWithinDay(ev) || !this.#isShown(ev)) continue;
        const day = Math.round((parseDate(o.date).getTime() - this.#monday.getTime()) / 86_400_000);
        if (day < 0 || day > 6) continue;
        blocks.push(this.#block(ev, day, o.time, o.skippedClockChange));
      }
    } else {
      for (const ev of this.#events.values()) {
        if (repeatsWithinDay(ev) || !this.#isShown(ev)) continue;
        if (ev.recurrence !== 'SETMANAL' && ev.recurrence !== 'CADA_N_SETMANES') continue;
        for (let day = 0; day < 7; day++) {
          if ((ev.daysMask & (1 << day)) !== 0) blocks.push(this.#block(ev, day, ev.startTime));
        }
      }
    }
    return blocks;
  }

  /** @param {any} ev @param {number} day @param {string} time @param {boolean} [skipped] @returns {Block} */
  #block(ev, day, time, skipped = false) {
    const start = minutesOf(time);
    const px = PX_PER_MIN[this.#zoom];
    const minutes = Math.max((ev.maxDurationSeconds ?? 0) / 60, MIN_BLOCK_PX / px);
    return { ev, day, start, end: Math.min(start + minutes, 24 * 60), time, skipped };
  }

  #renderGrid() {
    const px = PX_PER_MIN[this.#zoom];
    const height = Math.round(24 * 60 * px);
    const grid = $('pl-grid');
    grid.replaceChildren();
    const today = isoDate(new Date());

    // Capçalera: els dies
    const head = el('div', 'pl-head');
    head.append(el('div', 'pl-corner'));
    for (let day = 0; day < 7; day++) {
      const cell = el('div', 'pl-dayhead');
      if (this.#view === 'week') {
        const date = addDays(this.#monday, day);
        cell.append(el('span', 'pl-dayname', WEEKDAYS[day]), el('span', 'pl-daynum', String(date.getDate())));
        if (isoDate(date) === today) cell.classList.add('is-today');
      } else {
        cell.append(el('span', 'pl-dayname', WEEKDAYS[day]));
      }
      head.append(cell);
    }
    grid.append(head);

    // Cos: la columna de les hores i una columna per dia
    const body = el('div', 'pl-body');
    body.style.height = `${height}px`;
    const times = el('div', 'pl-times');
    const labelEvery = this.#zoom === 5 ? 15 : this.#zoom;
    for (let m = 0; m < 24 * 60; m += labelEvery) {
      const label = el('div', 'pl-timelabel', `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`);
      label.style.top = `${m * px}px`;
      times.append(label);
    }
    body.append(times);

    /** @type {HTMLElement[]} */
    const columns = [];
    for (let day = 0; day < 7; day++) {
      const column = el('div', 'pl-column');
      column.style.backgroundSize = `100% ${this.#zoom * px}px`;
      if (this.#view === 'week' && isoDate(addDays(this.#monday, day)) === today) column.classList.add('is-today');
      columns.push(column);
      body.append(column);
    }

    const blocks = this.#blocks();
    for (let day = 0; day < 7; day++) {
      const ofDay = blocks.filter((b) => b.day === day).sort((a, b) => a.start - b.start || a.ev.id - b.ev.id);
      for (const block of layoutLanes(ofDay)) columns[day].append(this.#item(block, px));
    }
    grid.append(body);

    if (this.#view === 'week') this.#drawNowLine();

    // La primera vegada (o en canviar de setmana o de zoom), cap a les 7 del matí o l'hora actual
    if (!this.#scrolled) {
      const wrap = $('pl-grid-wrap');
      const now = new Date();
      const isThisWeek = this.#view === 'week' && isoDate(this.#monday) === isoDate(weekStart(now));
      const minutes = isThisWeek ? Math.max(0, now.getHours() * 60 + now.getMinutes() - 60) : 7 * 60;
      wrap.scrollTop = minutes * px;
      this.#scrolled = true;
    }
  }

  /** @param {Block & { lane: number, lanes: number }} block @param {number} px */
  #item(block, px) {
    const { ev } = block;
    const info = modeInfo(ev.mode);
    const item = el('button', `pl-item ${info.css}`);
    item.type = 'button';
    if (!ev.enabled) item.classList.add('is-disabled');
    if (block.skipped) item.classList.add('is-skipped');
    if (ev.id === this.#selected) item.classList.add('is-selected');
    item.dataset.eventId = String(ev.id);
    const heightPx = (block.end - block.start) * px;
    item.style.top = `${block.start * px}px`;
    item.style.height = `${Math.max(MIN_BLOCK_PX, heightPx) - 2}px`;
    item.style.left = `calc(${(block.lane / block.lanes) * 100}% + 2px)`;
    item.style.width = `calc(${100 / block.lanes}% - 4px)`;

    const state = block.skipped ? "no sona pel canvi d'hora" : ev.enabled ? '' : 'desactivat';
    const line1 = el('div', 'pl-item-line');
    line1.append(icon(block.skipped ? 'bi-clock-history' : info.icon), ' ', el('span', 'pl-item-time', shortTime(block.time)), ' ',
      el('span', 'pl-item-name', ev.name));
    item.append(line1);
    if (heightPx >= 38) {
      const parts = [info.label, durationText(ev.maxDurationSeconds), state].filter(Boolean);
      item.append(el('div', 'pl-item-sub', parts.join(' · ')));
    }
    if (ev.recurrence === 'CADA_N_SETMANES' && heightPx >= 56) item.append(el('span', 'pl-item-badge', `Cada ${ev.intervalWeeks} setmanes`));
    item.title = `${shortTime(block.time)} · ${ev.name} · ${info.label}${state ? ` · ${state}` : ''}`;
    item.addEventListener('click', () => this.#select(ev.id));
    return item;
  }

  #drawNowLine() {
    document.querySelector('.pl-now')?.remove();
    const now = new Date();
    const day = Math.round((new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime() - this.#monday.getTime()) / 86_400_000);
    if (day < 0 || day > 6) return;
    const column = document.querySelectorAll('.pl-column')[day];
    if (!column) return;
    const line = el('div', 'pl-now');
    line.style.top = `${(now.getHours() * 60 + now.getMinutes()) * PX_PER_MIN[this.#zoom]}px`;
    line.title = 'Ara';
    column.append(line);
  }

  /** Setmana tipus: els esdeveniments que no són setmanals no tenen lloc fix a la graella; van en una llista. */
  #renderOthers() {
    const box = $('pl-others');
    box.replaceChildren();
    if (this.#view !== 'template') {
      box.classList.add('d-none');
      return;
    }
    const others = [...this.#events.values()].filter((ev) =>
      !repeatsWithinDay(ev) && this.#isShown(ev) && ev.recurrence !== 'SETMANAL' && ev.recurrence !== 'CADA_N_SETMANES');
    box.classList.toggle('d-none', others.length === 0);
    if (others.length === 0) return;
    box.append(el('div', 'pl-others-title', 'Altres recurrències (no tenen un lloc fix a la setmana tipus)'));
    for (const ev of others.sort((a, b) => a.startTime.localeCompare(b.startTime))) {
      const info = modeInfo(ev.mode);
      const row = el('button', `pl-other ${info.css}${ev.enabled ? '' : ' is-disabled'}`);
      row.type = 'button';
      row.append(icon(info.icon), ' ', el('span', 'pl-item-time', shortTime(ev.startTime)), ' ',
        el('strong', '', ev.name), ' · ', el('span', '', recurrenceText(ev)), ev.enabled ? '' : ' · desactivat');
      row.addEventListener('click', () => this.#select(ev.id));
      box.append(row);
    }
  }

  // --- Detall ---

  /** @param {number} id */
  #select(id) {
    this.#selected = id;
    for (const node of document.querySelectorAll('.pl-item.is-selected, .pl-repeat.is-selected')) node.classList.remove('is-selected');
    for (const node of document.querySelectorAll(`.pl-item[data-event-id="${id}"]`)) node.classList.add('is-selected');
    this.#renderLegend();
    this.#showDetail(id);
  }

  /** @param {number} id */
  async #showDetail(id) {
    const panel = $('pl-detail');
    const ev = this.#events.get(id);
    panel.replaceChildren();
    if (!ev) {
      this.#selected = null;
      panel.append(el('p', 'pl-detail-empty', 'Tria un esdeveniment de la graella per veure\'n el detall.'));
      return;
    }
    const info = modeInfo(ev.mode);

    const badges = el('div', 'd-flex flex-wrap gap-2 mb-2');
    const modeBadge = el('span', `pl-chip ${info.css}`);
    modeBadge.append(icon(info.icon), ' ', el('span', '', info.label));
    badges.append(modeBadge, ev.enabled
      ? statusLabel('text-success', 'bi-check-circle-fill', 'Activat')
      : statusLabel('text-warning', 'bi-pause-circle-fill', 'Desactivat: no es dispara'));
    panel.append(badges, el('h2', 'pl-detail-name', ev.name));

    const facts = el('dl', 'pl-facts');
    const fact = (/** @type {string} */ term, /** @type {string} */ value) => facts.append(el('dt', '', term), el('dd', '', value));
    fact('Hora d\'inici', ev.startTime);
    fact('Durada a la graella', ev.maxDurationSeconds ? durationText(ev.maxDurationSeconds) : 'sense durada màxima (es dibuixa com una marca)');
    fact('Recurrència', recurrenceText(ev));
    if (ev.mode === 'TALLA' || ev.mode === 'ENCUA') fact('Esborra la cua', ev.clearQueue ? 'sí' : 'no');
    if (ev.mode === 'TALLA') fact('Fos de la que sona', ev.fadeMs == null ? '1.000 ms (per defecte)' : `${ev.fadeMs} ms`);
    if (ev.duckingDb != null) fact('Atenuació de la música', `${ev.duckingDb} dB`);
    fact('Prioritat i tolerància', `${ev.priority} · ${ev.toleranceSeconds} s`);
    panel.append(facts);

    if (ev.excludedDates?.length) {
      const exceptions = el('div', 'pl-exceptions');
      exceptions.append(icon('bi-slash-circle'), ' ', el('span', '', `No s'emet: ${ev.excludedDates.map(shortDate).join(', ')}`));
      panel.append(exceptions);
    }

    // Pròximes emissions (les calcula el motor, amb els canvis d'hora)
    panel.append(el('h3', 'pl-detail-title', 'Pròximes emissions'));
    const next = el('div', 'pl-next');
    next.append(el('span', 'text-body-secondary', 'Calculant…'));
    panel.append(next);

    panel.append(el('h3', 'pl-detail-title', 'Accions, en ordre d\'emissió'));
    const actions = el('ol', 'pl-actions');
    if (ev.actions.length === 0) actions.append(el('li', 'pl-action-empty', 'Cap acció.'));
    for (const action of ev.actions) {
      const li = el('li', `pl-action${action.missing ? ' is-missing' : ''}`);
      const main = el('div', 'pl-action-label', action.label ?? action.type);
      const detail = el('div', 'pl-action-detail', [action.type, action.detail].filter(Boolean).join(' · '));
      li.append(main, detail);
      if (action.daysMask != null) li.append(el('div', 'pl-action-days', `Només ${daysText(action.daysMask)}`));
      if (action.missing) {
        const warning = el('div', 'pl-action-warning');
        warning.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', 'No sonarà: la pista o el fitxer falta'));
        li.append(warning);
      }
      actions.append(li);
    }
    panel.append(actions);

    if (ev.notes) panel.append(el('p', 'pl-notes', ev.notes));
    panel.append(el('p', 'pl-detail-foot', 'Per ara només es pot consultar: l\'editor arriba al pas 8b.'));

    try {
      const now = new Date();
      const result = await this.#send('scheduler.occurrences', {
        eventId: id, from: now.toISOString(), to: addDays(now, 399).toISOString(), limit: 10,
      });
      if (this.#selected !== id) return;
      next.replaceChildren();
      if (result.occurrences.length === 0) next.append(el('span', 'text-body-secondary', 'Cap en els pròxims 400 dies.'));
      for (const o of result.occurrences) {
        const date = parseDate(o.date);
        const chip = el('span', `pl-next-chip${o.skippedClockChange ? ' is-skipped' : ''}`,
          `${DAY_NAMES[(date.getDay() + 6) % 7]} ${date.getDate()}/${date.getMonth() + 1} ${shortTime(o.time)}`);
        if (o.skippedClockChange) {
          chip.prepend(icon('bi-clock-history'), ' ');
          chip.title = 'Aquesta hora no existeix pel canvi d\'hora: no sonarà';
        }
        next.append(chip);
      }
    } catch (err) {
      next.replaceChildren(el('span', 'text-danger', `No s'han pogut calcular: ${/** @type {Error} */ (err).message}`));
    }
  }
}
