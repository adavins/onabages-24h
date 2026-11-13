// @ts-check
// Calendari d'una pista (fase 4, pas 2b; D-104; dins de la pestanya Pistes, D-113): s'obre des de la fitxa d'una pista
// (de qualsevol tipus: falca, càpsula, promo…) amb «Calendari…», i «← Torna a les pistes» hi torna. La graella setmanal
// té una fila per a cada hora on hi ha esdeveniments que poden portar pistes.
// Fent clic a una casella, la pista entra o surt d'aquell esdeveniment aquell dia (calendar.set). A sota, els passis.
// Tot ho calcula el motor; aquí només es mostra.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { typeName, formatDuration } from '../format.js';
import { modeInfo } from '../schedule.js';
import {
  DAY_SHORT, DAY_NAMES, cellWarnings, cellTitle, blockTimeProblem, normalizeBlockTime, dateText, monthText, passes, periodText,
  withExtraRows, trackOnlyRows, contentTimes,
} from '../calendar-form.js';

export class CalendarView {
  #send;
  #visible = false;
  /** La pista triada. @type {number | null} */
  #trackId = null;
  /** L'últim calendari (calendar.get). @type {any} */
  #calendar = null;
  /** Hi ha un canvi enviant-se: no es deixa fer clic a cap altra casella. */
  #busy = false;
  /** Torna a la llista de pistes (D-113). @type {() => void} */
  #back;
  /** Els esdeveniments que es repeteixen dins del dia ja s'han avisat (no cada vegada). @type {Set<number>} */
  #warnedRepeats = new Set();
  /** Hores afegides amb «+ Afegeix una hora» que encara no tenen res (D-105: no es desen). @type {Set<string>} */
  #extraTimes = new Set();
  /** Què es veu: tots els esdeveniments (per programar) o només on sona la pista (D-105). @type {'all' | 'track'} */
  #mode = 'all';
  /** El contingut obert sota la graella (pas 2c): quin esdeveniment i quin dia, i el que ha dit calendar.content. */
  /** @type {{ eventId: number, day: number, time: string } | null} */
  #contentOf = null;
  /** @type {any} */
  #content = null;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {() => void} back  torna a la llista de pistes (la pestanya Pistes, tal com era)
   */
  constructor(send, back) {
    this.#send = send;
    this.#back = back;
    this.#renderEditor();
  }

  show() {
    this.#visible = true;
    this.refresh();
  }

  hide() {
    this.#visible = false;
  }

