// @ts-check
// Pantalla «Rotacions» (fase 3, pas 8a; D-094): la llista de rotacions (amb la que fa servir l'AutoDJ marcada) i l'editor
// d'una rotació: el nom i els passos (d'on surt la pista, etiqueta, com es tria, criteris de repetició i trepitjador), que
// es poden afegir, moure i treure. Es desa al motor (rotations.save); si és l'activa, l'AutoDJ agafa els passos nous.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { askText } from './ask-text.js';
import { SELECTIONS, newStep, stepFromApi, stepToApi, rotationChanged, rotationProblems, moveTo } from '../rotation-form.js';

export class RotationsView {
  #send;
  /** @type {any[]} */
  #rotations = [];
  /** @type {any[]} */
  #categories = [];
  /** @type {any[]} */
  #tags = [];
  /** La rotació activa de l'AutoDJ. @type {number | null} */
  #activeId = null;
  #visible = false;
  #loaded = false;
  /** La rotació oberta (null = una de nova sense desar). @type {any | null} */
  #rotation = null;
  #editing = false;
  #name = '';
  /** @type {import('../rotation-form.js').FormStep[]} */
  #steps = [];
  /** El pas que s'està arrossegant (D-117), o null. @type {number | null} */
  #dragFrom = null;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    $('rt-new').addEventListener('click', () => this.#new());
    $('rt-duplicate').addEventListener('click', () => this.#duplicate());
    $('rt-delete').addEventListener('click', () => this.#delete());
    $('rt-bag-new').addEventListener('click', () => this.#newBag());
    for (const id of ['rt-sep-track', 'rt-sep-artist']) $(id).addEventListener('input', () => this.#separationEdited());
    $('rt-sep-save').addEventListener('click', () => this.#saveSeparation());
    $('rt-reserve-rotation').addEventListener('change', () => this.#setReserveRotation());
    $('rt-reserve-add').addEventListener('change', () => this.#addExcluded());
    this.#renderEditor();
  }

  // --- Reserva (fase 3, pas 8d; D-099) ---

  /** La desada al motor (reserve.get). @type {{ rotationId: number | null, rotationName: string | null, excludedSubcategories: any[] } | null} */
  #reserve = null;

  #renderReserve() {
    const reserve = this.#reserve;
    if (!reserve) return;

    // Rotació de reserva: totes menys l'activa (si la rotació activa no troba res, tornar-hi no serviria de res)
    const rotation = /** @type {HTMLSelectElement} */ ($('rt-reserve-rotation'));
    rotation.replaceChildren(option('', 'Cap (directament tota la biblioteca)'),
      ...this.#rotations.map((r) => option(String(r.id), r.id === this.#activeId ? `${r.name} (l'activa: no farà res)` : r.name)));
    rotation.value = reserve.rotationId == null ? '' : String(reserve.rotationId);

    // Les «no de reserva», amb una creu per treure-les
    const list = $('rt-reserve-excluded');
    if (reserve.excludedSubcategories.length === 0) {
      list.replaceChildren(el('li', 'form-text border-0 p-0', 'Cap: pot sonar qualsevol música'));
    } else {
      list.replaceChildren(...reserve.excludedSubcategories.map((s) => {
        const li = el('li', '');
        const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link p-0 text-body-secondary'));
        remove.type = 'button';
        remove.title = 'Treu-la: podrà sonar a la reserva';
        remove.setAttribute('aria-label', `Treu ${s.categoryName} / ${s.name} de les «no de reserva»`);
        remove.append(icon('bi-x-lg'));
        remove.addEventListener('click', () => this.#saveExcluded(reserve.excludedSubcategories.filter((x) => x.id !== s.id).map((x) => x.id)));
        li.append(icon('bi-slash-circle'), el('span', '', `${s.categoryName} / ${s.name}`), remove);
        li.title = 'No sona mai a la reserva de tota la biblioteca';
        return li;
      }));
    }

    // Desplegable per afegir-ne una (les que ja hi són, no)
    const add = /** @type {HTMLSelectElement} */ ($('rt-reserve-add'));
    const excluded = new Set(reserve.excludedSubcategories.map((s) => s.id));
    add.replaceChildren(option('', 'Afegeix una subcategoria «no de reserva»…'));
    for (const c of this.#categories) {
      const subs = c.subcategories.filter((/** @type {any} */ s) => !excluded.has(s.id));
      if (subs.length === 0) continue;
      const group = document.createElement('optgroup');
      group.label = c.name;
      group.append(...subs.map((/** @type {any} */ s) => option(String(s.id), s.name)));
      add.append(group);
    }
    add.value = '';
  }

  /** @param {object} params */
  async #saveReserve(params) {
    try {
      this.#reserve = await this.#send('reserve.set', params);
      $('rt-reserve-state').textContent = 'Desat';
    } catch (err) {
      $('rt-reserve-state').textContent = /** @type {Error} */ (err).message;
    }
    this.#renderReserve();
  }

  #setReserveRotation() {
    const value = /** @type {HTMLSelectElement} */ ($('rt-reserve-rotation')).value;
    this.#saveReserve({ rotationId: value === '' ? null : Number(value) });
  }

  #addExcluded() {
    const value = /** @type {HTMLSelectElement} */ ($('rt-reserve-add')).value;
    if (value === '' || !this.#reserve) return;
    this.#saveExcluded([...this.#reserve.excludedSubcategories.map((s) => s.id), Number(value)]);
  }

  /** @param {number[]} ids */
  #saveExcluded(ids) {
    this.#saveReserve({ excludedSubcategoryIds: ids });
  }

  // --- Separació real (fase 3, pas 8b; D-095) ---

  /** La desada al motor. @type {{ trackMinutes: number, artistMinutes: number } | null} */
  #separation = null;

  /** @param {{ trackMinutes: number, artistMinutes: number }} s */
  #showSeparation(s) {
    this.#separation = s;
    /** @type {HTMLInputElement} */ ($('rt-sep-track')).value = String(s.trackMinutes);
    /** @type {HTMLInputElement} */ ($('rt-sep-artist')).value = String(s.artistMinutes);
    this.#separationEdited();
  }

  #separationValues() {
    const read = (/** @type {string} */ id) => Number(/** @type {HTMLInputElement} */ ($(id)).value);
    return { trackMinutes: read('rt-sep-track'), artistMinutes: read('rt-sep-artist') };
  }

  #separationEdited() {
    const v = this.#separationValues();
    const valid = [v.trackMinutes, v.artistMinutes].every((m) => Number.isInteger(m) && m >= 0 && m <= 10080);
    const changed = !!this.#separation && (v.trackMinutes !== this.#separation.trackMinutes || v.artistMinutes !== this.#separation.artistMinutes);
    /** @type {HTMLButtonElement} */ ($('rt-sep-save')).disabled = !valid || !changed;
    $('rt-sep-state').textContent = !valid ? 'Minuts enters, de 0 a 10.080' : changed ? 'Canvis sense desar' : '';
  }

  async #saveSeparation() {
    try {
      this.#showSeparation(await this.#send('separation.set', this.#separationValues()));
      $('rt-sep-state').textContent = 'Desat: val per a les cançons que es triïn a partir d\'ara';
    } catch (err) {
      $('rt-sep-state').textContent = /** @type {Error} */ (err).message;
    }
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
    return this.#editing && rotationChanged(this.#rotation, this.#name, this.#steps);
  }

