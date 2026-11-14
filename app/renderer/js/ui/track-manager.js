// @ts-check
// Pestanya «Pistes» (abans «Biblioteca»): el gestor de pistes (fase 3, pas 7a; D-087; redisseny, D-111). A l'esquerra,
// les eines en requadres (importació, categories i etiquetes, utilitats); al mig, la barra de filtres, les accions en
// bloc i la taula de pistes (carregant-ne 100 cada vegada, ordenable per columnes, amb el total); a la dreta, la fitxa.
// Tot es desa al motor (library.update…); la interfície no toca mai la base de dades ni els fitxers.

import { $, el, icon, statusLabel } from './dom.js';
import { typeBadge } from './type-badge.js';
import { confirmAction } from './confirm.js';
import { askText } from './ask-text.js';
import { formDialog, dialogField } from './form-dialog.js';
import { formatDuration, typeName, playedAgo } from '../format.js';
import { TRACK_TYPES, trackChanges, trackProblems } from '../track-form.js';
import { CueEditor } from './cue-editor.js';
import {
  defaultFilters, filtersToParams, filterChips, withoutFilter, isFiltered, sortBy, trackStates, dateCell, totalText,
  STATE_OPTIONS, VALIDITY_OPTIONS,
} from '../tracks-filter.js';
import { todayIso } from '../event-form.js';

const PAGE = 100;
const SEARCH_DELAY_MS = 250;

export class TrackManagerView {
  #send;
  /** @type {(defaultPath?: string) => Promise<string | null>} */
  #chooseFile;
  /** @type {any} */
  #filters = null;
  /** Els filtres de la llista (D-111). @type {import('../tracks-filter.js').TrackFilters} */
  #f = defaultFilters();
  /** La categoria i la subcategoria triades al requadre «Categories» (sobre les quals actuen els seus botons). */
  /** @type {number | null} */
  #boxCategory = null;
  /** @type {number | null} */
  #boxSubcategory = null;
  /** Les pistes seleccionades amb la casella (ids), per a les accions en bloc. @type {Set<number>} */
  #selected = new Set();
  /** Quantes pistes compleixen els filtres (l'última cerca). */
  #total = 0;
  #offset = 0;
  #more = false;
  #loading = false;
  #token = 0;
  #timer = 0;
  #visible = false;
  #loaded = false;
  /** La fitxa oberta, tal com la diu el motor. @type {any} */
  #track = null;
  /** Llegeix el formulari de la fitxa oberta. @type {(() => import('../track-form.js').TrackFormValues) | null} */
  #readForm = null;

