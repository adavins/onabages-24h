// @ts-check
// Pantalla «Biblioteca»: el gestor de pistes (fase 3, pas 7a; D-087). A l'esquerra, les categories i els estats (amb
// quantes pistes tenen); al mig, les pistes, amb cerca i carregant-ne 100 cada vegada; a la dreta, la fitxa editable.
// Tot es desa al motor (library.update…); la interfície no toca mai la base de dades ni els fitxers.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { askText } from './ask-text.js';
import { formatDuration, typeName, playedAgo } from '../format.js';
import { TRACK_TYPES, parseTags, trackChanges, trackProblems } from '../track-form.js';
import { CueEditor } from './cue-editor.js';

const PAGE = 100;
const SEARCH_DELAY_MS = 250;

/** Els estats que es poden filtrar, amb color + icona + text. */
const STATES = [
  { id: 'missing', name: 'El fitxer falta', icon: 'bi-exclamation-triangle-fill', css: 'text-warning' },
  { id: 'retired', name: 'Retirades', icon: 'bi-archive', css: 'text-body-secondary' },
  { id: 'disabled', name: 'Desactivades', icon: 'bi-slash-circle', css: 'text-body-secondary' },
  { id: 'unclassified', name: 'Sense subcategoria', icon: 'bi-question-circle', css: 'text-body-secondary' },
];

/** @typedef {{ kind: 'all' } | { kind: 'state', id: string } | { kind: 'category', id: number } | { kind: 'subcategory', id: number }} Scope */

export class TrackManagerView {
  #send;
  /** @type {(defaultPath?: string) => Promise<string | null>} */
  #chooseFile;
  /** @type {any} */
  #filters = null;
  /** @type {Scope} */
  #scope = { kind: 'all' };
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

