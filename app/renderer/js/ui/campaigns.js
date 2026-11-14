// @ts-check
// Pantalla «Campanyes» (fase 4, pas 3c; D-122, D-124): la llista de campanyes (amb l'estat) i l'editor d'una campanya:
// les dades, el repartiment, els passis contractats i les falques (amb dates, dies i pes propis, en ordre). Es desa al motor
// (campaigns.save), que en calcula el pla de passis. Mostra on sona (esdeveniments amb l'acció «Campanya») i el resum del pla.
// «Calendari…» obre la graella setmanal per programar-la a les hores que es vulgui (fase 4, pas 3d), com una pista.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { formDialog } from './form-dialog.js';
import { typeBadge } from './type-badge.js';
import { formatDuration } from '../format.js';
import { todayIso } from '../event-form.js';
import { DAY_NAMES, DAY_SHORT } from '../calendar-form.js';
import {
  STATES, SELECTIONS, MAX_WEIGHT, newCampaign, newSpot, campaignFromApi, campaignToApi, campaignChanged, campaignProblems,
  moveSpot, spotWarnings, datesText, effectiveState,
} from '../campaign-form.js';
import { planPanel } from './campaign-plan.js';

export class CampaignsView {
  #send;
  /** @type {any[]} */
  #campaigns = [];
  #visible = false;
  #loaded = false;
  /** La campanya oberta, tal com és al motor (null = una de nova sense desar). @type {any | null} */
  #campaign = null;
  /** El formulari (null = cap campanya oberta). @type {import('../campaign-form.js').FormCampaign | null} */
  #form = null;
  /** La falca que s'està arrossegant, o null. @type {number | null} */
  #dragFrom = null;
  /** Obre el calendari de la campanya (fase 4, pas 3d). @type {(campaign: { id: number }) => void} */
  #openCalendar;
  /** Torna a mostrar l'estat de la capçalera (p. ex. si canvia la data de fi). @type {(() => void) | null} */
  #syncState = null;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(campaign: { id: number }) => void} openCalendar  obre el calendari d'una campanya desada
   */
  constructor(send, openCalendar) {
    this.#send = send;
    this.#openCalendar = openCalendar;
    $('cp-new').addEventListener('click', () => this.#new());
    $('cp-filter').addEventListener('change', () => this.#renderList());
    $('cp-search').addEventListener('input', () => this.#renderList());
    document.addEventListener('click', (e) => {
      if (!(/** @type {HTMLElement} */ (e.target).closest?.('#cp-editor .ee-more'))) this.#closeMenu();
    });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.#closeMenu(); });
    this.#renderEditor();
  }

  /** Tanca el menú «Més» de la capçalera, si és obert. */
  #closeMenu() {
    const menu = document.querySelector('#cp-editor .ee-menu');
    if (!menu || menu.classList.contains('d-none')) return;
    menu.classList.add('d-none');
    document.querySelector('#cp-editor .ee-more > button')?.setAttribute('aria-expanded', 'false');
  }

  show() {
    this.#visible = true;
    if (!this.#loaded) this.refresh();
  }

  hide() {
    this.#visible = false;
  }

  /** Hi ha canvis sense desar. */
  get dirty() {
    return !!this.#form && campaignChanged(this.#campaign, this.#form);
  }

  /** En connectar-se o quan canvien les campanyes (només si es mostra). */
  async refresh() {
    if (!this.#visible) {
      this.#loaded = false;
      return;
    }
    this.#loaded = true;
    try {
      this.#campaigns = (await this.#send('campaigns.list')).campaigns;
    } catch {
      return; // sense connexió
    }
    // La campanya oberta, si no s'hi està escrivint, amb la versió del motor
    if (this.#campaign && !this.dirty) {
      if (this.#campaigns.some((c) => c.id === this.#campaign.id)) await this.#load(this.#campaign.id);
      else this.#close();
    }
    this.#renderList();
    this.#renderEditor();
  }

  // --- Llista ---

  #renderList() {
    const filter = /** @type {HTMLSelectElement} */ ($('cp-filter')).value;
    const text = /** @type {HTMLInputElement} */ ($('cp-search')).value.trim().toLocaleLowerCase('ca');
    const shown = this.#campaigns.filter((c) => {
      const finished = c.effectiveState === 'ACABADA';
      if (filter === 'current' && finished) return false;
      if (filter === 'finished' && !finished) return false;
      return !text || `${c.name} ${c.advertiser ?? ''}`.toLocaleLowerCase('ca').includes(text);
    });
    const list = $('cp-list');
    if (shown.length === 0) {
      list.replaceChildren(el('li', 'form-text px-3', this.#campaigns.length === 0
        ? 'Encara no hi ha cap campanya. Crea\'n una amb «Nova».' : 'Cap campanya amb aquests criteris.'));
      return;
    }
    const today = todayIso();
    list.replaceChildren(...shown.map((c) => {
      const li = el('li', '');
      const b = el('button', 'rt-item');
      b.type = 'button';
      b.classList.toggle('active', this.#campaign?.id === c.id);
      b.append(el('span', 'rt-item-name', c.name));
      b.append(stateLabel(c.effectiveState, 'rt-item-active'));
      const meta = [c.advertiser, datesText(c.startDate, c.endDate), `${c.spots.length} ${c.spots.length === 1 ? 'falca' : 'falques'}`];
      b.append(el('span', 'rt-item-meta cp-item-meta', meta.filter(Boolean).join(' · ')));
      if (c.effectiveState === 'ACTIVA' && c.startDate > today) b.append(el('span', 'rt-item-meta cp-item-meta', 'encara no ha començat'));
      b.addEventListener('click', () => this.#select(c));
      li.append(b);
      return li;
    }));
  }

  /** @param {any} c */
  async #select(c) {
    if (this.#campaign?.id === c.id && this.#form) return;
    if (!(await this.#leave())) return;
    await this.#load(c.id);
    this.#renderList();
    this.#renderEditor();
  }

  /** Llegeix la campanya sencera (amb les pistes de les falques i on sona). @param {number} id */
  async #load(id) {
    try {
      const { campaign } = await this.#send('campaigns.get', { campaignId: id });
      this.#open(campaign);
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut obrir la campanya', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** Si hi ha canvis sense desar, pregunta si es poden perdre. */
  async #leave() {
    if (!this.dirty) return true;
    return confirmAction({
      title: 'Canvis sense desar',
      message: 'La campanya oberta té canvis que no s\'han desat. Si continues, es perdran.',
      confirm: 'Descarta els canvis',
    });
  }

  /** @param {any | null} campaign */
  #open(campaign) {
    this.#campaign = campaign;
    this.#form = campaign ? campaignFromApi(campaign) : newCampaign(todayIso());
  }

  #close() {
    this.#campaign = null;
    this.#form = null;
  }

  async #new() {
    if (!(await this.#leave())) return;
    this.#open(null);
    this.#renderList();
    this.#renderEditor();
    /** @type {HTMLInputElement | null} */ (document.getElementById('cp-name'))?.focus();
  }

  async #closeEditor() {
    if (!(await this.#leave())) return;
    this.#close();
    this.#renderList();
    this.#renderEditor();
  }

  #duplicate() {
    if (!this.#form) return;
    const copy = { ...this.#form, name: `${this.#form.name} (còpia)`, state: 'ESBORRANY', spots: this.#form.spots.map((s) => ({ ...s })) };
    this.#campaign = null;
    this.#form = copy;
    this.#renderList();
    this.#renderEditor();
  }

  async #delete() {
    const c = this.#campaign;
    if (!c) return;
    if (!(await confirmAction({ title: 'Esborrar la campanya', message: `S'esborrarà la campanya «${c.name}» amb les seves falques (les pistes es queden a la biblioteca). No es pot desfer.`, confirm: 'Esborra-la' }))) return;
    try {
      await this.#send('campaigns.delete', { campaignId: c.id });
      this.#close();
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut esborrar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  async #save() {
    const form = this.#form;
    if (!form || campaignProblems(form).length > 0) return;
    /** @type {Record<string, any>} */
    const params = campaignToApi(form);
    if (this.#campaign) params.campaignId = this.#campaign.id;
    try {
      const { campaign } = await this.#send('campaigns.save', params);
      this.#open(campaign);
      await this.refresh(); // la llista (campaigns.changed també hi arriba)
      this.#renderList();
      this.#renderEditor();
      this.#flash('Desat. El pla de passis s\'ha tornat a calcular.');
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut desar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  // --- Editor ---

  #renderEditor() {
    const box = $('cp-editor');
    const form = this.#form;
    if (!form) {
      box.replaceChildren(el('p', 'tm-empty', 'Tria una campanya de la llista per veure-la i editar-la, o crea\'n una de nova.'));
      return;
    }
    const changed = () => this.#updateActions();

    // Capçalera fixa (D-133): nom, estat (amb el color de l'estat) i, a la dreta, l'avís de canvis sense desar, «Desa»,
    // «Calendari…», el menú «Més» (duplica, descarta els canvis, esborra) i «Tanca». A sota, els problemes que no deixen desar.
    const head = el('div', 'rt-head cp-head');
    const nameLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-label mb-0', 'Nom'));
    nameLabel.htmlFor = 'cp-name';
    const name = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm rt-name'));
    name.id = 'cp-name';
    name.maxLength = 100;
    name.value = form.name;
    name.addEventListener('input', () => { form.name = name.value; changed(); });
    // L'estat: el desplegable té el fons del color de l'estat i, al davant, la seva icona (color + icona + text)
    const stateIcon = el('span', 'cp-state-icon');
    const stateSelect = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm cp-state-select'));
    stateSelect.setAttribute('aria-label', 'Estat de la campanya');
    for (const [value, st] of Object.entries(STATES)) stateSelect.append(option(value, st.text));
    // Es mostra l'estat que compta (D-136): una activa amb la data de fi passada surt com a «Acabada» (en àmbar), encara
    // que el desat continuï sent «Activa»; si s'allarga la data de fi, torna a sortir com a activa
    const showState = () => {
      const shown = effectiveState(form.state, form.endDate, todayIso());
      const st = STATES[/** @type {keyof typeof STATES} */ (shown)] ?? STATES.ESBORRANY;
      stateSelect.value = shown;
      stateSelect.dataset.state = shown;
      stateSelect.title = shown !== form.state
        ? 'Ha passat la data de fi: compta com a acabada i ja no sona. Per allargar-la, canvia la data de fi.'
        : st.help; // l'explicació, en passar-hi el ratolí
      stateIcon.className = `cp-state-icon ${st.css}`;
      stateIcon.replaceChildren(icon(st.icon));
    };
    showState();
    this.#syncState = showState;
    stateSelect.addEventListener('change', () => { form.state = stateSelect.value; showState(); changed(); });
    const stateWrap = el('span', 'cp-state');
    stateWrap.append(stateIcon, stateSelect);

    const unsaved = el('span', 'cp-unsaved text-warning d-none');
    unsaved.id = 'cp-unsaved';
    unsaved.append(icon('bi-exclamation-circle-fill'), ' Canvis sense desar');
    const flash = el('span', 'cp-flash form-text m-0');
    flash.id = 'cp-flash';
    flash.setAttribute('aria-live', 'polite');

    const save = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-primary'));
    save.id = 'cp-save';
    save.type = 'button';
    save.append(icon('bi-check-lg'), ' Desa');
    save.addEventListener('click', () => this.#save());

    // «Calendari…» (D-132): la graella per programar-la; només d'una campanya desada
    const campaign = this.#campaign;
    const calendar = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-primary'));
    calendar.type = 'button';
    calendar.append(icon('bi-calendar-week'), ' Calendari…');
    calendar.title = campaign
      ? 'La graella setmanal: marca a quines hores i quins dies ha de sonar, com amb una pista'
      : 'Desa la campanya per poder-la programar al calendari';
    calendar.disabled = !campaign;
    if (campaign) calendar.addEventListener('click', () => this.#openCalendar(campaign));

    const close = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    close.type = 'button';
    close.append(icon('bi-x-lg'), ' Tanca');
    close.addEventListener('click', () => this.#closeEditor());

    const buttons = el('div', 'cp-head-buttons');
    buttons.append(unsaved, flash, save, calendar, this.#moreMenu(), close);
    head.append(nameLabel, name, stateWrap, buttons);
    const problems = el('div', 'tm-problems text-warning cp-head-problems');
    problems.id = 'cp-problems';
    const header = el('div', 'cp-header');
    header.append(head, problems);

    const notes = [];
    if (form.state === 'ACTIVA' && effectiveState(form.state, form.endDate, todayIso()) === 'ACABADA')
      notes.push(statusLabel('text-secondary small', 'bi-info-circle', 'Ha passat la data de fi: compta com a acabada i ja no sona. Per allargar-la, canvia la data de fi.'));

    // «Dades» i «Falques» una al costat de l'altra si hi caben (D-132); a sota, el pla de passis (d'una campanya desada)
    const top = el('div', 'cp-top');
    top.append(this.#generalBox(form, changed), this.#spotsBox(form));
    box.replaceChildren(header, ...notes, top, ...(this.#campaign ? [this.#planBox()] : []));
    this.#updateActions();
  }

  /** Requadre «Dades»: anunciant, dates, repartiment, passis contractats i notes. @param {import('../campaign-form.js').FormCampaign} form @param {() => void} changed */
  #generalBox(form, changed) {
    const body = el('div', 'cp-grid');
    const field = (/** @type {string} */ label, /** @type {HTMLElement} */ control, /** @type {string} */ css = '', /** @type {string} */ help = '') => {
      const wrap = el('div', css);
      const id = `cp-f-${Math.random().toString(36).slice(2, 8)}`;
      control.id = id;
      const l = /** @type {HTMLLabelElement} */ (el('label', 'form-label', label));
      l.htmlFor = id;
      wrap.append(l, control);
      if (help) wrap.append(el('div', 'form-text', help));
      body.append(wrap);
      return wrap;
    };
    const input = (/** @type {string} */ type, /** @type {string} */ value, /** @type {(v: string) => void} */ onInput, attrs = {}) => {
      const i = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
      i.type = type;
      i.value = value;
      for (const [k, v] of Object.entries(attrs)) i.setAttribute(k, String(v));
      i.addEventListener('input', () => { onInput(i.value); changed(); });
      return i;
    };
    field('Anunciant', input('text', form.advertiser, (v) => { form.advertiser = v; }, { maxlength: 100 }), 'cp-wide');
    field('Comença', input('date', form.startDate, (v) => { form.startDate = v; }));
    field('Acaba', input('date', form.endDate, (v) => { form.endDate = v; }));
    const selection = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    for (const [value, text] of SELECTIONS) selection.append(option(value, text));
    selection.value = form.selection;
    const selectionWrap = field('Repartiment entre les falques', selection, 'cp-wide', SELECTIONS.find((s) => s[0] === form.selection)?.[2]);
    selection.addEventListener('change', () => {
      form.selection = selection.value;
      /** @type {HTMLElement} */ (selectionWrap.querySelector('.form-text')).textContent = SELECTIONS.find((s) => s[0] === form.selection)?.[2] ?? '';
      this.#renderEditor(); // la columna «Pes» només surt amb Pes
    });
    field('Passis contractats (total)', input('number', form.contractedTotal, (v) => { form.contractedTotal = v; }, { min: 0, step: 1 }), '', 'Opcional: per avisar si el pla en té més o menys.');
    field('Passis contractats per dia', input('number', form.contractedPerDay, (v) => { form.contractedPerDay = v; }, { min: 0, step: 1 }), '', 'Opcional.');
    const notes = /** @type {HTMLTextAreaElement} */ (el('textarea', 'form-control form-control-sm'));
    notes.rows = 2;
    notes.maxLength = 2000;
    notes.value = form.notes;
    notes.addEventListener('input', () => { form.notes = notes.value; changed(); });
    field('Notes', notes, 'cp-wide');
    return box('Dades', [form.advertiser, datesText(form.startDate, form.endDate)].filter(Boolean).join(' · '), body, true);
  }

  /** Requadre «Falques»: la taula (arrossegable) i «Afegeix falques…». @param {import('../campaign-form.js').FormCampaign} form */
  #spotsBox(form) {
    const body = el('div', '');
    const weighted = form.selection === 'PES';
    if (form.spots.length === 0) {
      body.append(el('p', 'form-text', 'Encara no hi ha cap falca. Afegeix-ne amb el botó de sota.'));
    } else {
      const table = el('table', 'table table-sm rt-steps cp-spots');
      const hr = el('tr', '');
      const headers = [['', 'Arrossega per canviar l\'ordre (compta per a «Seqüència»)'], ['#', ''], ['Falca', ''],
        ['Dates', 'Buides: les dates de la campanya'],
        ...(weighted ? [['Pes', `De 1 a ${MAX_WEIGHT}: una de pes 2 sona el doble que una de pes 1`]] : []), ['', '']];
      for (const [text, title] of headers) {
        const th = el('th', '', text);
        if (title) th.title = title;
        hr.append(th);
      }
      const thead = el('thead', '');
      thead.append(hr);
      const tbody = el('tbody', '');
      form.spots.forEach((spot, index) => tbody.append(this.#spotRow(form, spot, index, weighted)));
      table.append(thead, tbody);
      const wrap = el('div', 'rt-table-wrap');
      wrap.append(table);
      body.append(wrap);
    }
    const add = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary cp-add-spots'));
    add.type = 'button';
    add.append(icon('bi-plus-lg'), ' Afegeix falques…');
    add.addEventListener('click', () => this.#pickSpots());
    body.append(add);
    const count = form.spots.length;
    return box('Falques', count === 0 ? 'cap' : `${count} ${count === 1 ? 'falca' : 'falques'}`, body, true);
  }

  /**
   * Una fila de la taula de falques.
   * @param {import('../campaign-form.js').FormCampaign} form @param {import('../campaign-form.js').FormSpot} spot
   * @param {number} index @param {boolean} weighted
   */
  #spotRow(form, spot, index, weighted) {
    const tr = el('tr', '');
    const cell = (/** @type {Node[]} */ ...nodes) => {
      const td = el('td', '');
      td.append(...nodes);
      tr.append(td);
      return td;
    };
    const changed = () => this.#updateActions();

    // Arrossegar per canviar l'ordre (com els passos de les rotacions): només des de la nansa
    const handle = el('span', 'rt-handle');
    handle.title = 'Arrossega per canviar l\'ordre';
    handle.append(icon('bi-grip-vertical'));
    handle.addEventListener('mousedown', () => { tr.draggable = true; });
    tr.addEventListener('dragstart', (e) => {
      this.#dragFrom = index;
      tr.classList.add('is-dragging');
      e.dataTransfer?.setData('text/plain', String(index));
      if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
    });
    tr.addEventListener('dragend', () => {
      tr.draggable = false;
      this.#dragFrom = null;
      for (const row of tr.parentElement?.querySelectorAll('tr') ?? []) row.classList.remove('is-dragging', 'drop-before', 'drop-after');
    });
    tr.addEventListener('dragover', (e) => {
      if (this.#dragFrom == null) return;
      e.preventDefault();
      const after = this.#dragFrom < index;
      tr.classList.toggle('drop-after', after && this.#dragFrom !== index);
      tr.classList.toggle('drop-before', !after && this.#dragFrom !== index);
    });
    tr.addEventListener('dragleave', () => tr.classList.remove('drop-before', 'drop-after'));
    tr.addEventListener('drop', (e) => {
      e.preventDefault();
      const from = this.#dragFrom;
      this.#dragFrom = null;
      if (from == null || from === index) return;
      form.spots = moveSpot(form.spots, from, index);
      this.#renderEditor();
    });
    cell(handle);
    cell(el('span', 'rt-index', String(index + 1)));

    // La pista, amb el tipus i els avisos
    const t = spot.track;
    const what = el('div', 'cp-spot');
    const title = el('div', 'cp-spot-title');
    title.append(typeBadge(t?.type ?? null), ' ', el('span', '', t ? (t.artist ? `${t.artist} - ${t.title}` : t.title) : `Pista ${spot.trackId}`));
    what.append(title);
    if (t?.durationMs) what.append(el('div', 'cp-spot-detail', formatDuration(t.durationMs)));
    for (const warning of spotWarnings(spot, form)) what.append(statusLabel('text-warning small d-block', 'bi-exclamation-triangle-fill', warning));
    if (t?.path) what.title = t.path;
    cell(what);

    // Dates pròpies (buides: les de la campanya)
    const date = (/** @type {'startDate' | 'endDate'} */ key, /** @type {string} */ label) => {
      const i = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm cp-date'));
      i.type = 'date';
      i.value = spot[key];
      i.min = form.startDate;
      i.max = form.endDate;
      i.setAttribute('aria-label', `Falca ${index + 1}: ${label}`);
      i.title = 'Buit: les dates de la campanya';
      i.addEventListener('input', () => { spot[key] = i.value; changed(); });
      return i;
    };
    // Les dues dates en una sola columna, una sota l'altra (D-135), perquè la taula càpiga sense desplaçar-se de costat
    const dates = el('div', 'cp-spot-dates');
    const dateLine = (/** @type {string} */ label, /** @type {HTMLInputElement} */ input) => {
      const line = el('label', 'cp-spot-date');
      line.append(el('span', 'cp-spot-date-label', label), input);
      return line;
    };
    dates.append(dateLine('Des de', date('startDate', 'comença')), dateLine('Fins a', date('endDate', 'acaba')));
    cell(dates);

    // Dies de la setmana: set botons que s'encenen i s'apaguen (el text sempre hi és; encès, amb color i en negreta).
    // Van a sota del títol de la falca (D-134), perquè la taula no s'hagi de desplaçar de costat
    const days = el('div', 'cp-days');
    days.setAttribute('role', 'group');
    days.setAttribute('aria-label', `Falca ${index + 1}: dies de la setmana`);
    for (let d = 0; d < 7; d++) {
      const b = /** @type {HTMLButtonElement} */ (el('button', 'cp-day', DAY_SHORT[d]));
      b.type = 'button';
      b.title = DAY_NAMES[d];
      const sync = () => {
        const on = (spot.daysMask & (1 << d)) !== 0;
        b.classList.toggle('is-on', on);
        b.setAttribute('aria-pressed', String(on));
      };
      sync();
      b.addEventListener('click', () => {
        spot.daysMask ^= 1 << d;
        sync();
        changed();
      });
      days.append(b);
    }
    const daysRow = el('div', 'cp-spot-days');
    daysRow.append(el('span', 'cp-spot-days-label', 'Dies:'), days);
    what.append(daysRow);

    if (weighted) {
      const weight = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm cp-weight'));
      weight.type = 'number';
      weight.min = '1';
      weight.max = String(MAX_WEIGHT);
      weight.step = '1';
      weight.value = String(spot.weight);
      weight.setAttribute('aria-label', `Falca ${index + 1}: pes`);
      weight.addEventListener('input', () => { spot.weight = Number(weight.value); changed(); });
      cell(weight);
    }

    const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link p-0 text-body-secondary'));
    remove.type = 'button';
    remove.title = 'Treu la falca de la campanya (la pista es queda a la biblioteca)';
    remove.setAttribute('aria-label', `Treu la falca ${index + 1}`);
    remove.append(icon('bi-x-lg'));
    remove.addEventListener('click', () => {
      form.spots = form.spots.filter((s) => s !== spot);
      this.#renderEditor();
    });
    cell(remove);
    return tr;
  }

  /** Finestra per afegir falques: cerca a la biblioteca (per defecte, només les de tipus Falca); cada clic n'afegeix una. */
  async #pickSpots() {
    const form = this.#form;
    if (!form) return;
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    input.type = 'search';
    input.placeholder = 'Cerca per títol, anunciant o carpeta…';
    input.setAttribute('aria-label', 'Cerca una pista');
    const type = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm cp-pick-type'));
    type.setAttribute('aria-label', 'Tipus de pista');
    type.append(option('commercial', 'Només falques'), option('', 'Tots els tipus'));
    const results = el('div', 'ee-results');
    let token = 0;
    let timer = 0;

    const search = async () => {
      const mine = ++token;
      /** @type {Record<string, any>} */
      const params = { text: input.value, limit: 50 };
      if (type.value) params.type = type.value;
      try {
        const result = await this.#send('library.search', params);
        if (mine !== token) return;
        results.replaceChildren();
        if (result.tracks.length === 0) results.append(el('div', 'text-body-secondary', 'Cap pista amb aquests criteris.'));
        for (const track of result.tracks) {
          const row = /** @type {HTMLButtonElement} */ (el('button', `ee-result${track.fileState === 'missing' ? ' is-missing' : ''}`));
          row.type = 'button';
          const label = track.artist ? `${track.artist} - ${track.title}` : track.title;
          const already = () => form.spots.some((s) => s.trackId === track.id);
          const detail = el('span', 'ee-result-detail');
          const sync = () => {
            detail.textContent = [track.durationMs ? formatDuration(track.durationMs) : '', track.fileState === 'missing' ? 'el fitxer falta' : '',
              already() ? 'ja és a la campanya' : ''].filter(Boolean).join(' · ');
            row.disabled = already();
          };
          const labelLine = el('span', 'ee-result-label');
          labelLine.append(typeBadge(track.type), ' ', label);
          row.append(labelLine, detail);
          row.title = track.path;
          sync();
          row.addEventListener('click', () => {
            if (already()) return;
            form.spots = [...form.spots, newSpot(track)];
            sync();
          });
          results.append(row);
        }
        if (result.more) results.append(el('div', 'text-body-secondary', 'N\'hi ha més: escriu més text.'));
      } catch (err) {
        if (mine === token) results.replaceChildren(el('div', 'text-danger', `No s'ha pogut cercar: ${/** @type {Error} */ (err).message}`));
      }
    };
    input.addEventListener('input', () => {
      clearTimeout(timer);
      timer = window.setTimeout(search, 250);
    });
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') e.preventDefault(); });
    type.addEventListener('change', search);
    const filters = el('div', 'ee-search-filters');
    filters.append(type);
    search();
    await formDialog({
      title: 'Afegeix falques', icon: 'bi-megaphone', wide: true,
      message: 'Cada pista que cliquis s\'afegeix a la campanya. Quan acabis, tanca la finestra.',
      body: [input, filters, results],
    });
    this.#renderEditor();
  }

  /**
   * Requadre «Pla de passis» (de la campanya desada; D-132): el resum i, al costat, el calendari i la llegenda. Els
   * esdeveniments on sona ja no es llisten (sortiran a l'informe de programació); només s'avisa si no en té cap.
   */
  #planBox() {
    const c = this.#campaign;
    const body = el('div', 'cp-where');
    if (c.events.length === 0) {
      body.append(statusLabel('text-warning d-block', 'bi-exclamation-triangle-fill', 'Encara no està programada enlloc.'),
        el('div', 'form-text', 'Prem «Calendari…» (a dalt) per triar les hores i els dies on ha de sonar. També es pot afegir l\'acció «Campanya» a un esdeveniment des del Planificador.'));
    }
    const plan = el('div', 'cp-plan');
    body.append(plan);
    this.#loadPlan(plan, c);
    return box('Pla de passis', c.events.length === 0 ? 'no està programada' : '', body, true);
  }

  /** El resum del pla (campaigns.plan): passis per falca i si quadren amb els contractats. @param {HTMLElement} into @param {any} c */
  async #loadPlan(into, c) {
    into.replaceChildren(statusLabel('text-info small', 'bi-hourglass-split', 'Calculant el pla…'));
    try {
      const plan = await this.#send('campaigns.plan', { campaignId: c.id });
      if (this.#campaign?.id !== c.id) return;
      const parts = [planPanel(plan, c, todayIso(), { columns: true })];
      if (this.dirty) parts.push(statusLabel('text-warning small d-block', 'bi-info-circle', 'És el pla de la campanya desada: els canvis sense desar no hi compten.'));
      into.replaceChildren(...parts);
    } catch (err) {
      into.replaceChildren(statusLabel('text-danger small', 'bi-x-circle', `No s'ha pogut llegir el pla: ${/** @type {Error} */ (err).message}`));
    }
  }

  /** El menú «Més» de la capçalera (D-133): duplica, descarta els canvis i esborra. */
  #moreMenu() {
    const more = el('div', 'ee-more');
    const toggle = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    toggle.type = 'button';
    toggle.setAttribute('aria-haspopup', 'menu');
    toggle.setAttribute('aria-expanded', 'false');
    toggle.append(icon('bi-three-dots'), ' Més');
    const menu = el('div', 'ee-menu cp-menu d-none');
    menu.setAttribute('role', 'menu');
    const item = (/** @type {string} */ iconName, /** @type {string} */ text, /** @type {() => void} */ run, danger = false) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', `ee-menu-item${danger ? ' text-danger' : ''}`));
      b.type = 'button';
      b.setAttribute('role', 'menuitem');
      b.append(icon(iconName), ' ', el('span', '', text));
      b.addEventListener('click', () => {
        this.#closeMenu();
        run();
      });
      menu.append(b);
      return b;
    };
    if (this.#campaign) item('bi-copy', 'Duplica', () => this.#duplicate()).title = 'Una campanya nova (esborrany) amb les mateixes dades i falques';
    const discard = item('bi-arrow-counterclockwise', 'Descarta els canvis', () => {
      if (this.#campaign) this.#open(this.#campaign);
      else this.#close();
      this.#renderList();
      this.#renderEditor();
    });
    discard.id = 'cp-discard';
    if (this.#campaign) item('bi-trash', 'Esborra…', () => this.#delete(), true);
    toggle.addEventListener('click', () => {
      const open = menu.classList.contains('d-none');
      menu.classList.toggle('d-none', !open);
      toggle.setAttribute('aria-expanded', String(open));
      if (open) /** @type {HTMLElement | null} */ (menu.querySelector('.ee-menu-item:not(:disabled)'))?.focus();
    });
    more.append(toggle, menu);
    return more;
  }

  /** Un missatge breu a la capçalera (p. ex. «Desat»), que s'esborra sol. @param {string} text */
  #flash(text) {
    const flash = document.getElementById('cp-flash');
    if (!flash) return;
    flash.textContent = text;
    setTimeout(() => { if (flash.textContent === text) flash.textContent = ''; }, 5000);
  }

  /** Activa «Desa» si hi ha canvis i no hi ha problemes; mostra l'avís de canvis sense desar i els problemes. */
  #updateActions() {
    const form = this.#form;
    const save = /** @type {HTMLButtonElement | null} */ (document.getElementById('cp-save'));
    if (!form || !save) return;
    this.#syncState?.(); // la data de fi pot haver canviat l'estat que compta
    const problems = campaignProblems(form);
    const dirty = this.dirty;
    save.disabled = !dirty || problems.length > 0;
    /** @type {HTMLButtonElement} */ ($('cp-discard')).disabled = !dirty && !!this.#campaign;
    $('cp-unsaved').classList.toggle('d-none', !dirty);
    if (dirty) $('cp-flash').textContent = '';
    $('cp-problems').replaceChildren(...(dirty ? problems : []).map((p) => statusLabel('', 'bi-exclamation-triangle-fill', p)));
  }
}

/** L'estat d'una campanya amb color, icona i text. @param {string} state @param {string} [css] */
function stateLabel(state, css = '') {
  const s = STATES[/** @type {keyof typeof STATES} */ (state)] ?? STATES.ESBORRANY;
  return statusLabel(`${s.css} ${css}`, s.icon, s.text);
}

/**
 * Un requadre que es desplega, amb el títol i un resum (que només es veu replegat), com a l'editor d'esdeveniments.
 * @param {string} title @param {string} summary @param {HTMLElement} body @param {boolean} open
 */
function box(title, summary, body, open) {
  const details = /** @type {HTMLDetailsElement} */ (el('details', 'ee-box'));
  details.open = open;
  const s = el('summary', 'ee-box-title');
  s.append(el('span', 'ee-box-name', title), el('span', 'ee-box-summary', summary));
  const b = el('div', 'ee-box-body');
  b.append(body);
  details.append(s, b);
  return details;
}

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
