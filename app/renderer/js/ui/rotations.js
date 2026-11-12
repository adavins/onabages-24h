// @ts-check
// Pantalla «Rotacions» (fase 3, pas 8a; D-094): la llista de rotacions (amb la que fa servir l'AutoDJ marcada) i l'editor
// d'una rotació: el nom i els passos (d'on surt la pista, etiqueta, com es tria, criteris de repetició i trepitjador), que
// es poden afegir, moure i treure. Es desa al motor (rotations.save); si és l'activa, l'AutoDJ agafa els passos nous.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { askText } from './ask-text.js';
import { SELECTIONS, newStep, stepFromApi, stepToApi, rotationChanged, rotationProblems } from '../rotation-form.js';

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

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    $('rt-new').addEventListener('click', () => this.#new());
    $('rt-duplicate').addEventListener('click', () => this.#duplicate());
    $('rt-delete').addEventListener('click', () => this.#delete());
    this.#renderEditor();
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
      const [rotations, filters, autodj] = await Promise.all([
        this.#send('rotations.list'), this.#send('library.filters'), this.#send('autodj.get'),
      ]);
      this.#rotations = rotations.rotations;
      this.#categories = filters.categories;
      this.#tags = filters.tags;
      this.#activeId = autodj.autodj.rotation?.id ?? null;
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
    this.#renderEditor();
  }

  /** L'AutoDJ ha canviat de rotació (autodj.changed). @param {number | null} id */
  setActive(id) {
    if (id === this.#activeId) return;
    this.#activeId = id;
    if (this.#loaded) this.#renderList();
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
    for (const [text, title] of [['#', ''], ['D\'on surt la pista', ''], ['Etiqueta', 'Només les pistes amb aquesta etiqueta'],
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

    box.replaceChildren(head, el('div', 'rt-table-wrap'), add, actions, problems);
    /** @type {HTMLElement} */ (box.querySelector('.rt-table-wrap')).append(table);
    this.#updateActions();
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
    cell(el('span', 'rt-index', String(index + 1)));

    const source = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    source.setAttribute('aria-label', `Pas ${index + 1}: d'on surt la pista`);
    source.append(option('', 'Tria…'));
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

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