  /** Carpetes vigilades (marques a l'arbre i el diàleg de la carpeta). @type {import('./folders.js').FoldersView} */
  #folders;
  /** Editor de punts (fase 3, pas 7e). @type {CueEditor} */
  #cueEditor;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(defaultPath?: string) => Promise<string | null>} chooseFile  diàleg de Windows per triar un fitxer d'àudio
   * @param {import('./folders.js').FoldersView} folders
   */
  constructor(send, chooseFile, folders) {
    this.#send = send;
    this.#chooseFile = chooseFile;
    this.#folders = folders;
    this.#cueEditor = new CueEditor(send);
    folders.onChange(() => this.#renderTree());
    $('tm-folder').addEventListener('click', () => this.#folder());
    $('tm-search').addEventListener('input', () => {
      clearTimeout(this.#timer);
      this.#timer = window.setTimeout(() => this.#search(), SEARCH_DELAY_MS);
    });
    $('tm-results').addEventListener('scroll', () => {
      const box = $('tm-results');
      if (this.#more && !this.#loading && box.scrollTop + box.clientHeight > box.scrollHeight - 200) this.#load(false);
    });
    $('tm-new-category').addEventListener('click', () => this.#newCategory());
    $('tm-new-subcategory').addEventListener('click', () => this.#newSubcategory());
    $('tm-rename').addEventListener('click', () => this.#rename());
    $('tm-empty').addEventListener('click', () => this.#empty());
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

  /** Per al diàleg d'importar: les categories i la subcategoria triada a l'arbre (si n'hi ha). */
  importContext() {
    return {
      categories: this.#filters?.categories ?? [],
      subcategoryId: this.#scope.kind === 'subcategory' ? this.#scope.id : null,
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
   * Algú ha canviat la biblioteca (library.changed): els comptes de l'arbre i, si és la pista oberta i no s'hi està
   * escrivint, la fitxa. @param {number | null} trackId
   */
  async libraryChanged(trackId) {
    if (!this.#loaded) return;
    await this.#loadFilters();
    // Categories noves o una importació acabada: la llista també pot haver canviat
    if (trackId == null) await this.#search();
    if (trackId != null && this.#track?.id === trackId && !this.dirty) await this.#open(trackId, false);
  }

  // --- Arbre: estats i categories ---

  async #loadFilters() {
    try {
      this.#filters = await this.#send('library.filters');
    } catch {
      return; // sense connexió: ja ho diu la capçalera
    }
    this.#renderTree();
  }

  #renderTree() {
    const f = this.#filters;
    if (!f) return;
    const list = $('tm-tree-list');
    /** @type {HTMLElement[]} */
    const items = [];
    const item = (/** @type {Scope} */ scope, /** @type {Node} */ label, /** @type {number} */ count, /** @type {string} */ css = '') => {
      const li = el('li', css);
      const b = el('button', 'tm-tree-item');
      b.type = 'button';
      const active = this.#sameScope(scope, this.#scope);
      b.classList.toggle('active', active);
      if (active) b.setAttribute('aria-current', 'true');
      b.append(label, el('span', 'tm-tree-count', String(count)));
      b.addEventListener('click', () => this.#setScope(scope));
      li.append(b);
      items.push(li);
    };

    item({ kind: 'all' }, statusLabel('', 'bi-collection', 'Totes les pistes'), f.states?.all ?? 0, 'tm-tree-top');
    for (const s of STATES) {
      const count = f.states?.[s.id] ?? 0;
      if (count > 0 || this.#sameScope({ kind: 'state', id: s.id }, this.#scope))
        item({ kind: 'state', id: s.id }, statusLabel(s.css, s.icon, s.name), count, 'tm-tree-state');
    }
    items.push(el('li', 'tm-tree-heading', 'Categories'));
    for (const c of f.categories) {
      item({ kind: 'category', id: c.id }, statusLabel('', 'bi-folder2', c.name), c.count, 'tm-tree-category');
      for (const s of c.subcategories) item({ kind: 'subcategory', id: s.id }, this.#subLabel(s), s.count, 'tm-tree-sub');
    }
    list.replaceChildren(...items);
  }

  /**
   * El nom d'una subcategoria a l'arbre, amb una marca si vigila una carpeta (i si té problemes o fitxers que falten).
   * Sempre amb text a l'atribut title, no només el color. @param {any} sub
   */
  #subLabel(sub) {
    const label = el('span', 'tm-sub-label');
    label.append(el('span', '', sub.name));
    const folder = this.#folders.statusOf(sub.id);
    if (folder) {
      const warn = folder.problem || folder.missing > 0;
      const mark = icon(warn ? 'bi-exclamation-triangle-fill' : folder.renewable ? 'bi-arrow-repeat' : 'bi-folder-check');
      mark.classList.add('tm-folder-mark', warn ? 'text-warning' : 'text-info');
      mark.removeAttribute('aria-hidden');
      mark.setAttribute('role', 'img');
      const text = `Carpeta vigilada${folder.renewable ? ' (bossa renovable)' : ''}: ${folder.folder}` +
        (folder.problem ? `\n${folder.problem}` : folder.missing > 0 ? `\n${folder.missing} amb el fitxer que falta` : '');
      mark.setAttribute('aria-label', text);
      mark.title = text;
      label.append(' ', mark);
    }
    return label;
  }

  /** Obre la carpeta vigilada de la subcategoria triada. */
  async #folder() {
    const scope = this.#scope;
    if (scope.kind !== 'subcategory') {
      await confirmAction({ title: 'Carpeta vigilada', message: 'Tria primer, a l\'esquerra, la subcategoria.', confirm: 'D\'acord', info: true });
      return;
    }
    await this.#folders.show({ id: scope.id, name: this.#scopeText() });
  }

  /** @param {Scope} a @param {Scope} b */
  #sameScope(a, b) {
    return a.kind === b.kind && (/** @type {any} */ (a).id ?? null) === (/** @type {any} */ (b).id ?? null);
  }

  /** @param {Scope} scope */
  #setScope(scope) {
    this.#scope = scope;
    this.#renderTree();
    this.#search();
  }

  /** La categoria triada a l'arbre (o la de la subcategoria triada), o null. */
  #selectedCategory() {
    const f = this.#filters;
    if (!f) return null;
    if (this.#scope.kind === 'category') {
      const id = this.#scope.id;
      return f.categories.find((/** @type {any} */ c) => c.id === id) ?? null;
    }
    if (this.#scope.kind === 'subcategory') {
      const id = this.#scope.id;
      return f.categories.find((/** @type {any} */ c) => c.subcategories.some((/** @type {any} */ s) => s.id === id)) ?? null;
    }
    return null;
  }

  async #newCategory() {
    const name = await askText({ title: 'Nova categoria', label: 'Nom de la categoria', confirm: 'Crea-la' });
    if (!name) return;
    const result = await this.#try('No s\'ha pogut crear la categoria', () => this.#send('category.save', { name }));
    if (result) this.#scope = { kind: 'category', id: result.id };
    await this.#loadFilters();
    if (result) await this.#search();
  }

  async #newSubcategory() {
    const category = this.#selectedCategory();
    if (!category) {
      await confirmAction({ title: 'Nova subcategoria', message: 'Tria primer, a l\'esquerra, la categoria on la vols crear.', confirm: 'D\'acord', info: true });
      return;
    }
    const name = await askText({ title: `Nova subcategoria a «${category.name}»`, label: 'Nom de la subcategoria', confirm: 'Crea-la' });
    if (!name) return;
    const result = await this.#try('No s\'ha pogut crear la subcategoria', () => this.#send('subcategory.save', { categoryId: category.id, name }));
    if (result) this.#scope = { kind: 'subcategory', id: result.id };
    await this.#loadFilters();
    if (result) await this.#search();
  }

  async #rename() {
    const f = this.#filters;
    const scope = this.#scope;
    if (!f || (scope.kind !== 'category' && scope.kind !== 'subcategory')) {
      await confirmAction({ title: 'Canviar el nom', message: 'Tria primer, a l\'esquerra, la categoria o la subcategoria.', confirm: 'D\'acord', info: true });
      return;
    }
    if (scope.kind === 'category') {
      const category = f.categories.find((/** @type {any} */ c) => c.id === scope.id);
      const name = await askText({ title: 'Canviar el nom de la categoria', label: 'Nom nou', confirm: 'Canvia el nom', value: category?.name });
      if (name && name !== category?.name) await this.#try('No s\'ha pogut canviar el nom', () => this.#send('category.save', { categoryId: scope.id, name }));
    } else {
      const sub = f.categories.flatMap((/** @type {any} */ c) => c.subcategories).find((/** @type {any} */ s) => s.id === scope.id);
      const name = await askText({ title: 'Canviar el nom de la subcategoria', label: 'Nom nou', confirm: 'Canvia el nom', value: sub?.name });
      if (name && name !== sub?.name) await this.#try('No s\'ha pogut canviar el nom', () => this.#send('subcategory.save', { subcategoryId: scope.id, name }));
    }
    await this.#loadFilters();
    if (this.#track) await this.#open(this.#track.id, false); // la fitxa diu el nom de la subcategoria
  }

  /**
   * Buida la subcategoria triada (D-089): esborra totes les seves pistes de la biblioteca, p. ex. per renovar una bossa
   * abans d'importar-ne la nova. Primer es demana al motor què faria i es confirma.
   */
  async #empty() {
    const scope = this.#scope;
    if (scope.kind !== 'subcategory') {
      await confirmAction({ title: 'Buidar una subcategoria', message: 'Tria primer, a l\'esquerra, la subcategoria que vols buidar.', confirm: 'D\'acord', info: true });
      return;
    }
    const preview = await this.#try('No s\'ha pogut buidar la subcategoria',
      () => this.#send('subcategory.empty', { subcategoryId: scope.id, dryRun: true }));
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
    const done = await this.#try('No s\'ha pogut buidar la subcategoria', () => this.#send('subcategory.empty', { subcategoryId: scope.id }));
    if (!done) return;
    if (this.#track && this.#track.subcategoryId === scope.id) {
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

  // --- Llista de pistes ---

  async #search() {
    $('tm-results').scrollTop = 0;
    $('tm-scope').textContent = this.#scopeText();
    await this.#load(true);
  }

  #scopeText() {
    const scope = this.#scope;
    if (scope.kind === 'state') return STATES.find((s) => s.id === scope.id)?.name ?? '';
    if (scope.kind === 'category') return this.#selectedCategory()?.name ?? '';
    if (scope.kind === 'subcategory') {
      const category = this.#selectedCategory();
      const sub = category?.subcategories.find((/** @type {any} */ s) => s.id === scope.id);
      return category && sub ? `${category.name} / ${sub.name}` : '';
    }
    return 'Totes les pistes';
  }

  /** @param {boolean} fromStart */
  async #load(fromStart) {
    const token = ++this.#token;
    this.#loading = true;
    const offset = fromStart ? 0 : this.#offset;
    /** @type {Record<string, any>} */
    const params = { text: /** @type {HTMLInputElement} */ ($('tm-search')).value, offset, limit: PAGE };
    const scope = this.#scope;
    if (scope.kind === 'state') params.state = scope.id;
    else if (scope.kind === 'category') params.categoryId = scope.id;
    else if (scope.kind === 'subcategory') params.subcategoryId = scope.id;
    try {
      const result = await this.#send('library.search', params);
      if (token !== this.#token) return; // n'hi ha una de més nova
      const list = $('tm-list');
      if (fromStart) list.replaceChildren();
      const now = new Date();
      list.append(...result.tracks.map((/** @type {any} */ t) => this.#row(t, now)));
      this.#offset = offset + result.tracks.length;
      this.#more = result.more;
      const shown = this.#offset;
      $('tm-count').textContent = shown === 0
        ? 'Cap pista amb aquests criteris.'
        : `${shown}${result.more ? '+' : ''} ${shown === 1 ? 'pista' : 'pistes'}${result.more ? ' · baixa per veure\'n més' : ''}`;
    } catch (err) {
      if (token === this.#token) $('tm-count').textContent = `No s'ha pogut cercar: ${/** @type {Error} */ (err).message}`;
    } finally {
      if (token === this.#token) this.#loading = false;
    }
  }

  /** @param {any} track @param {Date} now */
  #row(track, now) {
    const row = el('li', 'tm-row');
    row.dataset.trackId = String(track.id);
    const b = el('button', 'tm-row-button');
    b.type = 'button';
    b.title = track.path;
    if (this.#track?.id === track.id) b.classList.add('active');
    const text = el('span', 'tm-row-text');
    text.append(el('span', 'tm-row-title', track.title || '(sense títol)'), el('span', 'tm-row-artist', track.artist || ''));
    const badges = el('span', 'tm-row-badges');
    for (const badge of stateBadges(track)) badges.append(badge);
    const played = el('span', 'tm-row-played', playedAgo(track.lastPlayed, now));
    played.title = track.playCount ? `Ha sonat ${track.playCount} ${track.playCount === 1 ? 'vegada' : 'vegades'}` : 'No ha sonat mai';
    b.append(text, badges, played, el('span', 'tm-row-duration', formatDuration(track.durationMs)));
    b.addEventListener('click', () => this.#select(track.id));
    row.append(b);
    return row;
  }

  /** Torna a dibuixar la fila d'una pista (després de desar-la), sense tornar a cercar. @param {any} track */
  #updateRow(track) {
    const old = $('tm-list').querySelector(`[data-track-id="${track.id}"]`);
    if (old) old.replaceWith(this.#row(track, new Date()));
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
    for (const b of $('tm-list').querySelectorAll('.tm-row-button')) {
      b.classList.toggle('active', /** @type {HTMLElement} */ (b.parentElement).dataset.trackId === String(id));
    }
    this.#renderDetail();
  }

  // --- Fitxa ---

  #renderDetail() {
    const box = $('tm-detail');
    const t = this.#track;
    if (!t) {
      this.#readForm = null;
      box.replaceChildren(el('p', 'tm-empty', 'Tria una pista de la llista per veure\'n i editar-ne la fitxa.'));
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
    const tags = text((t.tags ?? []).join(', '), 1000);
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
      field('Etiquetes', tags, 'Separades per comes, p. ex. «català, 80s».'),
      pair(field('Sona des del dia', validFrom), field('Fins al dia', validUntil)),
      el('div', 'form-text mt-n1 mb-2', 'Fora d\'aquestes dates, l\'AutoDJ no la tria. Buit = sense límit.'),
      enabled.wrap, variable.wrap,
      field('Notes', notes),
    );

    this.#readForm = () => ({
      artist: artist.value, title: title.value, album: album.value, year: year.value, type: type.value,
      subcategoryId: subcategory.value === '' ? null : Number(subcategory.value), tags: parseTags(tags.value),
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
    if (t.fileState !== 'missing') {
      const edit = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-link btn-sm p-0 align-baseline'));
      edit.type = 'button';
      edit.textContent = 'Edita…';
      edit.title = 'Editor de punts: forma d\'ona, inici, mescla i final';
      edit.addEventListener('click', () => this.#editCues());
      cueBox.append(edit);
    }
    row('Punts', cueBox);
    row('Ha sonat', t.playCount ? `${t.playCount} ${t.playCount === 1 ? 'vegada' : 'vegades'}, l'última ${playedAgo(t.lastPlayed, new Date())}` : 'mai');
    if ((t.usedBy ?? []).length > 0) {
      const list = el('ul', 'tm-used');
      list.append(...t.usedBy.map((/** @type {any} */ e) => el('li', '', e.name)));
      row('Esdeveniments', list);
    }
    return box;
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