  /** Carpetes vigilades (l'estat de la subcategoria triada i el diàleg de la carpeta). @type {import('./folders.js').FoldersView} */
  #folders;
  /** Editor de punts (fase 3, pas 7e). @type {CueEditor} */
  #cueEditor;
  /** Obre la pestanya Calendari amb una pista (D-111). @type {(track: { id: number, title: string }) => void} */
  #openCalendar;
  /** Obre a l'Explorador la carpeta d'un fitxer (D-112). @type {(path: string) => Promise<'file' | 'folder' | 'missing'>} */
  #showFile;
  /** Obre l'informe de programació o el certificat d'una pista (D-138). @type {(kind: 'programming' | 'certificate', track: any) => void} */
  #openReport;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(defaultPath?: string) => Promise<string | null>} chooseFile  diàleg de Windows per triar un fitxer d'àudio
   * @param {import('./folders.js').FoldersView} folders
   * @param {(track: { id: number, title: string }) => void} openCalendar  obre el calendari de la pista (pestanya Calendari)
   * @param {(path: string) => Promise<'file' | 'folder' | 'missing'>} showFile  obre a l'Explorador la carpeta d'un fitxer
   * @param {(kind: 'programming' | 'certificate', track: any) => void} openReport  obre un informe de la pista
   */
  constructor(send, chooseFile, folders, openCalendar, showFile, openReport) {
    this.#openReport = openReport;
    this.#send = send;
    this.#chooseFile = chooseFile;
    this.#folders = folders;
    this.#openCalendar = openCalendar;
    this.#showFile = showFile;
    this.#cueEditor = new CueEditor(send);
    // Una carpeta vigilada nova o canviada: el requadre i els desplegables de subcategories (la marca, D-119)
    folders.onChange(() => {
      this.#renderBox();
      this.#syncFilterControls();
    });

    // Barra de filtres
    $('tm-search').addEventListener('input', () => {
      clearTimeout(this.#timer);
      this.#timer = window.setTimeout(() => this.#setFilters({ ...this.#f, text: /** @type {HTMLInputElement} */ ($('tm-search')).value }), SEARCH_DELAY_MS);
    });
    $('tm-category').addEventListener('change', () => {
      const v = /** @type {HTMLSelectElement} */ ($('tm-category')).value;
      this.#setFilters({ ...this.#f, categoryId: v === '' ? null : Number(v), subcategoryId: null });
    });
    $('tm-subcategory').addEventListener('change', () => {
      const v = /** @type {HTMLSelectElement} */ ($('tm-subcategory')).value;
      const id = v === '' ? null : Number(v);
      this.#setFilters({ ...this.#f, subcategoryId: id, categoryId: id == null ? this.#f.categoryId : this.#categoryOf(id) ?? this.#f.categoryId });
    });
    $('tm-filters-toggle').addEventListener('click', () => this.#toggleFiltersPanel());
    $('tm-reset').addEventListener('click', () => this.#setFilters({ ...defaultFilters(), sort: this.#f.sort, desc: this.#f.desc }));
    $('tm-results').addEventListener('scroll', () => {
      const box = $('tm-results');
      if (this.#more && !this.#loading && box.scrollTop + box.clientHeight > box.scrollHeight - 200) this.#load(false);
    });

    // Requadre «Categories»
    $('tm-box-category').addEventListener('change', () => {
      const v = /** @type {HTMLSelectElement} */ ($('tm-box-category')).value;
      this.#boxCategory = v === '' ? null : Number(v);
      this.#boxSubcategory = null;
      this.#renderBox();
    });
    $('tm-box-subcategory').addEventListener('change', () => {
      const v = /** @type {HTMLSelectElement} */ ($('tm-box-subcategory')).value;
      this.#boxSubcategory = v === '' ? null : Number(v);
      this.#renderBox();
    });
    $('tm-new-category').addEventListener('click', () => this.#newCategory());
    $('tm-rename-category').addEventListener('click', () => this.#renameCategory());
    $('tm-delete-category').addEventListener('click', () => this.#deleteCategory());
    $('tm-new-subcategory').addEventListener('click', () => this.#newSubcategory());
    $('tm-rename-subcategory').addEventListener('click', () => this.#renameSubcategory());
    $('tm-delete-subcategory').addEventListener('click', () => this.#deleteSubcategory());
    $('tm-folder').addEventListener('click', () => this.#folder());
    $('tm-empty').addEventListener('click', () => this.#empty());
    $('tm-tags').addEventListener('click', () => this.#manageTags());
    this.#renderDetail();
  }

  /** La pantalla es mostra: la primera vegada, carrega-ho tot. */
  show() {
    this.#visible = true;
    if (!this.#loaded) this.refresh();
  }

  hide() {
    this.#visible = false;
  }

  /** Per al diàleg d'importar: les categories i la subcategoria triada (al requadre o, si no, al filtre). */
  importContext() {
    return {
      categories: this.#filters?.categories ?? [],
      subcategoryId: this.#boxSubcategory ?? this.#f.subcategoryId,
    };
  }

  /** Hi ha canvis a la fitxa sense desar (per avisar abans de sortir-ne). */
  get dirty() {
    return !!(this.#track && this.#readForm && Object.keys(trackChanges(this.#track, this.#readForm())).length > 0);
  }

  /** En connectar-se (o tornar-s'hi a connectar), si la pantalla es mostra: les categories i les pistes. */
  async refresh() {
    if (!this.#visible) {
      this.#loaded = false; // es carregarà en mostrar-la
      return;
    }
    this.#loaded = true;
    await this.#loadFilters();
    await this.#search();
  }

  /**
   * Algú ha canviat la biblioteca (library.changed): els comptes i, si és la pista oberta i no s'hi està escrivint, la
   * fitxa. @param {number | null} trackId
   */
  async libraryChanged(trackId) {
    if (!this.#loaded) return;
    await this.#loadFilters();
    // Categories noves o una importació acabada: la llista també pot haver canviat
    if (trackId == null) await this.#search();
    if (trackId != null && this.#track?.id === trackId && !this.dirty) await this.#open(trackId, false);
  }

  // --- Requadre «Categories» (D-111): les accions actuen sobre la categoria i la subcategoria que s'hi trien ---

  async #loadFilters() {
    try {
      this.#filters = await this.#send('library.filters');
    } catch {
      return; // sense connexió: ja ho diu la capçalera
    }
    this.#fillSelects();
    this.#renderBox();
  }

  /** La categoria d'una subcategoria (id), o null. @param {number | null} subId */
  #categoryOf(subId) {
    return this.#filters?.categories.find((/** @type {any} */ c) => c.subcategories.some((/** @type {any} */ s) => s.id === subId))?.id ?? null;
  }

  /** @param {number | null} id */
  #category(id) {
    return this.#filters?.categories.find((/** @type {any} */ c) => c.id === id) ?? null;
  }

  /** @param {number | null} id */
  #subcategory(id) {
    return this.#filters?.categories.flatMap((/** @type {any} */ c) => c.subcategories).find((/** @type {any} */ s) => s.id === id) ?? null;
  }

  /** Els desplegables del requadre, l'estat de la carpeta vigilada i quins botons es poden fer servir. */
  #renderBox() {
    const f = this.#filters;
    if (!f) return;
    if (this.#boxCategory != null && !this.#category(this.#boxCategory)) this.#boxCategory = null; // s'ha esborrat
    if (this.#boxSubcategory != null && this.#categoryOf(this.#boxSubcategory) !== this.#boxCategory) this.#boxSubcategory = null;
    const cat = /** @type {HTMLSelectElement} */ ($('tm-box-category'));
    cat.replaceChildren(option('', '(tria una categoria)'), ...f.categories.map((/** @type {any} */ c) => option(String(c.id), `${c.name} (${c.count})`)));
    cat.value = this.#boxCategory == null ? '' : String(this.#boxCategory);
    const category = this.#category(this.#boxCategory);
    const sub = /** @type {HTMLSelectElement} */ ($('tm-box-subcategory'));
    sub.replaceChildren(option('', category ? '(tria una subcategoria)' : '(tria abans la categoria)'),
      ...(category?.subcategories ?? []).map((/** @type {any} */ s) => option(String(s.id), this.#subOptionText(s, s.name))));
    sub.value = this.#boxSubcategory == null ? '' : String(this.#boxSubcategory);
    sub.disabled = !category;

    const hasCat = this.#boxCategory != null;
    const hasSub = this.#boxSubcategory != null;
    for (const id of ['tm-rename-category', 'tm-delete-category', 'tm-new-subcategory']) /** @type {HTMLButtonElement} */ ($(id)).disabled = !hasCat;
    for (const id of ['tm-rename-subcategory', 'tm-delete-subcategory', 'tm-folder', 'tm-empty']) /** @type {HTMLButtonElement} */ ($(id)).disabled = !hasSub;

    // La carpeta vigilada de la subcategoria triada, si en té (sempre amb text, no només el color)
    const status = $('tm-folder-status');
    const folder = hasSub ? this.#folders.statusOf(/** @type {number} */ (this.#boxSubcategory)) : null;
    if (!folder) {
      status.replaceChildren();
      return;
    }
    const warn = folder.problem || folder.missing > 0;
    const text = `Carpeta vigilada${folder.renewable ? ' (bossa renovable)' : ''}: ${folder.folder}` +
      (folder.problem ? ` · ${folder.problem}` : folder.missing > 0 ? ` · ${folder.missing} amb el fitxer que falta` : '');
    status.replaceChildren(statusLabel(warn ? 'text-warning' : 'text-info', warn ? 'bi-exclamation-triangle-fill' : folder.renewable ? 'bi-arrow-repeat' : 'bi-folder-check', text));
  }

  /**
   * El text d'una subcategoria en un desplegable (D-119): el nom, quantes pistes té i, si té carpeta vigilada, una marca
   * al davant amb la icona (el caràcter de la font d'icones: els desplegables no poden portar HTML) i, al final, el text
   * (mai només la icona): «carpeta vigilada», «bossa renovable» o «carpeta amb avís».
   * @param {any} sub @param {string} name
   */
  #subOptionText(sub, name) {
    const folder = this.#folders.statusOf(sub.id);
    const base = `${name} (${sub.count})`;
    if (!folder) return base;
    const warn = folder.problem || folder.missing > 0;
    const glyph = warn ? '' : folder.renewable ? '' : ''; // bi-exclamation-triangle-fill, bi-arrow-repeat, bi-folder-check
    return `${glyph} ${base} · ${warn ? 'carpeta amb avís' : folder.renewable ? 'bossa renovable' : 'carpeta vigilada'}`;
  }

  /** «Categoria / Subcategoria» de la subcategoria triada al requadre. */
  #boxSubText() {
    const category = this.#category(this.#boxCategory);
    const sub = this.#subcategory(this.#boxSubcategory);
    return category && sub ? `${category.name} / ${sub.name}` : '';
  }

  /** Obre la carpeta vigilada de la subcategoria triada. */
  async #folder() {
    if (this.#boxSubcategory == null) return;
    await this.#folders.show({ id: this.#boxSubcategory, name: this.#boxSubText() });
  }

  async #newCategory() {
    const name = await askText({ title: 'Nova categoria', label: 'Nom de la categoria', confirm: 'Crea-la' });
    if (!name) return;
    const result = await this.#try('No s\'ha pogut crear la categoria', () => this.#send('category.save', { name }));
    if (result) {
      this.#boxCategory = result.id;
      this.#boxSubcategory = null;
    }
    await this.#loadFilters();
  }

  async #newSubcategory() {
    const category = this.#category(this.#boxCategory);
    if (!category) return;
    const name = await askText({ title: `Nova subcategoria a «${category.name}»`, label: 'Nom de la subcategoria', confirm: 'Crea-la' });
    if (!name) return;
    const result = await this.#try('No s\'ha pogut crear la subcategoria', () => this.#send('subcategory.save', { categoryId: category.id, name }));
    if (result) this.#boxSubcategory = result.id;
    await this.#loadFilters();
  }

  async #renameCategory() {
    const category = this.#category(this.#boxCategory);
    if (!category) return;
    const name = await askText({ title: 'Canviar el nom de la categoria', label: 'Nom nou', confirm: 'Canvia el nom', value: category.name });
    if (name && name !== category.name) await this.#try('No s\'ha pogut canviar el nom', () => this.#send('category.save', { categoryId: category.id, name }));
    await this.#loadFilters();
  }

  async #renameSubcategory() {
    const sub = this.#subcategory(this.#boxSubcategory);
    if (!sub) return;
    const name = await askText({ title: 'Canviar el nom de la subcategoria', label: 'Nom nou', confirm: 'Canvia el nom', value: sub.name });
    if (name && name !== sub.name) await this.#try('No s\'ha pogut canviar el nom', () => this.#send('subcategory.save', { subcategoryId: sub.id, name }));
    await this.#loadFilters();
    if (this.#track) await this.#open(this.#track.id, false); // la fitxa diu el nom de la subcategoria
  }

  /** Esborra la categoria triada: el motor només ho fa si no té subcategories ni la fa servir cap rotació. */
  async #deleteCategory() {
    const category = this.#category(this.#boxCategory);
    if (!category) return;
    const ok = await confirmAction({ title: 'Esborrar la categoria', message: `S'esborrarà la categoria «${category.name}».`, confirm: 'Esborra-la' });
    if (!ok) return;
    const done = await this.#try('No s\'ha pogut esborrar la categoria', () => this.#send('category.delete', { categoryId: category.id }));
    if (!done) return;
    this.#boxCategory = null;
    this.#boxSubcategory = null;
    if (this.#f.categoryId === category.id) this.#f = { ...this.#f, categoryId: null, subcategoryId: null };
    await this.#loadFilters();
  }

  /** Esborra la subcategoria triada: el motor només ho fa si és buida i no la fa servir res (diu què, si no). */
  async #deleteSubcategory() {
    const sub = this.#subcategory(this.#boxSubcategory);
    if (!sub) return;
    const ok = await confirmAction({ title: 'Esborrar la subcategoria', message: `S'esborrarà la subcategoria «${this.#boxSubText()}».`, confirm: 'Esborra-la' });
    if (!ok) return;
    const done = await this.#try('No s\'ha pogut esborrar la subcategoria', () => this.#send('subcategory.delete', { subcategoryId: sub.id }));
    if (!done) return;
    this.#boxSubcategory = null;
    if (this.#f.subcategoryId === sub.id) this.#f = { ...this.#f, subcategoryId: null };
    await this.#loadFilters();
  }

  /**
   * Buida la subcategoria triada (D-089): esborra totes les seves pistes de la biblioteca, p. ex. per renovar una bossa
   * abans d'importar-ne la nova. Primer es demana al motor què faria i es confirma.
   */
  async #empty() {
    const subcategoryId = this.#boxSubcategory;
    if (subcategoryId == null) return;
    const preview = await this.#try('No s\'ha pogut buidar la subcategoria',
      () => this.#send('subcategory.empty', { subcategoryId, dryRun: true }));
    if (!preview) return;
    if (preview.tracks === 0) {
      await confirmAction({ title: 'Buidar la subcategoria', message: `«${preview.name}» ja és buida.`, confirm: 'D\'acord', info: true });
      return;
    }
    const toDelete = preview.tracks - preview.usedByEvents;
    if (toDelete === 0) {
      await confirmAction({
        title: 'Buidar la subcategoria',
        message: `Les ${preview.tracks} pistes de «${preview.name}» les fan servir esdeveniments del planificador: no se n'esborra cap.`,
        confirm: 'D\'acord',
        info: true,
      });
      return;
    }
    const lines = [`S'esborraran de la biblioteca ${toDelete} pistes de «${preview.name}». ` +
      'L\'historial (i els certificats) les conserven i els fitxers no es toquen, però no es pot desfer.'];
    if (preview.usedByEvents > 0)
      lines.push(`${preview.usedByEvents} les fan servir esdeveniments: no s'esborraran i es quedaran com són.`);
    if (preview.rotations.length > 0)
      lines.push(`⚠ Les rotacions ${preview.rotations.map((/** @type {string} */ r) => `«${r}»`).join(', ')} tenen passos d'aquesta ` +
        'subcategoria: fins que no s\'hi importi música nova, l\'AutoDJ saltarà aquests passos (amb un avís).');
    const ok = await confirmAction({ title: 'Buidar la subcategoria', message: lines.join('\n\n'), confirm: `Esborra ${toDelete} ${toDelete === 1 ? 'pista' : 'pistes'}` });
    if (!ok) return;
    const done = await this.#try('No s\'ha pogut buidar la subcategoria', () => this.#send('subcategory.empty', { subcategoryId }));
    if (!done) return;
    if (this.#track && this.#track.subcategoryId === subcategoryId) {
      this.#track = null;
      this.#renderDetail();
    }
    await this.#loadFilters();
    await this.#search();
    await confirmAction({
      title: 'Subcategoria buidada',
      message: `«${done.name}»: ${done.removed} pistes esborrades${done.keptForEvents ? `, ${done.keptForEvents} conservades (les fan servir esdeveniments)` : ''}.`,
      confirm: 'D\'acord',
      info: true,
    });
  }

  // --- Gestor d'etiquetes (D-111) ---

  /** Una finestra amb totes les etiquetes: quantes pistes i quines rotacions; canviar el nom, fusionar i esborrar. */
  async #manageTags() {
    const list = el('div', 'tm-tags-list');
    /** @param {any[]} tags */
    const render = (tags) => {
      if (tags.length === 0) {
        list.replaceChildren(el('p', 'text-body-secondary m-0', 'Encara no hi ha cap etiqueta. Es creen escrivint-les a la fitxa d\'una pista.'));
        return;
      }
      list.replaceChildren(...tags.map((t) => {
        const row = el('div', 'tm-tag-row');
        const info = el('div', 'tm-tag-info');
        info.append(el('strong', '', t.name), el('span', 'text-body-secondary', ` · ${t.tracks} ${t.tracks === 1 ? 'pista' : 'pistes'}`));
        if (t.rotations.length > 0) info.append(el('div', 'form-text m-0', `La fan servir les rotacions ${t.rotations.map((/** @type {string} */ r) => `«${r}»`).join(', ')}`));
        const tool = (/** @type {string} */ iconName, /** @type {string} */ title, /** @type {() => Promise<void>} */ run, danger = false) => {
          const b = el('button', `btn btn-sm ${danger ? 'btn-outline-danger' : 'btn-outline-secondary'}`);
          b.type = 'button';
          b.title = title;
          b.setAttribute('aria-label', `${title}: ${t.name}`);
          b.append(icon(iconName));
          b.addEventListener('click', run);
          return b;
        };
        const tools = el('div', 'tm-tag-tools');
        tools.append(
          tool('bi-pencil', 'Canvia el nom', async () => {
            const name = await askText({ title: 'Canviar el nom de l\'etiqueta', label: 'Nom nou', confirm: 'Canvia el nom', value: t.name });
            if (name && name !== t.name) await update(() => this.#send('tags.rename', { tagId: t.id, name }));
          }),
          tool('bi-union', 'Fusiona amb una altra', async () => {
            const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select'));
            select.append(...tags.filter((o) => o.id !== t.id).map((o) => option(String(o.id), `${o.name} (${o.tracks})`)));
            if (select.options.length === 0) return;
            const ok = await formDialog({
              title: `Fusionar «${t.name}»`, icon: 'bi-union', confirm: 'Fusiona-les',
              message: `Les pistes (i les rotacions) que tenen «${t.name}» passaran a tenir l'etiqueta que triïs, i «${t.name}» desapareixerà.`,
              body: [dialogField('Dins de', select)],
            });
            if (ok) await update(() => this.#send('tags.merge', { fromId: t.id, intoId: Number(select.value) }));
          }),
          tool('bi-trash', 'Esborra', async () => {
            const ok = await confirmAction({
              title: 'Esborrar l\'etiqueta',
              message: `«${t.name}» s'esborrarà i es traurà de ${t.tracks} ${t.tracks === 1 ? 'pista' : 'pistes'}. Les pistes no s'esborren.`,
              confirm: 'Esborra-la',
            });
            if (ok) await update(() => this.#send('tags.delete', { tagId: t.id }));
          }, true),
        );
        row.append(info, tools);
        return row;
      }));
    };
    /** @param {() => Promise<any>} run */
    const update = async (run) => {
      const result = await this.#try('No s\'ha pogut canviar l\'etiqueta', run);
      if (result) render(result.tags);
    };
    const first = await this.#try('No s\'han pogut llegir les etiquetes', () => this.#send('tags.list'));
    if (!first) return;
    render(first.tags);
    await formDialog({
      title: 'Etiquetes', icon: 'bi-tags', wide: true,
      message: 'Dues etiquetes que s\'escriuen diferent (p. ex. «catala» i «català») es poden fusionar. Les que fa servir una rotació no es poden esborrar.',
      body: [list],
    });
    await this.#loadFilters();
    if (this.#track) await this.#open(this.#track.id, false); // la fitxa diu les etiquetes
  }

  // --- Selecció i accions en bloc (D-111) ---

  /** La barra d'accions: quantes n'hi ha de seleccionades, seleccionar totes les del filtre i què se'ls pot fer. */
  #renderBulk() {
    const bar = $('tm-bulk');
    const n = this.#selected.size;
    bar.classList.toggle('d-none', n === 0);
    if (n === 0) {
      bar.replaceChildren();
      return;
    }
    const button = (/** @type {string} */ iconName, /** @type {string} */ text, /** @type {() => void} */ run, css = 'btn-outline-secondary') => {
      const b = el('button', `btn btn-sm ${css}`);
      b.type = 'button';
      b.append(icon(iconName), ' ', el('span', '', text));
      b.addEventListener('click', run);
      return b;
    };
    const count = el('span', 'tm-bulk-count', `${n.toLocaleString('ca-ES')} ${n === 1 ? 'seleccionada' : 'seleccionades'}`);
    const items = [count];
    if (n < this.#total) {
      const all = el('button', 'btn btn-sm btn-link p-0', `Selecciona les ${this.#total.toLocaleString('ca-ES')} del filtre`);
      all.type = 'button';
      all.addEventListener('click', () => this.#selectAllMatching());
      items.push(all);
    }
    items.push(
      button('bi-folder-symlink', 'Canvia la subcategoria…', () => this.#bulkSubcategory()),
      button('bi-check-circle', 'Activa', () => this.#bulk({ action: 'enable' }, 'activades')),
      button('bi-slash-circle', 'Desactiva', () => this.#bulk({ action: 'disable' }, 'desactivades')),
      button('bi-tags', 'Etiqueta…', () => this.#bulkTag()),
      button('bi-trash', 'Esborra…', () => this.#bulkDelete(), 'btn-outline-danger'),
      button('bi-x-lg', 'Treu la selecció', () => this.#clearSelection(), 'btn-link'),
    );
    bar.replaceChildren(...items);
  }

  /** Marca o desmarca una pista. @param {number} id @param {boolean} on */
  #toggleSelected(id, on) {
    if (on) this.#selected.add(id);
    else this.#selected.delete(id);
    this.#syncChecks();
  }

  /** Les caselles de les files i la de la capçalera (marcada, desmarcada o a mitges), i la barra. */
  #syncChecks() {
    let visible = 0;
    let checked = 0;
    for (const box of /** @type {NodeListOf<HTMLInputElement>} */ ($('tm-list').querySelectorAll('.tm-check'))) {
      const id = Number(/** @type {HTMLElement} */ (box.closest('tr')).dataset.trackId);
      box.checked = this.#selected.has(id);
      visible++;
      if (box.checked) checked++;
    }
    const all = /** @type {HTMLInputElement | null} */ ($('tm-head').querySelector('.tm-check-all'));
    if (all) {
      all.checked = visible > 0 && checked === visible;
      all.indeterminate = checked > 0 && checked < visible;
    }
    this.#renderBulk();
  }

  /** La casella de la capçalera: selecciona (o treu) totes les que es veuen. @param {boolean} on */
  #toggleVisible(on) {
    for (const row of /** @type {NodeListOf<HTMLElement>} */ ($('tm-list').querySelectorAll('.tm-row'))) {
      const id = Number(row.dataset.trackId);
      if (on) this.#selected.add(id);
      else this.#selected.delete(id);
    }
    this.#syncChecks();
  }

  /** Selecciona totes les pistes del filtre (no només les que es veuen), demanant-ne els ids al motor. */
  async #selectAllMatching() {
    const result = await this.#try('No s\'han pogut seleccionar', () => this.#send('library.ids', filtersToParams(this.#f)));
    if (!result) return;
    for (const id of result.ids) this.#selected.add(id);
    if (result.total > result.ids.length) {
      await confirmAction({ title: 'Massa pistes', message: `Només se n'han seleccionat ${result.ids.length.toLocaleString('ca-ES')} de ${result.total.toLocaleString('ca-ES')}.`, confirm: 'D\'acord', info: true });
    }
    this.#syncChecks();
  }

  #clearSelection() {
    this.#selected.clear();
    this.#syncChecks();
  }

  /**
   * Fa una acció en bloc (library.bulk) i ho diu. @param {Record<string, any>} params @param {string} done  «activades»…
   */
  async #bulk(params, done) {
    const ids = [...this.#selected];
    const result = await this.#try('No s\'ha pogut fer', () => this.#send('library.bulk', { trackIds: ids, ...params }));
    if (!result) return;
    await this.#afterBulk();
    await confirmAction({ title: 'Fet', message: `${result.changed.toLocaleString('ca-ES')} de ${ids.length.toLocaleString('ca-ES')} pistes ${done}.`, confirm: 'D\'acord', info: true });
  }

  /** Després d'un canvi en bloc: la llista, els comptes i, si hi era, la fitxa oberta. La selecció es manté. */
  async #afterBulk() {
    await this.#loadFilters();
    await this.#load(true);
    if (this.#track) await this.#open(this.#track.id, false);
  }

  /** «Canvia la subcategoria…» (la «migració» de categories): totes les seleccionades passen a la que es triï. */
  async #bulkSubcategory() {
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select'));
    select.append(option('', '(sense subcategoria)'), ...(this.#filters?.categories ?? []).flatMap((/** @type {any} */ c) =>
      c.subcategories.map((/** @type {any} */ s) => option(String(s.id), `${c.name} / ${s.name}`))));
    if (this.#f.subcategoryId != null) select.value = '';
    const n = this.#selected.size;
    const ok = await formDialog({
      title: 'Canviar la subcategoria', icon: 'bi-folder-symlink', confirm: `Mou ${n === 1 ? 'la pista' : `les ${n.toLocaleString('ca-ES')} pistes`}`,
      message: `${n === 1 ? 'La pista seleccionada passarà' : `Les ${n.toLocaleString('ca-ES')} pistes seleccionades passaran`} a la subcategoria que triïs.`,
      body: [dialogField('Subcategoria nova', select)],
    });
    if (!ok) return;
    await this.#bulk({ action: 'subcategory', subcategoryId: select.value === '' ? null : Number(select.value) }, 'mogudes');
  }

  /** «Etiqueta…»: afegeix o treu una etiqueta a totes les seleccionades. */
  async #bulkTag() {
    const tags = this.#filters?.tags ?? [];
    const what = /** @type {HTMLSelectElement} */ (el('select', 'form-select'));
    what.append(option('add', 'Afegeix-los l\'etiqueta'), option('remove', 'Treu-los l\'etiqueta'));
    const name = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    name.type = 'text';
    name.maxLength = 100;
    name.setAttribute('list', 'tm-tag-names');
    const names = el('datalist', '');
    names.id = 'tm-tag-names';
    names.append(...tags.map((/** @type {any} */ t) => option(t.name, '')));
    const n = this.#selected.size;
    const ok = await formDialog({
      title: 'Etiqueta', icon: 'bi-tags', confirm: 'Fes-ho',
      message: `A ${n === 1 ? 'la pista seleccionada' : `les ${n.toLocaleString('ca-ES')} pistes seleccionades`}.`,
      body: [dialogField('Què', what), dialogField('Etiqueta', name, 'Si l\'afegeixes i no existeix, es crea.'), names],
    });
    const text = name.value.trim();
    if (!ok || !text) return;
    if (what.value === 'add') {
      await this.#bulk({ action: 'addTag', tag: text }, 'amb l\'etiqueta');
      return;
    }
    const tag = tags.find((/** @type {any} */ t) => t.name.toLocaleLowerCase('ca') === text.toLocaleLowerCase('ca'));
    if (!tag) {
      await confirmAction({ title: 'Etiqueta', message: `No hi ha cap etiqueta «${text}».`, confirm: 'D\'acord', info: true });
      return;
    }
    await this.#bulk({ action: 'removeTag', tagId: tag.id }, 'sense l\'etiqueta');
  }

  /** «Esborra…»: primer el motor diu què passaria (les dels esdeveniments es queden; les que han sonat fa poc). */
  async #bulkDelete() {
    const ids = [...this.#selected];
    const preview = await this.#try('No s\'ha pogut esborrar', () => this.#send('library.bulk', { trackIds: ids, action: 'delete', dryRun: true }));
    if (!preview) return;
    const kept = preview.kept.length;
    const toDelete = ids.length - kept;
    if (toDelete === 0) {
      await confirmAction({ title: 'Esborrar pistes', message: 'Totes les pistes seleccionades les fan servir esdeveniments: no se n\'esborra cap.', confirm: 'D\'acord', info: true });
      return;
    }
    const lines = [`S'esborraran de la biblioteca ${toDelete.toLocaleString('ca-ES')} ${toDelete === 1 ? 'pista' : 'pistes'}. Els fitxers no es toquen, però no es pot desfer.`];
    if (kept > 0) {
      const sample = preview.kept.slice(0, 5).map((/** @type {any} */ k) => `«${k.title}» (${k.events})`).join(', ');
      lines.push(`${kept} les fan servir esdeveniments i no s'esborraran: ${sample}${kept > 5 ? '…' : ''}.`);
    }
    if (preview.playedRecently > 0) {
      lines.push(`⚠ ${preview.playedRecently} han sonat els últims 2 anys: l'historial es conserva, però ja no es podrà fer el seu certificat ` +
        'per pista. Si són falques, potser és millor desactivar-les.');
    }
    const ok = await confirmAction({ title: 'Esborrar pistes', message: lines.join('\n\n'), confirm: `Esborra ${toDelete.toLocaleString('ca-ES')} ${toDelete === 1 ? 'pista' : 'pistes'}` });
    if (!ok) return;
    const done = await this.#try('No s\'ha pogut esborrar', () => this.#send('library.bulk', { trackIds: ids, action: 'delete' }));
    if (!done) return;
    for (const id of ids) if (!done.kept.some((/** @type {any} */ k) => k.id === id)) this.#selected.delete(id);
    if (this.#track && !done.kept.some((/** @type {any} */ k) => k.id === this.#track.id) && ids.includes(this.#track.id)) {
      this.#track = null;
      this.#renderDetail();
    }
    await this.#afterBulk();
    await confirmAction({ title: 'Fet', message: `${done.deleted.toLocaleString('ca-ES')} pistes esborrades.`, confirm: 'D\'acord', info: true });
  }

  // --- Llista de pistes (D-111): filtres, capçaleres per ordenar, taula i recompte ---

  /** Les columnes de la taula: clau d'ordre (null = no s'hi pot ordenar), títol i classe. */
  static COLUMNS = [
    { sort: null, label: '', css: 'tm-c-check' },
    { sort: 'title', label: 'Títol', css: 'tm-c-title' },
    { sort: 'artist', label: 'Artista', css: 'tm-c-artist' },
    { sort: 'type', label: 'Tipus', css: 'tm-c-type' },
    { sort: 'duration', label: 'Durada', css: 'tm-c-num' },
    { sort: null, label: 'Estat', css: 'tm-c-state' },
    { sort: 'validUntil', label: 'Data de fi', css: 'tm-c-date' },
    { sort: 'plays', label: 'Reprod.', css: 'tm-c-num', title: 'Quantes vegades ha sonat' },
    { sort: 'lastPlayed', label: 'Última vegada', css: 'tm-c-played' },
  ];

  /** Torna a cercar des del principi amb els filtres d'ara. */
  async #search() {
    $('tm-results').scrollTop = 0;
    this.#renderHead();
    this.#renderChips();
    await this.#load(true);
  }

  /** Canvia els filtres (des d'un control), torna a dibuixar el que en depèn i cerca. @param {import('../tracks-filter.js').TrackFilters} f */
  #setFilters(f) {
    // Si canvien els filtres (no només l'ordre), la selecció ja no correspon a la llista: es treu
    const key = (/** @type {import('../tracks-filter.js').TrackFilters} */ x) => JSON.stringify({ ...filtersToParams(x), sort: null, desc: null });
    if (key(f) !== key(this.#f)) this.#selected.clear();
    // El requadre «Categories» segueix la categoria i la subcategoria del filtre (per actuar-hi sense tornar-les a triar)
    if (f.categoryId !== this.#f.categoryId || f.subcategoryId !== this.#f.subcategoryId) {
      this.#boxCategory = f.categoryId;
      this.#boxSubcategory = f.subcategoryId;
      this.#renderBox();
    }
    this.#f = f;
    this.#syncFilterControls();
    this.#search();
  }

  /** Les capçaleres: un botó per ordenar (amb la fletxa del sentit) a les columnes que es poden ordenar. */
  #renderHead() {
    const tr = el('tr', '');
    for (const c of TrackManagerView.COLUMNS) {
      const th = el('th', c.css);
      th.scope = 'col';
      if (c.css === 'tm-c-check') {
        // Selecciona (o treu) totes les que es veuen
        const all = /** @type {HTMLInputElement} */ (el('input', 'form-check-input tm-check-all'));
        all.type = 'checkbox';
        all.title = 'Selecciona totes les que es veuen';
        all.setAttribute('aria-label', all.title);
        all.addEventListener('change', () => this.#toggleVisible(all.checked));
        th.append(all);
      } else if (!c.sort) {
        th.textContent = c.label;
      } else {
        const sorted = this.#f.sort === c.sort;
        th.setAttribute('aria-sort', sorted ? (this.#f.desc ? 'descending' : 'ascending') : 'none');
        const b = el('button', `tm-sort${sorted ? ' is-sorted' : ''}`);
        b.type = 'button';
        b.title = c.title ?? `Ordena per ${c.label.toLowerCase()}`;
        b.append(el('span', '', c.label));
        if (sorted) b.append(' ', icon(this.#f.desc ? 'bi-sort-down' : 'bi-sort-up'));
        b.addEventListener('click', () => this.#setFilters(sortBy(this.#f, /** @type {string} */ (c.sort))));
        th.append(b);
      }
      tr.append(th);
    }
    $('tm-head').replaceChildren(tr);
  }

  /** Les pastilles dels filtres actius de dins de «Filtres», amb la creu per treure'n cada un, i el comptador del botó. */
  #renderChips() {
    const tags = this.#filters?.tags ?? [];
    const chips = filterChips(this.#f, {
      tagName: (id) => tags.find((/** @type {any} */ t) => t.id === id)?.name ?? `etiqueta ${id}`,
      typeName,
    });
    $('tm-chips').replaceChildren(...chips.map((c) => {
      const chip = el('span', 'tm-chip');
      const x = el('button', 'tm-chip-remove');
      x.type = 'button';
      x.title = `Treu el filtre «${c.text}»`;
      x.setAttribute('aria-label', x.title);
      x.append(icon('bi-x'));
      x.addEventListener('click', () => this.#setFilters(withoutFilter(this.#f, c.key)));
      chip.append(el('span', '', c.text), x);
      return chip;
    }));
    const count = $('tm-filters-count');
    count.textContent = String(chips.length);
    count.classList.toggle('d-none', chips.length === 0);
    /** @type {HTMLButtonElement} */ ($('tm-reset')).disabled = !isFiltered(this.#f);
  }

  /** Omple els desplegables de categoria i subcategoria amb el que diu el motor, i el panell de filtres. */
  #fillSelects() {
    const f = this.#filters;
    if (!f) return;
    const category = /** @type {HTMLSelectElement} */ ($('tm-category'));
    category.replaceChildren(option('', 'Totes les categories'),
      ...f.categories.map((/** @type {any} */ c) => option(String(c.id), `${c.name} (${c.count})`)));
    this.#syncFilterControls();
    this.#renderFiltersPanel();
  }

  /** Posa als controls de dalt el que diuen els filtres (p. ex. després de clicar l'arbre o «Restableix»). */
  #syncFilterControls() {
    const f = this.#filters;
    // La cerca no es toca mentre s'hi escriu (el text passa als filtres un moment després)
    const search = /** @type {HTMLInputElement} */ ($('tm-search'));
    if (document.activeElement !== search) search.value = this.#f.text;
    const category = /** @type {HTMLSelectElement} */ ($('tm-category'));
    category.value = this.#f.categoryId == null ? '' : String(this.#f.categoryId);
    const chosen = f?.categories.find((/** @type {any} */ c) => c.id === this.#f.categoryId);
    const subs = chosen ? chosen.subcategories : (f?.categories ?? []).flatMap((/** @type {any} */ c) => c.subcategories.map((/** @type {any} */ s) => ({ ...s, name: `${c.name} / ${s.name}` })));
    const sub = /** @type {HTMLSelectElement} */ ($('tm-subcategory'));
    sub.replaceChildren(option('', 'Totes les subcategories'), ...subs.map((/** @type {any} */ s) => option(String(s.id), this.#subOptionText(s, s.name))));
    sub.value = this.#f.subcategoryId == null ? '' : String(this.#f.subcategoryId);
    if (!$('tm-filters-panel').classList.contains('d-none')) this.#renderFiltersPanel();
  }

  /** El panell «Filtres»: estat, esdeveniments, vigència, última reproducció, etiqueta i tipus. */
  #renderFiltersPanel() {
    const panel = $('tm-filters-panel');
    const f = this.#f;
    const field = (/** @type {string} */ label, /** @type {HTMLElement[]} */ controls) => {
      const box = el('div', 'tm-filter');
      const id = `tm-flt-${label.replace(/\W+/g, '-').toLowerCase()}`;
      controls[0].id = id;
      const l = /** @type {HTMLLabelElement} */ (el('label', 'form-label', label));
      l.htmlFor = id;
      const row = el('div', 'tm-filter-controls');
      row.append(...controls);
      box.append(l, row);
      return box;
    };
    const select = (/** @type {string[][]} */ options, /** @type {string} */ value, /** @type {(v: string) => void} */ change) => {
      const s = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
      s.append(...options.map(([v, t]) => option(v, t)));
      s.value = value;
      s.addEventListener('change', () => change(s.value));
      return s;
    };
    const date = (/** @type {string} */ value, /** @type {string} */ label, /** @type {(v: string) => void} */ change) => {
      const i = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
      i.type = 'date';
      i.value = value;
      i.setAttribute('aria-label', label);
      i.addEventListener('change', () => change(i.value));
      return i;
    };

    const validity = [select(VALIDITY_OPTIONS, f.validity, (v) => this.#setFilters({ ...this.#f, validity: v }))];
    if (f.validity === 'overlaps') {
      validity.push(date(f.validFrom, 'Vigents des del dia', (v) => this.#setFilters({ ...this.#f, validFrom: v })),
        date(f.validTo, 'Vigents fins al dia', (v) => this.#setFilters({ ...this.#f, validTo: v })));
    }
    const played = [select([['', 'Qualsevol'], ['never', 'No han sonat mai'], ['range', 'Entre dues dates…']], f.played,
      (v) => this.#setFilters({ ...this.#f, played: /** @type {any} */ (v) }))];
    if (f.played === 'range') {
      played.push(date(f.playedFrom, 'Última vegada des del dia', (v) => this.#setFilters({ ...this.#f, playedFrom: v })),
        date(f.playedTo, 'Última vegada fins al dia', (v) => this.#setFilters({ ...this.#f, playedTo: v })));
    }
    const tags = (this.#filters?.tags ?? []).map((/** @type {any} */ t) => [String(t.id), `${t.name} (${t.count})`]);
    panel.replaceChildren(
      field('Estat', [select(STATE_OPTIONS, f.state, (v) => this.#setFilters({ ...this.#f, state: v }))]),
      field('Esdeveniments', [select([['', 'Totes'], ['yes', 'Dins d\'algun esdeveniment'], ['no', 'Fora dels esdeveniments']], f.inEvents,
        (v) => this.#setFilters({ ...this.#f, inEvents: /** @type {any} */ (v) }))]),
      field('Vigència', validity),
      field('Última reproducció', played),
      field('Etiqueta', [select([['', 'Totes les etiquetes'], ...tags], f.tagId == null ? '' : String(f.tagId),
        (v) => this.#setFilters({ ...this.#f, tagId: v === '' ? null : Number(v) }))]),
      field('Tipus', [select([['', 'Tots els tipus'], ...TRACK_TYPES.map((t) => [t, typeName(t)])], f.type,
        (v) => this.#setFilters({ ...this.#f, type: v }))]),
    );
  }

  /** Obre o tanca el panell «Filtres». */
  #toggleFiltersPanel() {
    const panel = $('tm-filters-panel');
    const open = panel.classList.contains('d-none');
    if (open) this.#renderFiltersPanel();
    panel.classList.toggle('d-none', !open);
    $('tm-filters-toggle').setAttribute('aria-expanded', String(open));
  }

  /** @param {boolean} fromStart */
  async #load(fromStart) {
    const token = ++this.#token;
    this.#loading = true;
    const offset = fromStart ? 0 : this.#offset;
    const params = { ...filtersToParams(this.#f), offset, limit: PAGE };
    try {
      const result = await this.#send('library.search', params);
      if (token !== this.#token) return; // n'hi ha una de més nova
      const list = $('tm-list');
      if (fromStart) list.replaceChildren();
      const today = todayIso();
      const now = new Date();
      list.append(...result.tracks.map((/** @type {any} */ t) => this.#row(t, today, now)));
      this.#offset = offset + result.tracks.length;
      this.#more = result.more;
      const total = result.total ?? this.#offset;
      this.#total = total;
      this.#syncChecks();
      $('tm-count').textContent = totalText(total) + (this.#offset < total ? ` · se'n veuen ${this.#offset.toLocaleString('ca-ES')}: baixa per veure'n més` : '');
    } catch (err) {
      if (token === this.#token) $('tm-count').textContent = `No s'ha pogut cercar: ${/** @type {Error} */ (err).message}`;
    } finally {
      if (token === this.#token) this.#loading = false;
    }
  }

  /** Una fila de la taula: fent-hi clic (o Enter) s'obre la fitxa. @param {any} track @param {string} today @param {Date} now */
  #row(track, today, now) {
    const tr = el('tr', `tm-row${this.#track?.id === track.id ? ' active' : ''}`);
    tr.dataset.trackId = String(track.id);
    tr.tabIndex = 0;
    tr.title = track.path;
    if (this.#track?.id === track.id) tr.setAttribute('aria-selected', 'true');
    const cell = (/** @type {string} */ css, /** @type {string | Node} */ content) => {
      const td = el('td', css);
      td.append(content);
      tr.append(td);
      return td;
    };
    // La casella de selecció: no obre la fitxa
    const check = /** @type {HTMLInputElement} */ (el('input', 'form-check-input tm-check'));
    check.type = 'checkbox';
    check.checked = this.#selected.has(track.id);
    check.setAttribute('aria-label', `Selecciona «${track.title || '(sense títol)'}»`);
    check.addEventListener('click', (e) => e.stopPropagation());
    check.addEventListener('change', () => this.#toggleSelected(track.id, check.checked));
    cell('tm-c-check', check);
    cell('tm-c-title', track.title || '(sense títol)');
    cell('tm-c-artist', track.artist || '');
    cell('tm-c-type', typeBadge(track.type));
    cell('tm-c-num', formatDuration(track.durationMs));
    const states = el('span', 'tm-states');
    for (const s of trackStates(track, today)) {
      const label = statusLabel(s.css, s.icon, s.text);
      if (s.title) label.title = s.title;
      states.append(label);
    }
    cell('tm-c-state', states);
    cell('tm-c-date', dateCell(track.validUntil));
    cell('tm-c-num', String(track.playCount ?? 0));
    const played = cell('tm-c-played', track.lastPlayed ? playedAgo(track.lastPlayed, now) : 'mai');
    if (track.lastPlayed) played.title = new Date(track.lastPlayed).toLocaleString('ca-ES');
    tr.addEventListener('click', () => this.#select(track.id));
    tr.addEventListener('keydown', (e) => {
      if (e.target !== tr) return; // la casella fa la seva
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        this.#select(track.id);
      }
    });
    return tr;
  }

  /** Torna a dibuixar la fila d'una pista (després de desar-la), sense tornar a cercar. @param {any} track */
  #updateRow(track) {
    const old = $('tm-list').querySelector(`[data-track-id="${track.id}"]`);
    if (old) old.replaceWith(this.#row(track, todayIso(), new Date()));
  }

  /** Obre la fitxa d'una pista (p. ex. la que s'acaba d'importar, D-100). Si l'oberta té canvis, ho pregunta. @param {number} id */
  openTrack(id) {
    return this.#select(id);
  }

  /** @param {number} id */
  async #select(id) {
    if (this.#track?.id === id) return;
    if (this.dirty && !(await confirmAction({
      title: 'Canvis sense desar',
      message: 'La fitxa oberta té canvis que no s\'han desat. Si obres una altra pista, es perdran.',
      confirm: 'Descarta els canvis',
    }))) return;
    await this.#open(id, true);
  }

  /** @param {number} id @param {boolean} reportErrors */
  async #open(id, reportErrors) {
    try {
      const { track } = await this.#send('library.get', { trackId: id });
      this.#track = track;
    } catch (err) {
      if (reportErrors) await confirmAction({ title: 'No s\'ha pogut obrir la fitxa', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
      return;
    }
    for (const row of $('tm-list').querySelectorAll('.tm-row')) {
      const on = /** @type {HTMLElement} */ (row).dataset.trackId === String(id);
      row.classList.toggle('active', on);
      if (on) row.setAttribute('aria-selected', 'true');
      else row.removeAttribute('aria-selected');
    }
    this.#renderDetail();
  }

  // --- Fitxa ---

  #renderDetail() {
    const box = $('tm-detail');
    const t = this.#track;
    // Sense cap pista oberta, la columna de la fitxa s'amaga i la llista ocupa l'espai (D-119)
    box.classList.toggle('is-empty', !t);
    if (!t) {
      this.#readForm = null;
      box.replaceChildren();
      return;
    }

    const form = el('form', 'tm-form');
    form.addEventListener('submit', (e) => {
      e.preventDefault();
      this.#save();
    });

    // Capçalera: títol, estat i fitxer
    const head = el('div', 'tm-head');
    head.append(el('h2', 'h5 tm-head-title', t.artist ? `${t.artist} - ${t.title}` : t.title || '(sense títol)'));
    const badges = el('div', 'tm-head-badges');
    const ownBadges = stateBadges(t);
    badges.append(...(ownBadges.length ? ownBadges : [statusLabel('text-success', 'bi-check-circle', 'El fitxer hi és')]));
    head.append(badges, el('div', 'tm-path', t.path));
    const headTools = el('div', 'tm-head-tools');
    const cues = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    cues.type = 'button';
    cues.append(icon('bi-soundwave'), ' Punts…');
    cues.disabled = t.fileState === 'missing' || t.type === 'stream';
    cues.title = cues.disabled ? 'Sense fitxer (o un stream) no hi ha forma d\'ona per editar' : 'Editor de punts: forma d\'ona, inici, mescla i final';
    cues.addEventListener('click', () => this.#editCues());
    const calendar = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    calendar.type = 'button';
    calendar.append(icon('bi-calendar-check'), ' Calendari…');
    calendar.title = 'Obre el calendari d\'aquesta pista: on sona i quants passis fa';
    calendar.addEventListener('click', () => this.#openCalendar({ id: t.id, title: t.title }));
    const explorer = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    explorer.type = 'button';
    explorer.append(icon('bi-folder2-open'), ' Carpeta…');
    explorer.disabled = t.type === 'stream' || String(t.path ?? '').includes('://'); // un stream és una adreça, no un fitxer
    explorer.title = explorer.disabled ? "És una adreça d'internet, no un fitxer" : "Obre a l'Explorador de Windows la carpeta del fitxer, amb el fitxer seleccionat";
    explorer.addEventListener('click', () => this.#openFolder(t.path));
    const close = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary ms-auto'));
    close.type = 'button';
    close.title = 'Tanca la fitxa';
    close.append(icon('bi-x-lg'), ' Tanca');
    close.addEventListener('click', () => this.#closeDetail());
    // Els informes de la pista (D-138): els passis previstos i les emissions reals, en PDF
    const report = (/** @type {'programming' | 'certificate'} */ kind, /** @type {string} */ iconName, /** @type {string} */ text, /** @type {string} */ help) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      b.type = 'button';
      b.append(icon(iconName), ` ${text}`);
      b.title = help;
      b.addEventListener('click', () => this.#openReport(kind, t));
      return b;
    };
    headTools.append(cues, calendar,
      report('programming', 'bi-calendar-range', 'Programació…', 'Informe de programació d\'aquesta pista: els passis previstos, en PDF'),
      report('certificate', 'bi-patch-check', 'Certificat…', 'Certificat d\'emissió d\'aquesta pista: les emissions reals (de l\'historial), en PDF'),
      explorer, close);
    head.append(headTools);
    form.append(head);

    // Camps
    const field = (/** @type {string} */ label, /** @type {HTMLElement} */ input, /** @type {string} */ help = '') => {
      const id = `tm-f-${Math.random().toString(36).slice(2, 9)}`;
      input.id = id;
      const wrap = el('div', 'mb-2');
      const l = /** @type {HTMLLabelElement} */ (el('label', 'form-label', label));
      l.htmlFor = id;
      wrap.append(l, input);
      if (help) wrap.append(el('div', 'form-text', help));
      return wrap;
    };
    const text = (/** @type {string} */ value, /** @type {number} */ max = 500) => {
      const i = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
      i.type = 'text';
      i.value = value ?? '';
      i.maxLength = max;
      return i;
    };
    const date = (/** @type {string | null} */ value) => {
      const i = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
      i.type = 'date';
      i.value = value ?? '';
      return i;
    };
    const check = (/** @type {string} */ label, /** @type {boolean} */ value, /** @type {string} */ help = '') => {
      const wrap = el('div', 'form-check form-switch mb-2');
      const i = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
      i.type = 'checkbox';
      i.checked = value;
      i.id = `tm-c-${Math.random().toString(36).slice(2, 9)}`;
      const l = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', label));
      l.htmlFor = i.id;
      wrap.append(i, l);
      if (help) wrap.append(el('div', 'form-text', help));
      return { wrap, input: i };
    };

    const artist = text(t.artist);
    const title = text(t.title);
    const album = text(t.album);
    const year = text(t.year, 20);
    const type = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    type.append(...TRACK_TYPES.map((id) => option(id, typeName(id))));
    type.value = t.type;
    const subcategory = this.#subcategorySelect(t.subcategoryId);
    const tags = this.#tagPicker(t.tags ?? []);
    const validFrom = date(t.validFrom);
    const validUntil = date(t.validUntil);
    const enabled = check('Activada', t.enabled, 'Desactivada, l\'AutoDJ no la tria (es pot posar a la cua a mà).');
    const variable = check('Durada variable', t.variableDuration, 'El fitxer se substitueix de tant en tant (programes, promos, locucions).');
    const notes = /** @type {HTMLTextAreaElement} */ (el('textarea', 'form-control form-control-sm'));
    notes.rows = 3;
    notes.maxLength = 5000;
    notes.value = t.notes ?? '';

    const pair = (/** @type {HTMLElement} */ a, /** @type {HTMLElement} */ b) => {
      const row = el('div', 'tm-pair');
      row.append(a, b);
      return row;
    };
    form.append(
      field('Artista', artist),
      field('Títol', title),
      pair(field('Àlbum', album), field('Any', year)),
      pair(field('Tipus', type), field('Subcategoria', subcategory)),
      field('Etiquetes', tags.input, 'Escriu-ne una i prem Retorn (o una coma). Les que ja existeixen surten com a suggeriment.'),
      tags.chips,
      pair(field('Sona des del dia', validFrom), field('Fins al dia', validUntil)),
      el('div', 'form-text mt-n1 mb-2', 'Fora d\'aquestes dates, l\'AutoDJ no la tria. Buit = sense límit.'),
      enabled.wrap, variable.wrap,
      field('Notes', notes),
    );

    this.#readForm = () => ({
      artist: artist.value, title: title.value, album: album.value, year: year.value, type: type.value,
      subcategoryId: subcategory.value === '' ? null : Number(subcategory.value), tags: tags.values(),
      variableDuration: variable.input.checked, validFrom: validFrom.value, validUntil: validUntil.value,
      enabled: enabled.input.checked, notes: notes.value,
    });

    // Botons: desar i descartar (actius només si hi ha canvis)
    const actions = el('div', 'tm-actions');
    const save = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-primary'));
    save.type = 'submit';
    save.append(icon('bi-check-lg'), ' Desa');
    const discard = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    discard.type = 'button';
    discard.textContent = 'Descarta els canvis';
    discard.addEventListener('click', () => this.#renderDetail());
    const state = el('span', 'tm-dirty');
    actions.append(save, discard, state);
    form.append(actions);
    const problemsBox = el('div', 'tm-problems text-warning');
    form.append(problemsBox);

    const readForm = this.#readForm;
    const updateButtons = () => {
      const values = readForm();
      const changed = Object.keys(trackChanges(t, values)).length > 0;
      const problems = trackProblems(values);
      save.disabled = !changed || problems.length > 0;
      discard.disabled = !changed;
      state.replaceChildren(changed ? statusLabel('text-warning', 'bi-pencil-fill', 'Canvis sense desar') : '');
      problemsBox.replaceChildren(...problems.map((p) => statusLabel('text-warning', 'bi-exclamation-triangle-fill', p)));
    };
    form.addEventListener('input', updateButtons);
    form.addEventListener('change', updateButtons);
    updateButtons();

    // Informació (no editable)
    form.append(this.#info(t));

    // Fitxer i esborrar
    const fileTools = el('div', 'tm-file-tools');
    const relink = /** @type {HTMLButtonElement} */ (el('button', `btn btn-sm ${t.fileState === 'missing' ? 'btn-warning' : 'btn-outline-secondary'}`));
    relink.type = 'button';
    relink.append(icon('bi-file-earmark-music'), ' Canvia el fitxer…');
    relink.title = 'Fes que aquesta pista apunti a un altre fitxer (si s\'ha mogut o falta). Es conserven la fitxa i l\'historial.';
    relink.addEventListener('click', () => this.#relink());
    const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-danger'));
    remove.type = 'button';
    remove.append(icon('bi-trash'), ' Esborra de la biblioteca');
    remove.disabled = (t.usedBy ?? []).length > 0;
    remove.title = remove.disabled
      ? 'No es pot esborrar: la fan servir esdeveniments del planificador.'
      : 'Treu la pista de la biblioteca. El fitxer no s\'esborra i l\'historial la conserva.';
    remove.addEventListener('click', () => this.#delete());
    fileTools.append(relink, remove);
    form.append(fileTools);

    box.replaceChildren(form);
  }

  /**
   * Selector d'etiquetes (D-111): les de la pista com a pastilles amb una creu, i un camp per afegir-ne amb els noms que
   * ja existeixen com a suggeriment (si n'hi ha una d'igual sense comptar majúscules, es fa servir aquella). Quan canvia,
   * avisa el formulari (event «change») perquè s'activi «Desa».
   * @param {string[]} initial
   * @returns {{ input: HTMLInputElement, chips: HTMLElement, values: () => string[] }}
   */
  #tagPicker(initial) {
    const known = (this.#filters?.tags ?? []).map((/** @type {any} */ t) => /** @type {string} */ (t.name));
    let list = [...initial];
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    input.type = 'text';
    input.maxLength = 100;
    input.placeholder = list.length ? 'Afegeix-ne una altra…' : 'p. ex. català';
    const datalistId = `tm-tags-${Math.random().toString(36).slice(2, 9)}`;
    input.setAttribute('list', datalistId);
    const datalist = el('datalist', '');
    datalist.id = datalistId;
    datalist.append(...known.map((n) => option(n, '')));
    const chips = el('div', 'tm-tag-chips');
    const same = (/** @type {string} */ a, /** @type {string} */ b) => a.toLocaleLowerCase('ca') === b.toLocaleLowerCase('ca');
    const changed = () => {
      render();
      input.dispatchEvent(new Event('change', { bubbles: true }));
    };
    const add = () => {
      for (const part of input.value.split(',')) {
        const name = part.trim();
        if (!name || list.some((x) => same(x, name))) continue;
        list.push(known.find((k) => same(k, name)) ?? name);
      }
      input.value = '';
      changed();
    };
    const render = () => {
      chips.replaceChildren(...list.map((name) => {
        const chip = el('span', 'tm-chip');
        const x = el('button', 'tm-chip-remove');
        x.type = 'button';
        x.title = `Treu l'etiqueta «${name}»`;
        x.setAttribute('aria-label', x.title);
        x.append(icon('bi-x'));
        x.addEventListener('click', () => {
          list = list.filter((n) => n !== name);
          changed();
        });
        chip.append(el('span', '', name), x);
        return chip;
      }), datalist);
    };
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ',') {
        e.preventDefault(); // Retorn no ha de desar la fitxa
        add();
      } else if (e.key === 'Backspace' && input.value === '' && list.length > 0) {
        list = list.slice(0, -1);
        changed();
      }
    });
    input.addEventListener('blur', () => { if (input.value.trim()) add(); });
    render();
    return { input, chips, values: () => [...list] };
  }

  /** Desplegable de subcategories, agrupades per categoria. @param {number | null} selected */
  #subcategorySelect(selected) {
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    select.append(option('', '(cap)'));
    for (const c of this.#filters?.categories ?? []) {
      if (c.subcategories.length === 0) continue;
      const group = document.createElement('optgroup');
      group.label = c.name;
      group.append(...c.subcategories.map((/** @type {any} */ s) => option(String(s.id), s.name)));
      select.append(group);
    }
    select.value = selected == null ? '' : String(selected);
    return select;
  }

  /** Dades que no s'editen aquí: durada, punts, reproduccions i on es fa servir. @param {any} t */
  #info(t) {
    const box = el('dl', 'tm-info');
    const row = (/** @type {string} */ name, /** @type {string | Node} */ value) => {
      const dd = el('dd', '');
      dd.append(value);
      box.append(el('dt', '', name), dd);
    };
    row('Durada', formatDuration(t.durationMs));
    const cues = t.mixMs == null ? 'encara no calculats'
      : `inici ${seconds(t.startMs)} · mescla ${seconds(t.mixMs)} · final ${seconds(t.endMs)}`;
    const source = { radiodj: 'de RadioDJ', analyzer: 'calculats', manual: 'editats a mà' }[/** @type {'radiodj'} */ (t.cueSource)] ?? '';
    const cueBox = el('span', '');
    cueBox.append(`${cues}${source ? ` (${source})` : ''} `);
    row('Punts', cueBox);
    row('Ha sonat', t.playCount ? `${t.playCount} ${t.playCount === 1 ? 'vegada' : 'vegades'}, l'última ${playedAgo(t.lastPlayed, new Date())}` : 'mai');
    if ((t.usedBy ?? []).length > 0) {
      const list = el('ul', 'tm-used');
      list.append(...t.usedBy.map((/** @type {any} */ e) => el('li', '', e.name)));
      row('Esdeveniments', list);
    }
    return box;
  }

  /** Tanca la fitxa (D-119): si té canvis sense desar, ho pregunta. La fila deixa d'estar marcada. */
  async #closeDetail() {
    if (this.dirty && !(await confirmAction({
      title: 'Canvis sense desar',
      message: 'La fitxa té canvis que no s\'han desat. Si la tanques, es perdran.',
      confirm: 'Descarta els canvis',
    }))) return;
    this.#track = null;
    for (const row of $('tm-list').querySelectorAll('.tm-row.active')) {
      row.classList.remove('active');
      row.removeAttribute('aria-selected');
    }
    this.#renderDetail();
  }

  /** Obre a l'Explorador la carpeta del fitxer; si no hi és, ho diu (D-112). @param {string} path */
  async #openFolder(path) {
    const result = await this.#showFile(path);
    if (result === 'file') return;
    await confirmAction({
      title: 'Obrir la carpeta',
      message: result === 'folder'
        ? `El fitxer no hi és, però la carpeta sí: s'ha obert la carpeta.\n\n${path}`
        : `No es troba ni el fitxer ni la carpeta (potser és en una unitat de xarxa que ara no es veu).\n\n${path}`,
      confirm: "D'acord",
      info: true,
    });
  }

  /** Obre l'editor de punts de la pista oberta; si s'hi desa alguna cosa, la fitxa es torna a dibuixar. */
  async #editCues() {
    const t = this.#track;
    if (!t) return;
    if (this.dirty) {
      await confirmAction({ title: 'Canvis sense desar', message: 'Desa o descarta primer els canvis de la fitxa.', confirm: 'D\'acord', info: true });
      return;
    }
    const saved = await this.#cueEditor.open(t);
    if (!saved || this.#track?.id !== saved.id) return;
    this.#track = saved;
    this.#renderDetail();
    this.#updateRow(saved);
  }

  async #save() {
    const t = this.#track;
    if (!t || !this.#readForm) return;
    const values = this.#readForm();
    const changes = trackChanges(t, values);
    if (Object.keys(changes).length === 0 || trackProblems(values).length > 0) return;
    const result = await this.#try('No s\'ha pogut desar la fitxa', () => this.#send('library.update', { trackId: t.id, ...changes }));
    if (!result) return;
    this.#track = result.track;
    this.#renderDetail();
    this.#updateRow(result.track);
  }

  async #relink() {
    const t = this.#track;
    if (!t) return;
    if (this.dirty) {
      await confirmAction({ title: 'Canvis sense desar', message: 'Desa o descarta primer els canvis de la fitxa.', confirm: 'D\'acord', info: true });
      return;
    }
    const path = await this.#chooseFile(t.path);
    if (!path || path === t.path) return;
    const result = await this.#try('No s\'ha pogut canviar el fitxer', () => this.#send('library.relink', { trackId: t.id, path }));
    if (!result) return;
    this.#track = result.track;
    this.#renderDetail();
    this.#updateRow(result.track);
  }

  async #delete() {
    const t = this.#track;
    if (!t) return;
    const ok = await confirmAction({
      title: 'Esborrar la pista de la biblioteca',
      message: `«${t.artist ? `${t.artist} - ` : ''}${t.title}» deixarà de ser a la biblioteca: l'AutoDJ no la triarà i no es podrà cercar. ` +
        'El fitxer no s\'esborra, i l\'historial (i els certificats) la conserven. No es pot desfer.',
      confirm: 'Esborra-la',
    });
    if (!ok) return;
    const result = await this.#try('No s\'ha pogut esborrar', () => this.#send('library.delete', { trackId: t.id }));
    if (!result) return;
    $('tm-list').querySelector(`[data-track-id="${t.id}"]`)?.remove();
    this.#track = null;
    this.#renderDetail();
  }

  /**
   * Fa una ordre i, si falla, ho diu. Retorna el resultat o null.
   * @param {string} title @param {() => Promise<any>} run
   */
  async #try(title, run) {
    try {
      return await run();
    } catch (err) {
      await confirmAction({ title, message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
      return null;
    }
  }
}

/** Les marques d'estat d'una pista (color + icona + text). Buit si està bé. @param {any} track */
function stateBadges(track) {
  /** @type {HTMLElement[]} */
  const list = [];
  if (track.fileState === 'missing') list.push(statusLabel('text-warning', 'bi-exclamation-triangle-fill', 'Falta el fitxer'));
  if (track.fileState === 'retired') list.push(statusLabel('text-body-secondary', 'bi-archive', 'Retirada'));
  if (track.enabled === false) list.push(statusLabel('text-body-secondary', 'bi-slash-circle', 'Desactivada'));
  return list;
}

/** «12,3 s». @param {number | null | undefined} ms */
function seconds(ms) {
  return ms == null ? '—' : `${(ms / 1000).toFixed(1).replace('.', ',')} s`;
}

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