  /** En connectar-se (si es mostra): les rotacions, les categories i etiquetes, i quina és l'activa. */
  async refresh() {
    if (!this.#visible) {
      this.#loaded = false;
      return;
    }
    this.#loaded = true;
    try {
      const [rotations, filters, autodj, separation, bags, reserve] = await Promise.all([
        this.#send('rotations.list'), this.#send('library.filters'), this.#send('autodj.get'), this.#send('separation.get'),
        this.#send('bags.list'), this.#send('reserve.get'),
      ]);
      this.#bags = bags.bags;
      this.#reserve = reserve;
      this.#rotations = rotations.rotations;
      this.#categories = filters.categories;
      this.#tags = filters.tags;
      this.#activeId = autodj.autodj.rotation?.id ?? null;
      this.#showSeparation(separation);
    } catch {
      return; // sense connexió
    }
    // La rotació oberta, si no s'hi està escrivint, amb la versió del motor
    if (this.#rotation && !this.dirty) {
      const fresh = this.#rotations.find((r) => r.id === this.#rotation.id);
      if (fresh) this.#open(fresh);
      else this.#close();
    }
    this.#renderList();
    this.#renderBags();
    this.#renderReserve();
    this.#renderEditor();
  }

  // --- Bosses amb nom (fase 3, pas 8c; D-096) ---