  /** Torna a demanar el calendari de la pista triada (si es mostra): p. ex. si s'ha canviat un esdeveniment. */
  async refresh() {
    if (!this.#visible || this.#trackId == null) return;
    const trackId = this.#trackId;
    /** @type {any} */
    let calendar;
    try {
      calendar = await this.#send('calendar.get', { trackId });
    } catch {
      calendar = false; // la pista s'ha esborrat o no hi ha connexió (null = encara s'està carregant)
    }
    if (trackId !== this.#trackId) return; // mentrestant se n'ha triat una altra (p. ex. des de la fitxa): no la trepitja
    this.#calendar = calendar;
    await this.#loadContent();
    this.#renderEditor();
  }

  /** Torna a demanar el contingut obert (si n'hi ha): p. ex. després de posar-hi o treure'n la pista. */
  async #loadContent() {
    if (!this.#contentOf || this.#trackId == null) {
      this.#content = null;
      return;
    }
    try {
      this.#content = await this.#send('calendar.content', { eventId: this.#contentOf.eventId, day: this.#contentOf.day, trackId: this.#trackId });
    } catch {
      this.#contentOf = null; // l'esdeveniment s'ha esborrat (p. ex. un bloc buit)
      this.#content = null;
    }
  }

  /**
   * Obre el calendari d'una pista (des de la seva fitxa, a la pestanya Pistes; D-113). Qui la crida ja ha mostrat la vista.
   * @param {{ id: number, title: string }} track
   */
  async openTrack(track) {
    if (track.id !== this.#trackId) {
      this.#extraTimes.clear(); // les hores provisionals eren per a l'altra pista
      this.#contentOf = null;
    }
    this.#trackId = track.id;
    this.#calendar = null;
    this.#renderEditor(); // de seguida: «Torna a les pistes» es veu mentre es carrega
    await this.refresh();
  }

  // --- Graella ---

  #renderEditor() {
    const box = $('cal-editor');
    const cal = this.#calendar;
    const back = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary cal-back'));
    back.type = 'button';
    back.append(icon('bi-arrow-left'), ' Torna a les pistes');
    back.addEventListener('click', () => this.#back());
    if (this.#trackId == null || !cal) {
      box.replaceChildren(back, el('p', 'text-body-secondary mt-3 mb-0',
        this.#trackId == null ? 'Obre el calendari des de la fitxa d\'una pista (botó «Calendari…»).'
          : cal === false ? 'No s\'ha pogut llegir el calendari d\'aquesta pista.' : 'Carregant el calendari…'));
      return;
    }
    const t = cal.track;

    // Capçalera: la pista, el tipus, la durada i les dates
    const head = el('div', 'cal-head');
    const title = el('div', 'cal-title');
    title.append(el('strong', '', `${t.artist ? `${t.artist} - ` : ''}${t.title || '(sense títol)'}`), ' ',
      el('span', 'badge text-bg-secondary', typeName(t.type)), ' ',
      el('span', 'text-body-secondary', formatDuration(t.durationMs)));
    const dates = t.validFrom || t.validUntil
      ? `Vigent ${t.validFrom ? `del ${dateText(t.validFrom)}` : ''}${t.validUntil ? ` al ${dateText(t.validUntil)}` : ' sense data de fi'}`
      : 'Sense dates d\'inici ni de fi';
    // Una sola fila (D-116): a l'esquerra, tornar i la pista (les dates i els avisos en una línia petita a sota);
    // a la dreta, quina vista
    const info = el('div', 'cal-info');
    const sub = el('div', 'cal-sub');
    sub.append(el('span', '', dates));
    if (t.fileState === 'missing') sub.append(statusLabel('text-danger', 'bi-exclamation-octagon-fill', 'El fitxer falta: no sonarà'));
    info.append(title, sub);
    head.append(back, info, this.#modeSwitch());

    // A sota de la capçalera: la graella (amb el seu desplaçament) i, si n'hi ha d'obert, el contingut de l'esdeveniment
    // en un panell a la dreta (D-114). Es conserva on s'havia baixat (la vista es torna a dibuixar a cada clic).
    const scroll = box.querySelector('.cal-main')?.scrollTop ?? 0;
    const main = el('div', 'cal-main');
    const side = this.#mode === 'all' && this.#content ? this.#contentPanel(this.#content) : null;
    if (this.#mode === 'track') main.append(this.#trackOnlyTable(cal), this.#counts(cal.counts));
    else main.append(this.#table(cal), this.#addBlock(), this.#counts(cal.counts));
    const body = el('div', `cal-body${side ? ' has-side' : ''}`);
    body.append(main);
    if (side) {
      const aside = el('aside', 'cal-side');
      aside.setAttribute('aria-label', 'Contingut de l\'esdeveniment');
      aside.append(side);
      body.append(aside);
    }
    box.replaceChildren(head, body);
    main.scrollTop = scroll;
  }

  /** «Tots els esdeveniments» (per programar) o «Només aquesta pista» (com queda la setmana de la pista, D-105). */
  #modeSwitch() {
    const group = el('div', 'btn-group btn-group-sm cal-mode');
    group.setAttribute('role', 'group');
    group.setAttribute('aria-label', 'Què es veu a la graella');
    const option = (/** @type {'all' | 'track'} */ mode, /** @type {string} */ iconName, /** @type {string} */ text, /** @type {string} */ help) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', `btn ${this.#mode === mode ? 'btn-primary' : 'btn-outline-secondary'}`));
      b.type = 'button';
      b.title = help;
      b.setAttribute('aria-pressed', String(this.#mode === mode));
      b.append(icon(iconName), ` ${text}`);
      b.addEventListener('click', () => {
        this.#mode = mode;
        this.#renderEditor();
      });
      group.append(b);
    };
    option('all', 'bi-grid-3x3', 'Tots els esdeveniments', 'Per programar: totes les hores amb esdeveniments, i les caselles buides per crear-hi un bloc de falques');
    option('track', 'bi-check2-square', 'Només aquesta pista', 'Com queda la setmana de la pista: només les hores i els dies on sona');
    return group;
  }

  /** Vista «Només aquesta pista»: les hores on sona i, cada dia, si hi és (sense esdeveniments ni caselles buides). @param {any} cal */
  #trackOnlyTable(cal) {
    const rows = trackOnlyRows(cal.rows);
    if (rows.length === 0) return el('p', 'text-body-secondary', 'Aquesta pista encara no està programada enlloc. Canvia a «Tots els esdeveniments» per programar-la.');
    const table = el('table', 'cal-grid cal-grid-track');
    const head = el('tr', '');
    head.append(el('th', 'cal-time', 'Hora'), ...DAY_SHORT.map((d, i) => {
      const th = el('th', '', d);
      th.title = DAY_NAMES[i];
      return th;
    }));
    const thead = el('thead', '');
    thead.append(head);
    const tbody = el('tbody', '');
    for (const row of rows) {
      const tr = el('tr', '');
      tr.append(el('th', 'cal-time', row.time.slice(0, 5)));
      row.days.forEach((on, day) => {
        const td = el('td', on ? 'cal-on' : 'cal-off');
        if (on) {
          td.append(icon('bi-check-lg'), el('span', 'visually-hidden', 'Sona'));
          td.title = `${DAY_NAMES[day]} ${row.time.slice(0, 5)}: sona`;
        }
        tr.append(td);
      });
      tbody.append(tr);
    }
    table.append(thead, tbody);
    const wrap = el('div', 'cal-grid-wrap');
    wrap.append(table);
    return wrap;
  }

  /** @param {any} cal */
  #table(cal) {
    const allRows = withExtraRows(cal.rows, this.#extraTimes); // sempre hi ha les 24 hores en punt
    const table = el('table', 'cal-grid');
    const thead = el('thead', '');
    const headRow = el('tr', '');
    headRow.append(el('th', 'cal-time', 'Hora'), ...DAY_SHORT.map((d, i) => {
      const th = el('th', '', d);
      th.title = DAY_NAMES[i];
      return th;
    }));
    thead.append(headRow);
    const tbody = el('tbody', '');
    for (const row of allRows) {
      const tr = el('tr', /** @type {any} */ (row).provisional ? 'cal-provisional' : '');
      const th = el('th', 'cal-time', row.time.slice(0, 5));
      if (/** @type {any} */ (row).provisional) th.title = 'Hora nova: encara no hi ha res. Marca un dia per crear-hi el bloc de falques';
      tr.append(th);
      row.days.forEach((/** @type {any} */ cell, /** @type {number} */ day) => tr.append(this.#cell(cell, day, row.time)));
      tbody.append(tr);
    }
    table.append(thead, tbody);
    const wrap = el('div', 'cal-grid-wrap');
    wrap.append(table);
    return wrap;
  }

  /** @param {any} cell @param {number} day @param {string} time */
  #cell(cell, day, time) {
    const td = el('td', '');
    if (!cell) {
      // Casella buida (D-105): un clic hi programa la pista en un bloc de falques d'aquella hora (es crea si cal)
      const b = /** @type {HTMLButtonElement} */ (el('button', 'cal-cell cal-cell-empty'));
      b.type = 'button';
      b.title = `${DAY_NAMES[day]} ${time.slice(0, 5)}: no hi ha cap esdeveniment.\nFes clic per programar-hi la pista: anirà al bloc de falques de les ${time.slice(0, 5)} (es crea si no n'hi ha).`;
      b.setAttribute('aria-label', `${DAY_NAMES[day]} ${time.slice(0, 5)}: buida, programa-hi la pista`);
      b.append(icon('bi-plus'));
      b.disabled = this.#busy;
      b.addEventListener('click', () => this.#change(time, day, true));
      td.append(b);
      return td;
    }
    const info = modeInfo(cell.mode);
    const warnings = cellWarnings(cell);
    const b = /** @type {HTMLButtonElement} */ (el('button', `cal-cell ${info.css}${cell.marked ? ' is-marked' : ''}${cell.enabled ? '' : ' is-disabled'}`));
    b.type = 'button';
    b.title = cellTitle(cell, day, time);
    b.setAttribute('aria-pressed', String(cell.marked));
    b.setAttribute('aria-label', `${DAY_NAMES[day]} ${time.slice(0, 5)}, ${cell.name}: ${cell.marked ? 'hi és' : 'no hi és'}`);
    // Sempre icona + text (CLAUDE.md): la marca, el nom i, si n'hi ha, l'avís
    b.append(icon(cell.marked ? 'bi-check-square-fill' : 'bi-square'), ' ', el('span', 'cal-cell-name', cell.name));
    if (!cell.enabled) b.append(' ', icon('bi-slash-circle'));
    if (warnings.length > (cell.enabled ? 0 : 1)) b.append(' ', icon('bi-exclamation-triangle-fill'));
    b.disabled = this.#busy;
    b.addEventListener('click', () => this.#toggle(cell, day, time));
    // Al costat, el botó per veure el contingut en ordre d'emissió (pas 2c): el clic a la casella ja posa o treu la pista
    const open = /** @type {HTMLButtonElement} */ (el('button', 'cal-cell-open'));
    open.type = 'button';
    const shown = this.#contentOf?.eventId === cell.eventId && this.#contentOf.day === day;
    if (shown) open.classList.add('active');
    open.title = `Contingut de «${cell.name}» el ${DAY_NAMES[day]}, en ordre d'emissió (per moure-hi la pista)`;
    open.setAttribute('aria-label', open.title);
    open.setAttribute('aria-pressed', String(shown));
    open.append(icon('bi-card-list'));
    open.addEventListener('click', () => this.#openContent(shown ? null : { eventId: cell.eventId, day, time }));
    const pair = el('div', 'cal-cell-pair');
    pair.append(b, open);
    td.append(pair);
    return td;
  }

  // --- Contingut d'un esdeveniment, en ordre d'emissió (pas 2c) ---

  /** Obre (o tanca, amb null) el contingut d'una casella. @param {{ eventId: number, day: number, time: string } | null} of */
  async #openContent(of) {
    this.#contentOf = of;
    await this.#loadContent();
    this.#renderEditor(); // el panell surt a la dreta (D-114): no cal desplaçar res
  }

  /** @param {any} content  de calendar.content */
  #contentPanel(content) {
    const ev = content.event;
    const time = this.#contentOf?.time ?? ev.time;
    const info = modeInfo(ev.mode);
    const box = el('section', 'cal-content');
    box.setAttribute('aria-label', 'Contingut de l\'esdeveniment');

    const head = el('div', 'cal-content-head');
    const title = el('div', '');
    const tag = el('span', `cal-mode-tag ${info.css}`);
    tag.append(icon(info.icon), ` ${ev.mode}`); // color + icona + text
    title.append(tag, ' ', el('strong', '', ev.name), ` · ${DAY_NAMES[content.day]} ${time.slice(0, 5)}`);
    if (!ev.enabled) title.append(' ', statusLabel('text-warning small', 'bi-slash-circle', 'desactivat'));
    const close = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    close.type = 'button';
    close.append(icon('bi-x-lg'), ' Tanca');
    close.addEventListener('click', () => this.#openContent(null));
    head.append(title, close);
    box.append(head);

    const notes = [];
    notes.push(ev.mode === 'TALLA'
      ? 'Comença a l\'hora exacta. Les hores de cada pista són previstes (amb els encavalcaments poden avançar uns segons).'
      : 'ENCUA: entra a la cua a aquesta hora i comença quan s\'acaba la pista que sona; les hores són aproximades.');
    if (ev.repeats) notes.push('Es repeteix dins del dia: aquestes són les hores de la primera vegada.');
    notes.push('L\'ordre és el mateix tots els dies: moure la pista aquí també la mou els altres dies on hi és.');
    box.append(el('p', 'form-text mt-1 mb-2', notes.join(' ')));

    const items = /** @type {any[]} */ (content.items);
    if (items.length === 0) {
      box.append(el('p', 'text-body-secondary m-0', 'Aquest dia no hi ha res.'));
      return box;
    }
    const { starts, end } = contentTimes(items, time);
    const list = el('ol', 'cal-content-list');
    items.forEach((item, i) => {
      const li = el('li', `cal-content-item${item.isTrack ? ' is-track' : ''}`);
      li.append(el('span', 'cal-content-time', starts[i] ?? (item.type === 'PISTA' || item.type === 'STREAM' ? '?' : '')));
      const text = el('div', 'cal-content-text');
      text.append(el('div', 'cal-content-label', item.label));
      if (item.detail) text.append(el('div', 'form-text m-0', item.detail));
      li.append(text);
      if (item.missing) li.append(statusLabel('text-danger small', 'bi-exclamation-octagon-fill', 'no sonarà'));
      if (item.isTrack) {
        li.append(statusLabel('text-success small', 'bi-check-square-fill', 'aquesta pista'),
          this.#moveButton(-1, i === 0), this.#moveButton(1, i === items.length - 1));
      }
      list.append(li);
    });
    box.append(list);
    if (!items.some((it) => it.isTrack)) box.append(el('p', 'form-text m-0', 'La pista no hi és aquest dia: marca la casella per posar-la-hi.'));
    const footer = el('div', 'form-text mt-2', end ? `S'acaba cap a les ${end}.` : 'No se sap quan s\'acaba (hi ha alguna durada desconeguda).');
    box.append(footer);
    if (end && ev.maxDurationSeconds) {
      const [h, m, s] = time.split(':').map(Number);
      const [eh, em, es] = end.split(':').map(Number);
      const used = ((eh * 3600 + em * 60 + es) - (h * 3600 + m * 60 + s) + 86400) % 86400;
      if (used > ev.maxDurationSeconds) {
        box.append(statusLabel('text-warning small', 'bi-exclamation-triangle-fill',
          `Dura més que la durada màxima de l'esdeveniment (${formatDuration(ev.maxDurationSeconds * 1000)}).`));
      }
    }
    return box;
  }

  /** @param {-1 | 1} delta @param {boolean} atEnd */
  #moveButton(delta, atEnd) {
    const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary cal-content-move'));
    b.type = 'button';
    b.append(icon(delta < 0 ? 'bi-arrow-up' : 'bi-arrow-down'));
    b.title = delta < 0 ? 'Mou la pista una posició amunt (sona abans)' : 'Mou la pista una posició avall (sona després)';
    b.setAttribute('aria-label', b.title);
    b.disabled = atEnd || this.#busy;
    b.addEventListener('click', () => this.#move(delta));
    return b;
  }

  /** @param {-1 | 1} delta */
  async #move(delta) {
    if (this.#busy || !this.#contentOf || this.#trackId == null) return;
    this.#busy = true;
    try {
      this.#content = await this.#send('calendar.move', { eventId: this.#contentOf.eventId, day: this.#contentOf.day, trackId: this.#trackId, delta });
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut moure', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    } finally {
      this.#busy = false;
    }
    this.#renderEditor();
  }

  /** @param {any} cell @param {number} day @param {string} time */
  async #toggle(cell, day, time) {
    if (this.#busy || this.#trackId == null) return;
    // Un esdeveniment que es repeteix dins del dia: la pista hi seria a totes les hores. S'avisa una vegada
    if (!cell.marked && cell.repeats && !this.#warnedRepeats.has(cell.eventId)) {
      const ok = await confirmAction({
        title: 'Esdeveniment que es repeteix',
        message: `«${cell.name}» es repeteix dins del dia: la pista sonarà a totes les seves hores, no només a aquesta.`,
        confirm: 'Posa-la igualment',
      });
      if (!ok) return;
      this.#warnedRepeats.add(cell.eventId);
    }
    await this.#change(time, day, !cell.marked);
  }

  /**
   * Posa o treu la pista de la casella (calendar.set). Si la casella és buida, el motor la posa al bloc de falques
   * d'aquella hora (que crea si cal); un bloc que es queda buit s'esborra (D-105).
   * @param {string} time @param {number} day @param {boolean} marked
   */
  async #change(time, day, marked) {
    if (this.#busy || this.#trackId == null) return;
    this.#busy = true;
    try {
      this.#calendar = await this.#send('calendar.set', { trackId: this.#trackId, time, day, marked });
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut canviar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    } finally {
      this.#busy = false;
    }
    await this.#loadContent(); // el contingut obert pot haver canviat (o el bloc s'ha esborrat)
    this.#renderEditor();
  }

  // --- «+ Afegeix una hora»: una fila provisional, sense crear res (D-105) ---

  #addBlock() {
    const box = el('div', 'cal-add');
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm cal-add-time'));
    input.type = 'text';
    input.placeholder = 'HH:MM';
    input.setAttribute('aria-label', 'Hora del bloc de falques nou');
    const button = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    button.type = 'button';
    button.append(icon('bi-plus-lg'), ' Afegeix una hora');
    button.title = 'Afegeix una fila a la graella (en múltiples de 5 minuts). No es crea res fins que hi marquis un dia';
    const state = el('span', 'form-text m-0', '');
    const add = () => {
      const problem = blockTimeProblem(input.value);
      if (problem) {
        state.textContent = problem;
        return;
      }
      this.#extraTimes.add(`${normalizeBlockTime(input.value)}:00`);
      this.#renderEditor();
    };
    button.addEventListener('click', add);
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') add(); });
    box.append(input, button, state);
    return box;
  }

  // --- Passis ---

  /** @param {any} counts */
  #counts(counts) {
    const box = el('div', 'cal-counts');
    const total = el('div', 'cal-total');
    total.append(el('strong', '', passes(counts.total)), ` ${periodText(counts)}`);
    box.append(total);

    // Setmana tipus: per dia de la setmana
    const week = el('div', 'cal-week');
    const sum = counts.perWeekday.reduce((/** @type {number} */ a, /** @type {number} */ b) => a + b, 0);
    week.append(el('span', 'text-body-secondary', `Setmana tipus (${passes(sum)}): `),
      ...DAY_SHORT.map((d, i) => el('span', 'cal-week-day', `${d} ${counts.perWeekday[i]}`)));
    box.append(week);

    if (counts.months.length > 0) {
      box.append(el('div', 'cal-months', `Per mes: ${counts.months.map((/** @type {any} */ m) => `${monthText(m.month)}: ${m.count}`).join(' · ')}`));
    }
    if (counts.weeks.length > 0) {
      box.append(el('div', 'cal-months', `Per setmana: ${counts.weeks.map((/** @type {any} */ w) => `${dateText(w.from).slice(0, 5)}: ${w.count}`).join(' · ')}`));
    }
    if (counts.markedInDisabled > 0) {
      box.append(statusLabel('text-warning small', 'bi-exclamation-triangle-fill',
        `${counts.markedInDisabled} ${counts.markedInDisabled === 1 ? 'dia marcat és' : 'dies marcats són'} d'esdeveniments desactivats: no compten (no sonaran fins que s'activin).`));
    }
    return box;
  }
}