  /** @type {any[]} */
  #bags = [];

  /** Desplegable de subcategories (agrupades per categoria), amb «(cap)». @param {number | null} selected */
  #subcategorySelect(selected) {
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    select.append(option('', '(cap)'));
    for (const c of this.#categories) {
      if (c.subcategories.length === 0) continue;
      const group = document.createElement('optgroup');
      group.label = c.name;
      group.append(...c.subcategories.map((/** @type {any} */ s) => option(String(s.id), `${s.name} (${s.count})`)));
      select.append(group);
    }
    select.value = selected == null ? '' : String(selected);
    return select;
  }

  #renderBags() {
    const list = $('rt-bags');
    if (this.#bags.length === 0) {
      list.replaceChildren(el('li', 'form-text', 'Cap. Crea\'n una per alternar subcategories sense tocar les rotacions.'));
      return;
    }
    list.replaceChildren(...this.#bags.map((bag) => {
      const li = el('li', 'rt-bag');
      const head = el('div', 'rt-bag-head');
      const name = el('span', 'rt-bag-name', bag.name);
      name.title = `${bag.steps} ${bag.steps === 1 ? 'pas hi apunta' : 'passos hi apunten'}` +
        (bag.changedAt ? `\nDes de ${new Date(bag.changedAt).toLocaleString('ca-ES', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}${bag.changedBy ? `, ${bag.changedBy}` : ''}` : '');
      const tools = el('span', 'rt-tools');
      const tool = (/** @type {string} */ iconName, /** @type {string} */ title, /** @type {() => void} */ run) => {
        const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link p-0 text-body-secondary'));
        b.type = 'button';
        b.title = title;
        b.setAttribute('aria-label', `${title}: ${bag.name}`);
        b.append(icon(iconName));
        b.addEventListener('click', run);
        tools.append(b);
      };
      tool('bi-pencil', 'Canvia el nom', () => this.#renameBag(bag));
      tool('bi-trash', 'Esborra la bossa', () => this.#deleteBag(bag));
      head.append(name, tools);
      const select = this.#subcategorySelect(bag.subcategoryId);
      select.setAttribute('aria-label', `Subcategoria de la bossa ${bag.name}`);
      select.addEventListener('change', () => this.#setBag(bag, select));
      li.append(head, select);
      if (bag.subcategoryId == null)
        li.append(statusLabel('text-warning small', 'bi-exclamation-triangle-fill', 'Sense subcategoria: els seus passos se salten'));
      return li;
    }));
  }

  /** @param {any} bag @param {HTMLSelectElement} select */
  async #setBag(bag, select) {
    const subcategoryId = select.value === '' ? null : Number(select.value);
    try {
      await this.#send('bags.set', { bagId: bag.id, subcategoryId });
      // La resta arriba amb bags.changed / rotations.changed
    } catch (err) {
      select.value = bag.subcategoryId == null ? '' : String(bag.subcategoryId);
      await confirmAction({ title: 'No s\'ha pogut canviar la bossa', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  async #newBag() {
    const name = await askText({ title: 'Nova bossa', label: 'Nom de la bossa (p. ex. «Generalista A»)', confirm: 'Crea-la' });
    if (!name) return;
    try {
      await this.#send('bags.save', { name, subcategoryId: null });
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut crear la bossa', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** @param {any} bag */
  async #renameBag(bag) {
    const name = await askText({ title: 'Canviar el nom de la bossa', label: 'Nom nou', confirm: 'Canvia el nom', value: bag.name });
    if (!name || name === bag.name) return;
    try {
      await this.#send('bags.save', { bagId: bag.id, name });
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut canviar el nom', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** @param {any} bag */
  async #deleteBag(bag) {
    if (!(await confirmAction({ title: 'Esborrar la bossa', message: `S'esborrarà la bossa «${bag.name}».`, confirm: 'Esborra-la' }))) return;
    try {
      await this.#send('bags.delete', { bagId: bag.id });
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut esborrar la bossa', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** L'AutoDJ ha canviat de rotació (autodj.changed). @param {number | null} id */
  setActive(id) {
    if (id === this.#activeId) return;
    this.#activeId = id;
    if (this.#loaded) {
      this.#renderList();
      this.#renderReserve(); // marca quina és l'activa
    }
  }

  // --- Llista ---

  #renderList() {
    $('rt-list').replaceChildren(...this.#rotations.map((r) => {
      const li = el('li', '');
      const b = el('button', 'rt-item');
      b.type = 'button';
      b.classList.toggle('active', this.#rotation?.id === r.id);
      const name = el('span', 'rt-item-name', r.name);
      const meta = el('span', 'rt-item-meta', `${r.steps.length} ${r.steps.length === 1 ? 'pas' : 'passos'}`);
      b.append(name, meta);
      if (r.id === this.#activeId) b.append(statusLabel('text-success rt-item-active', 'bi-play-circle-fill', 'Activa'));
      b.addEventListener('click', () => this.#select(r));
      li.append(b);
      return li;
    }));
  }

  /** @param {any} rotation */
  async #select(rotation) {
    if (this.#rotation?.id === rotation.id && this.#editing) return;
    if (!(await this.#leave())) return;
    this.#open(rotation);
    this.#renderList();
    this.#renderEditor();
  }

  /** Si hi ha canvis sense desar, pregunta si es poden perdre. */
  async #leave() {
    if (!this.dirty) return true;
    return confirmAction({
      title: 'Canvis sense desar',
      message: 'La rotació oberta té canvis que no s\'han desat. Si continues, es perdran.',
      confirm: 'Descarta els canvis',
    });
  }

  /** @param {any | null} rotation */
  #open(rotation) {
    this.#rotation = rotation;
    this.#editing = true;
    this.#name = rotation?.name ?? '';
    this.#steps = rotation ? rotation.steps.map(stepFromApi) : [newStep()];
  }

  #close() {
    this.#rotation = null;
    this.#editing = false;
    this.#name = '';
    this.#steps = [];
  }

  async #new() {
    if (!(await this.#leave())) return;
    this.#open(null);
    this.#name = '';
    this.#renderList();
    this.#renderEditor();
    /** @type {HTMLInputElement | null} */ (document.getElementById('rt-name'))?.focus();
  }

  async #duplicate() {
    if (!this.#rotation) {
      await confirmAction({ title: 'Duplicar', message: 'Tria primer la rotació que vols duplicar.', confirm: 'D\'acord', info: true });
      return;
    }
    if (!(await this.#leave())) return;
    const original = this.#rotation;
    const name = await askText({ title: `Duplicar «${original.name}»`, label: 'Nom de la còpia', confirm: 'Duplica', value: `${original.name} (còpia)` });
    if (!name) return;
    try {
      const { rotation } = await this.#send('rotations.save', { name, steps: original.steps.map(stepFromApi).map(stepToApi) });
      this.#open(rotation);
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut duplicar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  async #delete() {
    const r = this.#rotation;
    if (!r) {
      await confirmAction({ title: 'Esborrar', message: 'Tria primer la rotació que vols esborrar.', confirm: 'D\'acord', info: true });
      return;
    }
    if (!(await confirmAction({ title: 'Esborrar la rotació', message: `S'esborrarà la rotació «${r.name}». No es pot desfer.`, confirm: 'Esborra-la' }))) return;
    try {
      await this.#send('rotations.delete', { rotationId: r.id });
      this.#close();
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut esborrar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  // --- Editor ---

  #renderEditor() {
    const box = $('rt-editor');
    if (!this.#editing) {
      box.replaceChildren(el('p', 'tm-empty', 'Tria una rotació de la llista per veure-la i editar-la, o crea\'n una de nova.'));
      return;
    }
    const head = el('div', 'rt-head');
    const label = /** @type {HTMLLabelElement} */ (el('label', 'form-label mb-0', 'Nom'));
    label.htmlFor = 'rt-name';
    const name = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm rt-name'));
    name.id = 'rt-name';
    name.maxLength = 100;
    name.value = this.#name;
    name.addEventListener('input', () => {
      this.#name = name.value;
      this.#updateActions();
    });
    head.append(label, name);
    if (this.#rotation?.id === this.#activeId && this.#rotation)
      head.append(statusLabel('text-success', 'bi-play-circle-fill', 'És la rotació activa: els canvis s\'apliquen en desar-los'));

    const table = el('table', 'table table-sm rt-steps');
    const thead = el('thead', '');
    const hr = el('tr', '');
    for (const [text, title] of [['', 'Arrossega per moure el pas'], ['#', ''], ['D\'on surt la pista', ''], ['Etiqueta', 'Només les pistes amb aquesta etiqueta'],
      ['Com es tria', ''], ['No repetir', 'Criteris de repetició: no repetir la pista ni l\'artista (música)'],
      ['Trepitjador', 'Sweeper a sobre de l\'inici de la pista (de la categoria de sweepers activa)'], ['', '']]) {
      const th = el('th', '', text);
      if (title) th.title = title;
      hr.append(th);
    }
    thead.append(hr);
    const tbody = el('tbody', '');
    this.#steps.forEach((step, index) => tbody.append(this.#stepRow(step, index)));
    table.append(thead, tbody);

    const add = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    add.type = 'button';
    add.append(icon('bi-plus-lg'), ' Afegeix un pas');
    add.addEventListener('click', () => {
      // El pas nou, una còpia de l'últim (és el que sol voler-se: després només cal canviar la subcategoria)
      const last = this.#steps[this.#steps.length - 1];
      this.#steps.push(last ? { ...last } : newStep());
      this.#renderEditor();
    });

    const actions = el('div', 'tm-actions');
    const save = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-primary'));
    save.id = 'rt-save';
    save.type = 'button';
    save.append(icon('bi-check-lg'), ' Desa');
    save.addEventListener('click', () => this.#save());
    const discard = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    discard.id = 'rt-discard';
    discard.type = 'button';
    discard.textContent = 'Descarta els canvis';
    discard.addEventListener('click', () => {
      if (this.#rotation) this.#open(this.#rotation);
      else this.#close();
      this.#renderList();
      this.#renderEditor();
    });
    const state = el('span', 'tm-dirty');
    state.id = 'rt-state';
    actions.append(save, discard, state);
    const problems = el('div', 'tm-problems text-warning');
    problems.id = 'rt-problems';

    // Previsualització (fase 3, pas 8b): només de rotacions desades
    const preview = el('div', 'rt-preview');
    if (this.#rotation) {
      const button = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-primary'));
      button.type = 'button';
      button.append(icon('bi-eye'), ' Previsualitza les properes 20 cançons');
      button.title = 'Què triaria l\'AutoDJ amb aquesta rotació, ara mateix (sense canviar res)';
      const list = el('div', 'rt-preview-list');
      button.addEventListener('click', () => this.#preview(list));
      preview.append(el('h3', 'h6 mt-3', 'Previsualització'), button, list);
    }

    box.replaceChildren(head, el('div', 'rt-table-wrap'), add, actions, problems, preview);
    /** @type {HTMLElement} */ (box.querySelector('.rt-table-wrap')).append(table);
    this.#updateActions();
  }

  /**
   * Les properes cançons que triaria la rotació desada (rotations.preview), amb d'on surt cadascuna, quan ha sonat i si
   * s'ha hagut d'afluixar alguna regla. @param {HTMLElement} box
   */
  async #preview(box) {
    const rotation = this.#rotation;
    if (!rotation) return;
    box.replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Calculant…'));
    try {
      const p = await this.#send('rotations.preview', { rotationId: rotation.id, count: 20 });
      const notes = [];
      if (this.dirty) notes.push(statusLabel('text-warning', 'bi-exclamation-triangle-fill', 'És la de la rotació desada: els canvis sense desar no hi compten.'));
      notes.push(el('div', 'form-text', `${rotation.id === this.#activeId ? `És la rotació activa: comença pel pas ${p.fromStep + 1}, després de les cançons que ja són a la cua.` : 'Comença pel pas 1.'} ` +
        `Separació: la mateixa cançó cada ${p.separation.trackMinutes} min; el mateix artista, cada ${p.separation.artistMinutes} min. Amb «a l'atzar», és una de les possibilitats.`));
      const table = el('table', 'table table-sm rt-preview-table');
      const tbody = el('tbody', '');
      const now = new Date();
      for (const [i, item] of p.items.entries()) {
        const tr = el('tr', '');
        const td = (/** @type {string | Node} */ content, /** @type {string} */ css = '') => {
          const cell = el('td', css);
          cell.append(content);
          tr.append(cell);
        };
        td(String(i + 1), 'rt-index');
        // De la reserva (D-099): d'on surt, al passar-hi el ratolí («Reserva: rotació «…», pas 2 (…)» o «tota la biblioteca»)
        const reserveLabel = statusLabel('text-warning', 'bi-life-preserver', 'Reserva');
        if (item.step) reserveLabel.title = item.step;
        td(item.fallback ? reserveLabel : `Pas ${item.stepIndex + 1}`, 'rt-preview-step');
        const t = item.track;
        td(t ? `${t.artist ? `${t.artist} - ` : ''}${t.title}` : '(cap pista)');
        td(t ? playedText(t.lastPlayed, now) : '', 'rt-preview-played');
        const flags = el('span', '');
        if (item.relaxed) flags.append(statusLabel('text-warning', 'bi-exclamation-circle', `S'ha afluixat: ${item.relaxed}`));
        if (item.problem) flags.title = item.problem;
        if (item.problem && !item.fallback) flags.append(' ', statusLabel('text-body-secondary', 'bi-skip-forward', 'Pas saltat'));
        td(flags);
        tbody.append(tr);
      }
      table.append(tbody);
      box.replaceChildren(...notes, table);
    } catch (err) {
      box.replaceChildren(statusLabel('text-danger', 'bi-x-octagon-fill', /** @type {Error} */ (err).message));
    }
  }

  /** @param {import('../rotation-form.js').FormStep} step @param {number} index */
  #stepRow(step, index) {
    const tr = el('tr', '');
    const cell = (/** @type {Node} */ node) => {
      const td = el('td', '');
      td.append(node);
      tr.append(td);
    };
    const changed = () => this.#updateActions();

    // Arrossegar per moure el pas (D-117): només des de la nansa, perquè els desplegables de la fila funcionin normalment.
    // Amb el teclat, els botons ↑ ↓ de la dreta.
    const handle = el('span', 'rt-handle');
    handle.title = 'Arrossega per moure el pas';
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
      this.#steps = moveTo(this.#steps, from, index);
      this.#renderEditor();
    });
    cell(handle);
    cell(el('span', 'rt-index', String(index + 1)));

    const source = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    source.setAttribute('aria-label', `Pas ${index + 1}: d'on surt la pista`);
    source.append(option('', 'Tria…'));
    if (this.#bags.length > 0) {
      const bags = document.createElement('optgroup');
      bags.label = 'Bosses';
      bags.append(...this.#bags.map((b) => option(`b:${b.id}`, `Bossa «${b.name}» (ara: ${b.subcategory ?? 'cap'})`)));
      source.append(bags);
    }
    for (const c of this.#categories) {
      const group = document.createElement('optgroup');
      group.label = c.name;
      group.append(option(`c:${c.id}`, `${c.name}: qualsevol (${c.count})`),
        ...c.subcategories.map((/** @type {any} */ s) => option(`s:${s.id}`, `${s.name} (${s.count})`)));
      source.append(group);
    }
    source.value = step.source;
    source.addEventListener('change', () => { step.source = source.value; changed(); });
    cell(source);

    const tag = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    tag.setAttribute('aria-label', `Pas ${index + 1}: etiqueta`);
    tag.append(option('', '—'), ...this.#tags.map((t) => option(String(t.id), t.name)));
    tag.value = step.tagId == null ? '' : String(step.tagId);
    tag.addEventListener('change', () => { step.tagId = tag.value === '' ? null : Number(tag.value); changed(); });
    cell(tag);

    const selection = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    selection.setAttribute('aria-label', `Pas ${index + 1}: com es tria`);
    selection.append(...SELECTIONS.map(([v, t]) => option(v, t)));
    selection.value = step.selection;
    selection.addEventListener('change', () => { step.selection = selection.value; changed(); });
    cell(selection);

    for (const field of /** @type {const} */ (['repeatRule', 'sweeper'])) {
      const box = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
      box.type = 'checkbox';
      box.checked = step[field];
      box.setAttribute('aria-label', `Pas ${index + 1}: ${field === 'repeatRule' ? 'no repetir' : 'trepitjador'}`);
      box.addEventListener('change', () => { step[field] = box.checked; changed(); });
      cell(box);
    }

    const tools = el('span', 'rt-tools');
    const tool = (/** @type {string} */ iconName, /** @type {string} */ title, /** @type {boolean} */ disabled, /** @type {() => void} */ run) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      b.type = 'button';
      b.title = title;
      b.setAttribute('aria-label', `${title} (pas ${index + 1})`);
      b.disabled = disabled;
      b.append(icon(iconName));
      b.addEventListener('click', () => { run(); this.#renderEditor(); });
      tools.append(b);
    };
    tool('bi-arrow-up', 'Puja', index === 0, () => { [this.#steps[index - 1], this.#steps[index]] = [this.#steps[index], this.#steps[index - 1]]; });
    tool('bi-arrow-down', 'Baixa', index === this.#steps.length - 1, () => { [this.#steps[index + 1], this.#steps[index]] = [this.#steps[index], this.#steps[index + 1]]; });
    tool('bi-x-lg', 'Treu el pas', false, () => { this.#steps.splice(index, 1); });
    cell(tools);
    return tr;
  }

  #updateActions() {
    const save = /** @type {HTMLButtonElement | null} */ (document.getElementById('rt-save'));
    if (!save) return;
    const changed = rotationChanged(this.#rotation, this.#name, this.#steps);
    const problems = rotationProblems(this.#name, this.#steps);
    save.disabled = !changed || problems.length > 0;
    /** @type {HTMLButtonElement} */ ($('rt-discard')).disabled = !changed;
    $('rt-state').replaceChildren(changed ? statusLabel('text-warning', 'bi-pencil-fill', this.#rotation ? 'Canvis sense desar' : 'Rotació nova, sense desar') : '');
    $('rt-problems').replaceChildren(...problems.map((p) => statusLabel('text-warning', 'bi-exclamation-triangle-fill', p)));
  }

  async #save() {
    if (rotationProblems(this.#name, this.#steps).length > 0) return;
    try {
      const params = { name: this.#name.trim(), steps: this.#steps.map(stepToApi), ...(this.#rotation ? { rotationId: this.#rotation.id } : {}) };
      const { rotation } = await this.#send('rotations.save', params);
      this.#open(rotation);
      await this.refresh();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut desar la rotació', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}

/** «Ha sonat fa 3 h» / «fa 2 dies» / «mai». @param {string | null} iso @param {Date} now */
function playedText(iso, now) {
  if (!iso) return 'Mai no ha sonat';
  const minutes = Math.round((now.getTime() - new Date(iso).getTime()) / 60000);
  if (minutes < 60) return `Ha sonat fa ${Math.max(0, minutes)} min`;
  if (minutes < 48 * 60) return `Ha sonat fa ${Math.round(minutes / 60)} h`;
  return `Ha sonat fa ${Math.round(minutes / 1440)} dies`;
}

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
