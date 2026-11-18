// @ts-check
// Editor d'esdeveniments (fase 2, pas 8b), al panell de la dreta del planificador. Edita una còpia (l'esborrany) i no
// desa res fins que es clica «Desa»; el motor valida i és qui té l'última paraula (scheduler.save).
// Les 10 pròximes emissions es demanen al motor mentre s'edita (scheduler.preview): no es calculen aquí.

import { el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { formatDuration, typeName } from '../format.js';
import { MODES, modeInfo, parseDate, shortDate, shortTime, daysText, recurrenceText } from '../schedule.js';
import {
  newEvent, cloneEvent, eventToSave, sameEvent, duplicateEvent, normalizeTime, minutesToSeconds, secondsToMinutes,
  intOrNull, numberOrNull, parseHours, formatHours, parseMonthDays, formatMonthDays, setDay, moveItem, withMode, quickCheck,
  initialSections, optionsSummary, unreachableDaysText, todayIso, trackDatesState,
} from '../event-form.js';
import { sweeperOptions, fillSweeperSelect } from '../subcategories.js';

const DAY_NAMES = ['dl', 'dt', 'dc', 'dj', 'dv', 'ds', 'dg'];
const RECURRENCES = [
  ['SETMANAL', 'Cada setmana'],
  ['CADA_N_SETMANES', 'Cada N setmanes'],
  ['MENSUAL_ORDINAL', 'Un dia de la setmana de cada mes (p. ex. primer dissabte)'],
  ['DIES_MES', 'Uns dies del mes (p. ex. 1 i 15)'],
  ['DATES', 'Només unes dates soltes'],
];
const REPEATS = [['CAP', 'Una vegada al dia'], ['CADA_HORA', 'Cada hora'], ['CADA_N_MIN', 'Cada N minuts']];
const ORDINALS = [[1, '1r'], [2, '2n'], [3, '3r'], [4, '4t'], [5, '5è'], [-1, 'últim']];
const PREVIEW_DELAY_MS = 400;

let nextId = 1;
/** Un id únic per lligar cada etiqueta amb el seu camp. */
const uid = () => `ee-${nextId++}`;

/**
 * @typedef {{ onSaved: (ev: any) => void, onDeleted: (id: number) => void, onClosed: () => void, onOpenCampaign?: (campaignId: number) => void }} EditorCallbacks
 */

export class EventEditor {
  #panel;
  #send;
  #callbacks;
  /** L'esdeveniment tal com és al motor (null si és nou). @type {any} */
  #original = null;
  /** La còpia que s'edita (null = no hi ha res obert). @type {any} */
  #draft = null;
  /** @type {any[]} */
  #rotations = [];
  /** Col·leccions de sweepers per a l'acció «Sweepers». @type {import('../subcategories.js').SubcategoryOption[]} */
  #sweeperOptions = [];
  /** @type {{ auxiliary: number, sweepers: number } | null} */
  #ducking = null;
  /** @type {string | null} */
  #error = null;
  /** El tipus d'acció triat al desplegable «Afegeix» (es recorda). */
  #addType = 'PISTA';
  #previewTimer = 0;
  #previewToken = 0;
  #saving = false;
  /** Les seccions obertes (redisseny: replegables). @type {Set<string>} */
  #openSections = new Set();
  /** El resum de «Pròximes emissions» (la primera), per veure-la amb la secció tancada. */
  #nextSummary = '';
  /** Filtres del cercador de pistes de les accions. */
  #searchCategory = '';
  #searchSubcategory = '';
  /** S'està afegint la primera excepció (la subsecció és oberta). */
  #addingException = false;

  /**
   * @param {HTMLElement} panel
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {EditorCallbacks} callbacks
   */
  constructor(panel, send, callbacks) {
    this.#panel = panel;
    this.#send = send;
    this.#callbacks = callbacks;
    document.addEventListener('click', (e) => {
      if (!(/** @type {HTMLElement} */ (e.target).closest?.('.ee-more'))) this.#closeMenu();
    });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.#closeMenu(); });
    this.#render();
  }

  /** L'id de l'esdeveniment obert (0 = un de nou; null = cap). */
  get eventId() {
    return this.#draft ? this.#draft.id : null;
  }

  /** Hi ha canvis sense desar. */
  get dirty() {
    if (!this.#draft) return false;
    return this.#original ? !sameEvent(this.#draft, this.#original) : true;
  }

  /** Les rotacions, per a les accions «Rotació». @param {any[]} rotations */
  setRotations(rotations) {
    this.#rotations = rotations;
  }

  /** Les categories de la biblioteca (library.filters), per a les accions «Sweepers» (fase 3, pas 5b) i «Bossa» (pas 8c). @param {any[]} categories */
  setCategories(categories) {
    this.#sweeperOptions = sweeperOptions(categories);
    this.#categories = categories;
  }

  /** Les bosses amb nom (bags.list), per a les accions «Bossa» (fase 3, pas 8c; D-096). @param {any[]} bags */
  setBags(bags) {
    this.#bags = bags;
  }

  /** @type {any[]} */
  #categories = [];
  /** @type {any[]} */
  #bags = [];

  /** L'atenuació per defecte (ducking.get, D-078): es mostra quan el camp és buit. @param {{ auxiliary: number, sweepers: number } | null} ducking */
  setDucking(ducking) {
    this.#ducking = ducking;
  }

  /** Obre un esdeveniment existent (o tanca l'editor amb null). No pregunta res: qui el crida mira abans {@link dirty}. @param {any | null} ev */
  open(ev) {
    // El mateix esdeveniment (p. ex. després de desar-lo): es mantenen les seccions obertes
    if (!ev || ev.id !== this.#draft?.id || this.#draft?.id === 0) this.#openSections = ev ? initialSections(ev, false) : new Set();
    this.#addingException = false;
    this.#original = ev ? cloneEvent(ev) : null;
    this.#draft = ev ? cloneEvent(ev) : null;
    this.#error = null;
    this.#render();
  }

  /** Obre un esdeveniment nou. @param {object} [values]  valors que substitueixen els per defecte */
  create(values = {}) {
    this.#original = null;
    this.#draft = { ...newEvent(), ...values };
    this.#openSections = initialSections(this.#draft, true);
    this.#addingException = false;
    this.#error = null;
    this.#render();
  }

  /**
   * L'esdeveniment ha canviat al motor (p. ex. des d'un altre lloc). Si no hi ha canvis sense desar, es mostra el nou.
   * @param {any | undefined} ev  undefined = ja no existeix
   */
  refresh(ev) {
    if (!this.#draft || this.#draft.id === 0 || this.dirty) return;
    if (!ev) this.open(null);
    else if (!sameEvent(ev, this.#original)) this.open(ev);
  }

  /** Si hi ha canvis sense desar, demana si es descarten. Retorna true si es pot continuar. */
  async confirmDiscard() {
    if (!this.dirty) return true;
    return confirmAction({
      title: 'Canvis sense desar',
      message: `«${this.#draft.name || 'Esdeveniment nou'}» té canvis que no s'han desat. Si continues, es perdran.`,
      confirm: 'Descarta els canvis',
    });
  }

  // --- Canvis a l'esborrany ---

  /** Canvi d'un camp que no canvia l'estructura del formulari: només s'actualitzen els botons i la previsualització. */
  #changed() {
    this.#error = null;
    this.#syncButtons();
    this.#showDaysWarning();
    this.#schedulePreview();
  }

  /** L'avís dels dies que no s'executaran per les dates d'inici i de fi (D-109). */
  #showDaysWarning() {
    const box = this.#panel.querySelector('#ee-days-warning');
    if (!box || !this.#draft) return;
    const text = unreachableDaysText(this.#draft);
    box.classList.toggle('d-none', !text);
    box.replaceChildren(...(text ? [statusLabel('text-warning small', 'bi-exclamation-triangle-fill', text)] : []));
  }

  /**
   * Els dies que no s'emet (excepcions; D-109): si no n'hi ha cap, només un botó per afegir-ne; si n'hi ha, una
   * subsecció replegable amb quantes són.
   * @param {any} ev
   */
  #exceptions(ev) {
    const list = () => this.#dateList('', ev.excludedDates, (dates) => { ev.excludedDates = dates; }, todayIso());
    if (ev.excludedDates.length === 0 && !this.#addingException) {
      const b = el('button', 'btn btn-sm btn-link ee-link');
      b.type = 'button';
      b.append(icon('bi-plus-lg'), ' ', el('span', '', 'Afegeix un dia que no s\'emet (excepció)'));
      b.addEventListener('click', () => {
        this.#addingException = true;
        this.#restructured();
      });
      const wrap = el('div', 'ee-field');
      wrap.append(b);
      return wrap;
    }
    const box = /** @type {HTMLDetailsElement} */ (el('details', 'ee-sub'));
    box.open = this.#addingException;
    box.addEventListener('toggle', () => { this.#addingException = box.open; });
    const n = ev.excludedDates.length;
    box.append(el('summary', 'ee-sub-title', n === 0 ? 'Dies que no s\'emet' : `Dies que no s'emet (${n} ${n === 1 ? 'excepció' : 'excepcions'})`), list());
    return box;
  }

  /** Canvi que fa aparèixer o desaparèixer camps (mode, recurrència, accions…): es torna a dibuixar tot. */
  #restructured() {
    this.#error = null;
    this.#render();
  }

  /** Tanca el menú «Més» de la botonera, si és obert. */
  #closeMenu() {
    const menu = this.#panel.querySelector('.ee-menu');
    if (!menu || menu.classList.contains('d-none')) return;
    menu.classList.add('d-none');
    this.#panel.querySelector('.ee-more > button')?.setAttribute('aria-expanded', 'false');
  }

  /** Obre una secció (sense tornar a dibuixar res). @param {string} key */
  #reveal(key) {
    this.#openSections.add(key);
    const box = /** @type {HTMLDetailsElement | null} */ (this.#panel.querySelector(`details[data-section="${key}"]`));
    if (box) box.open = true;
  }

  // --- Dibuix ---

  /**
   * Una secció amb requadre i títol, replegable. Tancada, el títol porta un resum d'una línia.
   * En un esdeveniment nou, omplir-la obre la següent (next), seguint el flux normal.
   * @param {string} key  @param {string} title  @param {() => string} summary
   * @param {string | null} next  @param {HTMLElement[]} content
   */
  #section(key, title, summary, next, content) {
    const box = /** @type {HTMLDetailsElement} */ (el('details', 'ee-box'));
    box.dataset.section = key;
    box.open = this.#openSections.has(key);
    const head = el('summary', 'ee-box-title');
    const resum = el('span', 'ee-box-summary', summary());
    resum.dataset.summary = key;
    head.append(el('span', 'ee-box-name', title), resum);
    box.addEventListener('toggle', () => {
      if (box.open) this.#openSections.add(key);
      else {
        this.#openSections.delete(key);
        resum.textContent = summary(); // pot haver canviat mentre era oberta
      }
    });
    if (next) {
      const advance = () => { if (this.#original === null && this.#draft) this.#reveal(next); };
      box.addEventListener('input', advance);
      box.addEventListener('change', advance);
    }
    const body = el('div', 'ee-box-body');
    body.append(...content);
    box.append(head, body);
    return box;
  }

  /** El mode, en un desplegable, amb el que fa a sota. @param {any} ev */
  #modeSelect(ev) {
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    select.id = uid();
    for (const [mode, m] of Object.entries(MODES)) {
      const option = /** @type {HTMLOptionElement} */ (el('option', '', m.label));
      option.value = mode;
      select.append(option);
    }
    select.value = ev.mode;
    select.addEventListener('change', () => {
      this.#draft = withMode(ev, select.value);
      this.#restructured();
    });
    const info = modeInfo(ev.mode);
    const badge = el('span', `pl-chip ${info.css}`);
    badge.append(icon(info.icon), ' ', el('span', '', info.label));
    const row = el('div', 'ee-mode-row');
    row.append(select, badge);
    const box = this.#labelled('Mode', row, MODE_HELP[/** @type {keyof typeof MODE_HELP} */ (ev.mode)]);
    const label = /** @type {HTMLLabelElement | null} */ (box.querySelector('label'));
    if (label) label.htmlFor = select.id;
    return box;
  }

  #render() {
    const panel = this.#panel;
    const scroll = panel.scrollTop;
    panel.replaceChildren();
    const ev = this.#draft;
    // Sense cap esdeveniment obert, la columna s'amaga i la graella ocupa tot l'ample (D-115)
    panel.classList.toggle('is-empty', !ev);
    if (!ev) return;

    // A dalt de tot: activat i, a la dreta, «Desa», «Més» i «Tanca». El mode és a «General», al costat del desplegable (D-149)
    const head = el('div', 'ee-head');
    head.append(this.#switch('Activat', ev.enabled, (on) => { ev.enabled = on; }));
    const close = el('button', 'btn btn-sm btn-outline-secondary');
    close.type = 'button';
    close.title = 'Tanca l\'editor';
    close.append(icon('bi-x-lg'), ' ', el('span', '', 'Tanca'));
    close.addEventListener('click', async () => {
      if (!(await this.confirmDiscard())) return;
      this.open(null);
      this.#callbacks.onClosed();
    });
    // «Desa» i «Més» a dalt, al costat de «Tanca», com a la fitxa de la pista (D-149)
    const save = el('button', 'btn btn-sm btn-primary');
    save.id = 'ee-save';
    save.type = 'button';
    save.append(icon('bi-check-lg'), ' ', el('span', '', 'Desa'));
    save.addEventListener('click', () => this.#save());

    // La resta d'opcions, en un menú (redisseny, D-109): descarta els canvis, duplica i esborra
    const more = el('div', 'ee-more');
    const toggle = el('button', 'btn btn-sm btn-outline-secondary');
    toggle.type = 'button';
    toggle.setAttribute('aria-haspopup', 'menu');
    toggle.setAttribute('aria-expanded', 'false');
    toggle.append(icon('bi-three-dots'), ' ', el('span', '', 'Més'));
    const menu = el('div', 'ee-menu cp-menu d-none'); // s'obre cap avall, alineat a la dreta
    menu.setAttribute('role', 'menu');
    const item = (/** @type {string} */ iconName, /** @type {string} */ text, /** @type {() => void} */ run, danger = false) => {
      const b = el('button', `ee-menu-item${danger ? ' text-danger' : ''}`);
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
    const discard = item('bi-arrow-counterclockwise', 'Descarta els canvis', () => {
      if (this.#original) this.open(this.#original);
      else {
        this.open(null);
        this.#callbacks.onClosed();
      }
    });
    discard.id = 'ee-discard';
    if (ev.id !== 0) {
      item('bi-copy', 'Duplica', async () => {
        if (!(await this.confirmDiscard())) return;
        this.#original = null;
        this.#draft = duplicateEvent(ev);
        this.#openSections = initialSections(this.#draft, false);
        this.#restructured();
      });
      item('bi-trash', 'Esborra…', () => this.#delete(), true);
    }
    toggle.addEventListener('click', () => {
      const open = menu.classList.contains('d-none');
      menu.classList.toggle('d-none', !open);
      toggle.setAttribute('aria-expanded', String(open));
      if (open) /** @type {HTMLElement | null} */ (menu.querySelector('.ee-menu-item:not(.d-none)'))?.focus();
    });
    more.append(toggle, menu);
    const buttons = el('div', 'ee-head-buttons');
    buttons.append(save, more, close);
    head.append(buttons);

    // Avisos (pas 8c): els calcula el motor amb l'esborrany, alhora que les pròximes emissions
    const warnings = el('div', 'ee-warnings d-none');
    warnings.id = 'ee-warnings';
    warnings.setAttribute('role', 'status');

    // La capçalera (amb els botons) i els avisos queden sempre visibles a dalt
    const top = el('div', 'ee-top');
    // L'error de validació en desar («Cal posar-li un nom»…): al mateix lloc que els avisos, sempre visible
    const error = el('div', 'alert alert-danger d-flex gap-2 m-0 d-none');
    error.id = 'ee-error';
    error.setAttribute('role', 'alert');
    error.append(icon('bi-exclamation-octagon-fill'), el('span', ''));
    top.append(head, error, warnings);
    panel.append(top);

    // General: nom i mode
    /** @type {HTMLElement[]} */
    const general = [
      this.#textField('Nom', ev.name, (v) => { ev.name = v; }, { placeholder: 'p. ex. El Magazín de la Comarca' }),
      this.#modeSelect(ev),
    ];
    if (ev.notes) general.push(el('p', 'pl-notes', ev.notes));
    panel.append(this.#section('general', 'General', () => [ev.name || 'sense nom', modeInfo(ev.mode).label].join(' · '), 'when', general));

    // Quan
    /** @type {HTMLElement[]} */
    const when = [];
    const whenRow = el('div', 'ee-row');
    whenRow.append(
      this.#field('Hora d\'inici', 'time', ev.startTime, (v) => { ev.startTime = normalizeTime(v) || ev.startTime; }, { step: '1', required: 'true' }),
      this.#textField('Durada màxima (min)', secondsToMinutes(ev.maxDurationSeconds), (v) => { ev.maxDurationSeconds = minutesToSeconds(v); },
        { inputmode: 'decimal', placeholder: 'sense' }),
    );
    when.push(whenRow);
    when.push(this.#select('Recurrència', RECURRENCES, ev.recurrence, (v) => {
      ev.recurrence = v;
      if (v === 'CADA_N_SETMANES') ev.intervalWeeks ??= 2;
      if (v === 'MENSUAL_ORDINAL' && ev.ordinals.length === 0) ev.ordinals = [1];
      this.#restructured();
    }));
    this.#recurrenceFields(when, ev);
    if (ev.recurrence !== 'DATES') {
      const datesRow = el('div', 'ee-row');
      datesRow.append(
        this.#field('Des del dia (opcional)', 'date', ev.startDate ?? '', (v) => { ev.startDate = v || null; }),
        this.#field('Fins al dia (opcional)', 'date', ev.endDate ?? '', (v) => { ev.endDate = v || null; }),
      );
      // Avís (D-109): dies triats que no s'executaran mai per les dates d'inici i de fi. S'actualitza mentre s'escriu
      const daysWarning = el('div', 'ee-days-warning d-none');
      daysWarning.id = 'ee-days-warning';
      daysWarning.setAttribute('role', 'status');
      when.push(datesRow, daysWarning, this.#exceptions(ev));
    }
    when.push(this.#select('Repetició dins del dia', REPEATS, ev.repeat, (v) => {
      ev.repeat = v;
      if (v === 'CADA_N_MIN') ev.intervalMinutes ??= 30;
      this.#restructured();
    }));
    if (ev.repeat === 'CADA_HORA') {
      when.push(this.#textField('Quines hores (buit = totes)', formatHours(ev.hoursMask), (v) => {
        const mask = parseHours(v);
        if (mask !== undefined) ev.hoursMask = mask;
      }, { placeholder: 'p. ex. 7-23 o 9, 12, 18' }, `Sona cada hora al minut ${ev.startTime.slice(3, 5)} i segon ${ev.startTime.slice(6, 8)} de l'hora d'inici.`));
    }
    if (ev.repeat === 'CADA_N_MIN') {
      when.push(this.#textField('Cada quants minuts', String(ev.intervalMinutes ?? ''), (v) => { ev.intervalMinutes = intOrNull(v); },
        { inputmode: 'numeric' }, 'A partir de l\'hora d\'inici i fins al final del dia.'));
    }
    panel.append(this.#section('when', 'Quan', () => `${shortTime(ev.startTime)} · ${recurrenceText(ev)}`, 'actions', when));

    // Opcions del mode
    /** @type {HTMLElement[]} */
    const options = [];
    if (ev.mode === 'TALLA' || ev.mode === 'ENCUA') {
      options.push(this.#switch('Esborra la cua abans de posar-hi el contingut', ev.clearQueue, (on) => { ev.clearQueue = on; }));
    }
    if (ev.mode === 'TALLA') {
      options.push(this.#textField('Fos de la pista que sonava (ms)', ev.fadeMs == null ? '' : String(ev.fadeMs), (v) => { ev.fadeMs = intOrNull(v); },
        { inputmode: 'numeric', placeholder: '1000' }, '0 = tall sec. Buit = 1.000 ms.'));
    }
    if (ev.mode === 'SUPERPOSA' || ev.actions.some((/** @type {any} */ a) => a.type === 'AUXILIAR')) {
      // Buit = la per defecte de la configuració (D-078); 0 = sense atenuació
      const standard = this.#ducking ? dbText(this.#ducking.auxiliary) : null;
      options.push(this.#textField('Atenuació de la música mentre sona l\'auxiliar (dB)', ev.duckingDb == null ? '' : String(ev.duckingDb).replace('.', ','),
        (v) => { ev.duckingDb = numberOrNull(v); }, { inputmode: 'decimal', placeholder: standard ? `per defecte, ${standard}` : 'per defecte' },
        `Negatiu: −12 vol dir 12 dB més fluix. Buit = la per defecte${standard ? ` (${standard})` : ''}. 0 = no s'atenua.`));
    }
    const advanced = el('div', 'ee-row');
    advanced.append(
      this.#textField('Prioritat', String(ev.priority), (v) => { ev.priority = intOrNull(v) ?? 50; }, { inputmode: 'numeric' }),
      this.#textField('Tolerància (s)', String(ev.toleranceSeconds), (v) => { ev.toleranceSeconds = intOrNull(v) ?? 60; }, { inputmode: 'numeric' }),
    );
    options.push(advanced, el('div', 'form-text',
      'Si dos Talla o Encua coincideixen, sona el de prioritat més alta. Si s\'hi arriba més tard que la tolerància, s\'omet.'));
    panel.append(this.#section('options', 'Opcions', () => optionsSummary(ev), null, options));

    // Accions (en ordre d'emissió)
    panel.append(this.#section('actions', 'Accions', () => actionsSummary(ev.actions), null, [this.#actionList(ev), this.#addActions(ev)]));

    // Pròximes emissions (les calcula el motor amb l'esborrany): replegada per defecte
    const preview = el('div', 'pl-next');
    preview.id = 'ee-preview';
    panel.append(this.#section('next', 'Pròximes emissions', () => this.#nextSummary, null, [preview]));


    panel.scrollTop = scroll;
    this.#showError();
    this.#syncButtons();
    this.#showDaysWarning();
    this.#schedulePreview(0);
  }

  /** Els camps que depenen de la recurrència. @param {HTMLElement[]} into  on s'afegeixen @param {any} ev */
  #recurrenceFields(into, ev) {
    const days = () => this.#days('Dies de la setmana', ev.daysMask, (mask) => { ev.daysMask = mask; });
    switch (ev.recurrence) {
      case 'CADA_N_SETMANES': {
        const row = el('div', 'ee-row');
        row.append(
          this.#textField('Cada quantes setmanes', String(ev.intervalWeeks ?? ''), (v) => { ev.intervalWeeks = intOrNull(v); }, { inputmode: 'numeric' }),
          this.#field('Un dia en què segur que toca', 'date', ev.anchorDate ?? '', (v) => { ev.anchorDate = v || null; }),
        );
        into.push(days(), row);
        break;
      }
      case 'MENSUAL_ORDINAL': {
        const group = el('div', 'ee-checks');
        for (const [value, label] of ORDINALS) {
          group.append(this.#check(String(label), ev.ordinals.includes(value), (on) => {
            ev.ordinals = on ? [...new Set([...ev.ordinals, value])] : ev.ordinals.filter((/** @type {number} */ o) => o !== value);
          }));
        }
        into.push(this.#labelled('Quin del mes', group), days());
        break;
      }
      case 'DIES_MES':
        into.push(this.#textField('Dies del mes', formatMonthDays(ev.monthDays), (v) => {
          const list = parseMonthDays(v);
          if (list !== undefined) ev.monthDays = list;
        }, { placeholder: 'p. ex. 1, 15, últim' }));
        break;
      case 'DATES':
        into.push(this.#dateList('Dates en què sona', ev.includedDates, (dates) => { ev.includedDates = dates; }));
        break;
      default:
        into.push(days());
    }
  }

  /** La llista d'accions, amb els botons per pujar, baixar i treure. @param {any} ev */
  #actionList(ev) {
    const list = el('ol', 'pl-actions');
    if (ev.actions.length === 0) list.append(el('li', 'pl-action-empty', 'Encara no hi ha cap acció.'));
    const today = todayIso();
    ev.actions.forEach((/** @type {any} */ action, /** @type {number} */ index) => {
      // Una pista fora de les seves dates avui no sona (D-110): es marca com la que no té fitxer
      const dates = action.type === 'PISTA' || action.type === 'AUXILIAR' ? trackDatesState(action, today) : null;
      const silent = dates?.kind === 'ended' || dates?.kind === 'notYet';
      const li = el('li', `pl-action${action.missing || silent ? ' is-missing' : ''}`);
      const top = el('div', 'ee-action-top');
      const text = el('div', 'ee-action-text');
      text.append(el('div', 'pl-action-label', action.label ?? ACTION_NAMES[action.type] ?? action.type),
        el('div', 'pl-action-detail', [ACTION_NAMES[action.type] ?? action.type, action.detail].filter(Boolean).join(' · ')));
      const tools = el('div', 'ee-action-tools');
      const tool = (/** @type {string} */ iconName, /** @type {string} */ title, /** @type {() => void} */ run, disabled = false) => {
        const b = el('button', 'btn btn-sm btn-outline-secondary');
        b.type = 'button';
        b.title = title;
        b.setAttribute('aria-label', title);
        b.disabled = disabled;
        b.append(icon(iconName));
        b.addEventListener('click', run);
        tools.append(b);
      };
      // Una campanya: anar-hi directament (D-137); si l'esdeveniment té canvis sense desar, la pestanya Campanyes no els toca
      const campaignId = action.type === 'CAMPANYA' ? Number(action.params?.campanyaId) : NaN;
      if (Number.isFinite(campaignId) && this.#callbacks.onOpenCampaign) {
        const open = this.#callbacks.onOpenCampaign;
        tool('bi-box-arrow-up-right', 'Obre la campanya', () => open(campaignId));
      }
      tool('bi-arrow-up', 'Puja (sona abans)', () => { ev.actions = moveItem(ev.actions, index, -1); this.#restructured(); }, index === 0);
      tool('bi-arrow-down', 'Baixa (sona després)', () => { ev.actions = moveItem(ev.actions, index, 1); this.#restructured(); }, index === ev.actions.length - 1);
      tool('bi-x-lg', 'Treu aquesta acció', () => { ev.actions = ev.actions.filter((/** @type {any} */ a) => a !== action); this.#restructured(); });
      top.append(text, tools);
      li.append(top);

      if (action.missing) {
        const warning = el('div', 'pl-action-warning');
        warning.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', action.type === 'CAMPANYA'
          ? 'No sonarà: la campanya no existeix, no és activa o no té falques' : 'No sonarà: la pista o el fitxer falta'));
        li.append(warning);
      }
      if (dates) {
        const note = el('div', silent ? 'pl-action-warning' : 'pl-action-note');
        note.append(icon(silent ? 'bi-calendar-x' : 'bi-calendar-event'), ' ', el('span', '', dates.text));
        li.append(note);
      }
      this.#actionFields(li, ev, action);
      list.append(li);
    });
    return list;
  }

  /** Els camps propis de cada tipus d'acció. @param {HTMLElement} li @param {any} ev @param {any} action */
  #actionFields(li, ev, action) {
    const p = action.params;
    if (action.type === 'AUXILIAR') {
      const row = el('div', 'ee-row');
      row.append(
        this.#select('Reproductor auxiliar', [['1', '1'], ['2', '2'], ['3', '3']], String(p.player ?? 1), (v) => { p.player = Number(v); this.#changed(); }),
        this.#textField('Atenuació pròpia (dB)', p.duckingDb == null ? '' : String(p.duckingDb).replace('.', ','), (v) => {
          const db = numberOrNull(v);
          if (db == null) delete p.duckingDb;
          else p.duckingDb = db;
        }, { inputmode: 'decimal', placeholder: 'la de l\'esdeveniment' }),
      );
      li.append(row);
    }
    if (action.type === 'ROTACIO') {
      const options = this.#rotations.map((r) => [String(r.id), r.name]);
      if (!options.some(([id]) => id === String(p.rotacioId))) options.unshift([String(p.rotacioId ?? ''), '(tria una rotació)']);
      li.append(this.#select('Rotació de l\'AutoDJ', options, String(p.rotacioId ?? ''), (v) => {
        p.rotacioId = Number(v);
        action.label = `Rotació ${this.#rotations.find((r) => r.id === p.rotacioId)?.name ?? ''}`;
        this.#restructured();
      }));
    }
    if (action.type === 'SWEEPERS') {
      // Categoria de sweepers del trepitjador (fase 3, pas 5b; D-084), com «Set Sweeper Category» de RadioDJ
      const id = uid();
      const wrap = el('div', 'mb-2');
      const label = /** @type {HTMLLabelElement} */ (el('label', 'form-label', 'Categoria de sweepers'));
      label.htmlFor = id;
      const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
      select.id = id;
      fillSweeperSelect(select, this.#sweeperOptions, p.subcategoriaId == null ? '(tria una subcategoria)' : null);
      if (p.subcategoriaId != null && ![...select.options].some((o) => o.value === String(p.subcategoriaId))) {
        const unknown = document.createElement('option');
        unknown.value = String(p.subcategoriaId);
        unknown.textContent = action.label ?? `Subcategoria ${p.subcategoriaId}`;
        select.prepend(unknown);
      }
      select.value = p.subcategoriaId == null ? '' : String(p.subcategoriaId);
      select.addEventListener('change', () => {
        p.subcategoriaId = select.value === '' ? null : Number(select.value);
        action.label = select.selectedOptions[0]?.textContent ?? 'Sweepers';
        this.#restructured();
      });
      const standard = this.#ducking ? ` La música baixa ${dbText(this.#ducking.sweepers)} mentre sonen (per defecte, a la configuració).` : '';
      wrap.append(label, select, el('div', 'form-text',
        `A partir d'aquí, els passos de rotació amb trepitjador fan sonar un sweeper d'aquesta categoria a sobre de l'inici de la cançó.${standard}`));
      li.append(wrap);
    }
    if (action.type === 'BOSSA') {
      // Bossa amb nom (fase 3, pas 8c; D-096): p. ex. el dia 1 de cada mes, «Generalista A» passa a Adult 6
      const label = () => {
        const bag = this.#bags.find((b) => b.id === p.bossaId);
        const sub = this.#categories.flatMap((c) => c.subcategories.map((/** @type {any} */ s) => ({ ...s, category: c.name })))
          .find((s) => s.id === p.subcategoriaId);
        return `Bossa «${bag?.name ?? '?'}» → ${sub ? `${sub.category} / ${sub.name}` : '?'}`;
      };
      const bags = this.#bags.map((b) => /** @type {[string, string]} */ ([String(b.id), `${b.name} (ara: ${b.subcategory ?? 'cap'})`]));
      li.append(this.#select('Bossa', [['', '(tria la bossa)'], ...bags], String(p.bossaId ?? ''), (v) => {
        p.bossaId = v === '' ? null : Number(v);
        action.label = label();
        this.#restructured();
      }));
      const subs = this.#categories.flatMap((c) => c.subcategories.map((/** @type {any} */ s) => /** @type {[string, string]} */ ([String(s.id), `${c.name} / ${s.name}`])));
      li.append(this.#select('Passa a ser', [['', '(tria la subcategoria)'], ...subs], String(p.subcategoriaId ?? ''), (v) => {
        p.subcategoriaId = v === '' ? null : Number(v);
        action.label = label();
        this.#restructured();
      }));
      li.append(el('div', 'form-text', 'Els passos de rotació que apunten a aquesta bossa triaran d\'aquesta subcategoria a partir de la cançó següent.'));
    }
    if (action.type === 'AUTODJ') {
      li.append(this.#select('Què fa', [['true', 'Activa l\'AutoDJ'], ['false', 'Atura l\'AutoDJ']], String(p.actiu !== false), (v) => {
        p.actiu = v === 'true';
        action.label = p.actiu ? 'Activa l\'AutoDJ' : 'Atura l\'AutoDJ';
        this.#restructured();
      }));
    }
    if (action.type === 'TITOL') {
      // Títol fix per a BUTT (D-184): el text i quant dura, des de l'hora de l'esdeveniment
      li.append(
        this.#textField('Text per a BUTT', p.text ?? '', (v) => {
          p.text = v.trim();
          action.label = p.text ? `Títol per a BUTT «${p.text}»` : 'Títol per a BUTT';
        }, { maxlength: '200', placeholder: 'P. ex. Esport i Punt' }),
        this.#textField('Durada (min)', secondsToMinutes(p.durada_seg), (v) => {
          const seconds = minutesToSeconds(v);
          if (seconds == null) delete p.durada_seg;
          else p.durada_seg = seconds;
        }, { inputmode: 'decimal' }, 'Des de l\'hora de l\'esdeveniment, BUTT envia aquest text i no els títols del que sona (d\'1 minut a 24 hores). Després torna sol el títol del que sona.'),
      );
    }
    if (action.type === 'STREAM' || action.type === 'ENTRADA') {
      // Una entrada de línia (D-185) sona com un stream: les mateixes opcions, sense les que només són de la xarxa
      const isInput = action.type === 'ENTRADA';
      if (isInput) {
        li.append(this.#inputSelect(action, p), el('div', 'form-text',
          'Ha de ser una entrada on arribi el programa de fora (un receptor, un altre ordinador…), no la que va a BUTT: aquella ja porta el so d\'aquest ordinador i faria un bucle.'));
      }
      if (!isInput && p.songId == null) {
        // Esdeveniments d'abans (D-108): l'adreça escrita a mà. Els nous trien el stream de la biblioteca
        li.append(this.#textField('Adreça del stream', p.url ?? '', (v) => { p.url = v.trim(); action.label = p.url; }, { placeholder: 'http://…' }));
      }
      li.append(
        this.#textField('Durada (min)', secondsToMinutes(p.durada_seg), (v) => {
          const seconds = minutesToSeconds(v);
          if (seconds == null) delete p.durada_seg;
          else p.durada_seg = seconds;
        }, { inputmode: 'decimal' }, 'Quan s\'acaba, baixa amb un fos i continua la resta de l\'esdeveniment.'),
      );
      // Opcions (fase 2, pas 7c; D-030, D-178)
      const setNumber = (/** @type {string} */ name, /** @type {string} */ v, /** @type {number} */ factor = 1) => {
        const n = Number(v.trim().replace(',', '.').replace('−', '-'));
        if (v.trim() === '' || !Number.isFinite(n)) delete p[name];
        else p[name] = Math.round(n * factor);
      };
      if (!isInput) {
        li.append(this.#check('Comença pel directe', p.directe !== false, (on) => {
          if (on) delete p.directe;
          else p.directe = false;
        }), el('div', 'form-text', 'Sense els segons que el servidor envia de cop en connectar-s\'hi: sona el més recent, amb el mínim retard.'));
      }
      li.append(this.#textField('Fos final (s)', p.fade_ms == null ? '' : String(p.fade_ms / 1000).replace('.', ','), (v) => setNumber('fade_ms', v, 1000),
        { inputmode: 'decimal', placeholder: '2' }, 'Buit: 2 s.'));
      li.append(this.#textField('S\'acaba si arriba en silenci (s)', p.silenci_seg == null ? '' : String(p.silenci_seg), (v) => setNumber('silenci_seg', v),
        { inputmode: 'numeric', placeholder: 'no' }, 'Per als butlletins que acaben abans d\'hora o que arriben en silenci. Buit: no s\'acaba per silenci (hi ha programes amb pauses llargues).'));
      if (!isInput) {
        li.append(this.#textField('Retard de connexió (s)', p.retard_seg == null ? '' : String(p.retard_seg), (v) => setNumber('retard_seg', v),
          { inputmode: 'numeric', placeholder: '0' }, ev.mode === 'TALLA'
            ? 'El tall es fa aquests segons més tard, perquè el que sona quadri amb l\'origen (que arriba amb retard).'
            : 'Només en un esdeveniment TALLA (a l\'hora exacta).'));
      }
      // Volum de l'entrada (D-188): un guany fix i, si cal, l'anivellador automàtic
      if (isInput) {
        const db = (/** @type {number | undefined} */ v) => (v == null ? '' : String(v).replace('.', ',').replace('-', '−'));
        li.append(this.#textField('Guany (dB)', db(p.guany_db), (v) => setNumber('guany_db', v),
          { inputmode: 'decimal', placeholder: '0' }, 'Per apujar (+) o abaixar (−) l\'entrada, de −20 a +20 dB. El valor el pots mesurar amb prova-entrada (consola, Diagnòstic → b), amb la font sonant com sempre: diu el guany recomanat. Buit: tal com arriba.'));
        li.append(this.#check('Anivella el volum (AGC)', p.agc === true, (on) => {
          if (on) p.agc = true;
          else {
            delete p.agc;
            delete p.agc_objectiu;
            delete p.agc_max_db;
          }
          this.#restructured();
        }), el('div', 'form-text', 'Iguala el volum mentre sona, a poc a poc (les pauses no les apuja). Per a les fonts que arriben unes vegades més fluixes i altres més fortes. L\'entrada sona 1 s més tard que l\'origen (ha de mirar endavant).'));
        if (p.agc === true) {
          li.append(this.#textField('Objectiu (LUFS)', db(p.agc_objectiu), (v) => setNumber('agc_objectiu', v),
            { inputmode: 'decimal', placeholder: '−13' }, 'El volum al qual es porta, de −30 a −6. Buit: −13 (el de la música, mesurat a la sortida).'));
          li.append(this.#textField('Com a molt, apuja (dB)', db(p.agc_max_db), (v) => setNumber('agc_max_db', v),
            { inputmode: 'decimal', placeholder: '12' }, 'Perquè no apugi de més una font molt fluixa (i el seu soroll), de 0 a 20. Buit: 12.'));
        }
      }
      // Reconnexió durant tota la durada (D-031, D-180): per als programes llargs
      li.append(this.#check('Si cau, es reconnecta (programes llargs)', p.reconnecta === true, (on) => {
        if (on) p.reconnecta = true;
        else {
          delete p.reconnecta;
          delete p.tornada;
        }
        this.#restructured();
      }), el('div', 'form-text', p.reconnecta === true
        ? (isInput
          ? 'Si es desconnecta o no s\'obre a l\'hora, sona música de la rotació mentre es prova de tornar-la a obrir; l\'entrada torna quan funciona. La resta de l\'esdeveniment espera a l\'hora prevista de final.'
          : 'Si cau o no connecta a l\'hora, sona música de la rotació mentre es prova de reconnectar; el stream torna quan funciona. La resta de l\'esdeveniment espera a l\'hora prevista de final.')
        : 'Si cau i no es recupera en uns segons, passa a la resta de l\'esdeveniment (per als butlletins curts).'));
      if (p.reconnecta === true) {
        li.append(this.#select('Quan torna', [['seguida', 'De seguida (la música baixa amb un fos)'], ['canco', 'En acabar la cançó que sona']],
          p.tornada === 'canco' ? 'canco' : 'seguida', (v) => {
            if (v === 'canco') p.tornada = 'canco';
            else delete p.tornada;
          }));
      }
      // Títol per a BUTT (D-182): el que envia el servidor del stream, mentre sona
      if (!isInput) {
        li.append(this.#check('Envia el títol del stream a BUTT', p.titol === true, (on) => {
          if (on) p.titol = true;
          else delete p.titol;
        }), el('div', 'form-text', 'Mentre sona, el títol que envia el servidor va a NowPlaying.txt. No l\'activis en un stream que retransmet la mateixa emissió d\'Ona Bages: el títol seria el que ja hi ha escrit.'));
      }
      // Pista de reserva: si el stream no connecta, sona aquesta en el seu lloc (si no n'hi ha, se salta)
      const reserveName = action.reserveLabel ?? (/reserva «(.+?)»/.exec(action.detail ?? '')?.[1]) ?? (p.reserva_songId != null ? `Pista ${p.reserva_songId}` : null);
      const reserve = el('div', 'ee-reserve');
      const choose = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      choose.type = 'button';
      choose.append(icon('bi-life-preserver'), ' ', el('span', '', reserveName ? 'Canvia la reserva…' : 'Tria una pista de reserva…'));
      choose.addEventListener('click', () => this.#pickTrack('PISTA', (track, label) => {
        p.reserva_songId = track.id;
        action.reserveLabel = label;
        this.#restructured();
      }));
      reserve.append(el('span', 'form-label mb-0', reserveName ? `Pista de reserva: «${reserveName}»` : p.reconnecta === true
        ? `Sense pista de reserva: si no ${isInput ? 's\'obre' : 'connecta'}, sona música de la rotació.`
        : `Sense pista de reserva: si no ${isInput ? 's\'obre' : 'connecta'}, se salta.`), choose);
      if (reserveName) {
        const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link'));
        remove.type = 'button';
        remove.append(icon('bi-x-lg'), ' ', el('span', '', 'Treu-la'));
        remove.addEventListener('click', () => {
          delete p.reserva_songId;
          delete action.reserveLabel;
          this.#restructured();
        });
        reserve.append(remove);
      }
      li.append(reserve);
    }

    // Només alguns dies dels de l'esdeveniment (D-028)
    if (ev.recurrence === 'SETMANAL' || ev.recurrence === 'CADA_N_SETMANES') {
      const limited = action.daysMask != null;
      li.append(this.#check('Només alguns dies', limited, (on) => {
        action.daysMask = on ? ev.daysMask : null;
        this.#restructured();
      }));
      if (limited) li.append(this.#days('', action.daysMask, (mask) => { action.daysMask = mask; }, ev.daysMask));
    }
  }

  /** Les entrades de Windows (devices.list), demanades una sola vegada mentre l'editor és obert. @type {Promise<any[]> | null} */
  #inputs = null;

  /**
   * El desplegable de l'entrada d'una acció ENTRADA (D-185): les entrades actives de Windows (les desactivades o sense res
   * connectat, també, però sense poder-les triar). Si la desada ja no hi és, es manté i es diu.
   * @param {any} action  @param {any} p  els paràmetres de l'acció
   */
  #inputSelect(action, p) {
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    select.append(option('', 'Carregant les entrades…'));
    select.disabled = true;
    this.#inputs ??= this.#send('devices.list', { kind: 'input', includeInactive: true })
      .then((/** @type {any} */ r) => r.devices ?? [])
      .catch(() => { this.#inputs = null; return []; });
    this.#inputs.then((devices) => {
      const saved = p.dispositiuId ?? p.dispositiu ?? '';
      const chosen = devices.find((d) => d.id === p.dispositiuId) ?? devices.find((d) => d.name === p.dispositiu);
      const items = [option('', devices.length ? 'Tria l\'entrada…' : 'Windows no té cap entrada')];
      for (const d of devices) {
        const o = option(d.id, d.status === 'active' ? d.name : `${d.name} (${d.status === 'disabled' ? 'desactivada' : 'sense res connectat'})`);
        o.disabled = d.status !== 'active' && d !== chosen;
        items.push(o);
      }
      if (saved && !chosen) items.push(option(saved, `${p.dispositiu ?? saved} (no hi és ara)`));
      select.replaceChildren(...items);
      select.value = chosen?.id ?? saved;
      select.disabled = false;
    });
    select.addEventListener('change', async () => {
      const devices = await (this.#inputs ?? Promise.resolve([]));
      const d = devices.find((x) => x.id === select.value);
      if (!d) return;
      p.dispositiuId = d.id;
      p.dispositiu = d.name;
      action.label = `Entrada «${d.name}»`;
      action.missing = false;
      this.#changed();
    });
    return this.#labelled('Entrada', select);
  }

  /** Per afegir una acció: un desplegable amb el tipus i el botó «Afegeix». @param {any} ev */
  #addActions(ev) {
    const box = el('div', 'ee-add');
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    select.id = uid();
    select.setAttribute('aria-label', 'Tipus d\'acció per afegir');
    for (const [type, text] of ADD_TYPES) select.append(option(type, text));
    select.value = this.#addType;
    const help = el('div', 'form-text', ADD_HELP[/** @type {keyof typeof ADD_HELP} */ (select.value)] ?? '');
    select.addEventListener('change', () => {
      this.#addType = select.value;
      help.textContent = ADD_HELP[/** @type {keyof typeof ADD_HELP} */ (select.value)] ?? '';
    });
    const button = el('button', 'btn btn-sm btn-outline-secondary');
    button.type = 'button';
    button.append(icon('bi-plus-lg'), ' ', el('span', '', 'Afegeix'));
    button.addEventListener('click', () => this.#addAction(ev, select.value));
    const row = el('div', 'ee-add-row');
    row.append(select, button);
    box.append(row, help);
    return box;
  }

  /** Afegeix una acció del tipus triat; les que demanen triar una pista o una categoria obren una finestra. @param {any} ev @param {string} type */
  #addAction(ev, type) {
    const push = (/** @type {any} */ action) => {
      ev.actions = [...ev.actions, { daysMask: null, ...action }];
      this.#restructured();
    };
    switch (type) {
      case 'PISTA':
      case 'AUXILIAR':
      case 'STREAM':
        this.#pickTrack(type, (track, label, detail) => {
          // Un stream triat com a pista es converteix en acció STREAM, que porta la durada (D-108)
          if (track.type === 'stream' && type !== 'AUXILIAR') {
            push({ type: 'STREAM', params: { songId: track.id }, label, detail: 'Stream · cal posar-hi la durada' });
            return;
          }
          push({
            type, params: type === 'AUXILIAR' ? { songId: track.id, player: 1 } : { songId: track.id }, label, detail, missing: track.fileState === 'missing',
            validFrom: track.validFrom ?? null, validUntil: track.validUntil ?? null,
          });
        });
        break;
      case 'CAMPANYA':
        this.#pickCampaign((c) => push({ type, params: { campanyaId: c.id }, label: `Campanya «${c.name}»`, detail: campaignDetail(c), missing: c.effectiveState !== 'ACTIVA' || c.spots.length === 0 }));
        break;
      case 'SWEEPERS':
        this.#pickSweepers((sub) => push({ type, params: { subcategoriaId: sub.id }, label: sub.label }));
        break;
      case 'ROTACIO': {
        const first = this.#rotations[0];
        push({ type, params: { rotacioId: first?.id ?? null }, label: first ? `Rotació ${first.name}` : 'Rotació' });
        break;
      }
      case 'AUTODJ':
        push({ type, params: { actiu: true }, label: 'Activa l\'AutoDJ' });
        break;
      case 'ENTRADA':
        // L'entrada es tria al desplegable de l'acció (D-185)
        push({ type, params: { durada_seg: 3600 }, label: 'Entrada (tria-la)', detail: 'Entrada · 1 h 00 min', missing: true });
        break;
      case 'TITOL':
        push({ type, params: { text: '', durada_seg: 3600 }, label: 'Títol per a BUTT', detail: '1 h 00 min' });
        break;
      case 'BOSSA': {
        const first = this.#bags[0];
        push({ type, params: { bossaId: first?.id ?? null, subcategoriaId: null }, label: first ? `Bossa «${first.name}»` : 'Bossa' });
        break;
      }
    }
  }

  /**
   * Una finestra modal (element <dialog>) amb títol, el contingut i «Cancel·la». Es treu de la pàgina en tancar-se.
   * @param {string} iconName  @param {string} title  @param {HTMLElement[]} content
   * @returns {HTMLDialogElement}
   */
  #modal(iconName, title, content) {
    const dialog = /** @type {HTMLDialogElement} */ (el('dialog', 'ob-dialog ob-dialog-wide ee-pick'));
    const form = el('form', '');
    form.setAttribute('method', 'dialog');
    const h = el('h2', 'h5');
    h.append(icon(iconName), ' ', el('span', '', title));
    const buttons = el('div', 'd-flex justify-content-end mt-3');
    const cancel = el('button', 'btn btn-outline-secondary');
    cancel.setAttribute('value', 'cancel');
    cancel.append(el('span', '', 'Cancel·la'));
    buttons.append(cancel);
    form.append(h, ...content, buttons);
    dialog.append(form);
    dialog.addEventListener('close', () => dialog.remove());
    document.body.append(dialog);
    dialog.showModal();
    return dialog;
  }

  /**
   * Finestra per triar una pista (library.search), amb filtres de categoria i subcategoria. En clicar-ne una, es tria.
   * Per a un STREAM, només els streams de la biblioteca (tipus Stream, p. ex. la categoria «Radio Streams»; D-108).
   * @param {'PISTA' | 'AUXILIAR' | 'STREAM'} kind
   * @param {(track: any, label: string, detail: string) => void} onPick
   */
  #pickTrack(kind, onPick) {
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    input.type = 'search';
    input.placeholder = kind === 'AUXILIAR' ? 'Cerca la pista que sonarà per sobre…'
      : kind === 'STREAM' ? 'Cerca un stream per nom o adreça…' : 'Cerca una pista per títol, artista o carpeta…';
    input.setAttribute('aria-label', 'Cerca una pista');
    const results = el('div', 'ee-results');
    let timer = 0;
    let token = 0;

    // Filtres per categoria i subcategoria (com a la Biblioteca); es recorden mentre l'editor és obert
    const category = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    category.setAttribute('aria-label', 'Categoria');
    category.title = 'Categoria (sense triar: totes)';
    const subcategory = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    subcategory.setAttribute('aria-label', 'Subcategoria');
    subcategory.title = 'Subcategoria (sense triar: totes)';
    const withTracks = (/** @type {any[]} */ list) => list.filter((x) => x.count > 0);
    category.replaceChildren(option('', 'Categoria'), ...withTracks(this.#categories).map((c) => option(String(c.id), `${c.name} (${c.count})`)));
    category.value = this.#searchCategory;
    const fillSubcategories = () => {
      const chosen = this.#categories.find((c) => String(c.id) === category.value);
      const subs = chosen ? chosen.subcategories : this.#categories.flatMap((c) => c.subcategories);
      subcategory.replaceChildren(option('', 'Subcategoria'), ...withTracks(subs).map((s) => option(String(s.id), `${s.name} (${s.count})`)));
      subcategory.value = this.#searchSubcategory;
      this.#searchSubcategory = subcategory.value; // si ja no hi és, «Subcategoria»
    };
    fillSubcategories();
    const filters = el('div', 'ee-search-filters');
    filters.append(category, subcategory);
    const content = kind === 'STREAM'
      ? [el('p', 'ob-dialog-message', 'Els streams de la biblioteca (pistes de tipus Stream). Després hi hauràs de posar la durada.'), input, results]
      : [input, filters, results];
    const dialog = this.#modal(kind === 'AUXILIAR' ? 'bi-layers' : kind === 'STREAM' ? 'bi-broadcast' : 'bi-music-note-beamed',
      kind === 'AUXILIAR' ? 'Afegeix un auxiliar' : kind === 'STREAM' ? 'Afegeix un stream' : 'Afegeix una pista', content);

    const search = async () => {
      const mine = ++token;
      /** @type {Record<string, any>} */
      const params = { text: input.value, limit: 50 };
      if (kind === 'STREAM') params.type = 'stream';
      else if (subcategory.value) params.subcategoryId = Number(subcategory.value);
      else if (category.value) params.categoryId = Number(category.value);
      try {
        const result = await this.#send('library.search', params);
        if (mine !== token) return;
        results.replaceChildren();
        if (result.tracks.length === 0) results.append(el('div', 'text-body-secondary', 'Cap pista amb aquests criteris.'));
        for (const track of result.tracks) {
          const row = el('button', `ee-result${track.fileState === 'missing' ? ' is-missing' : ''}`);
          row.type = 'button';
          const label = track.artist ? `${track.artist} - ${track.title}` : track.title;
          const detail = [typeName(track.type), track.durationMs ? formatDuration(track.durationMs) : '',
            track.fileState === 'missing' ? 'el fitxer falta' : ''].filter(Boolean).join(' · ');
          row.append(el('span', 'ee-result-label', label), el('span', 'ee-result-detail', detail));
          row.title = track.path;
          row.addEventListener('click', () => {
            dialog.close();
            onPick(track, label, detail);
          });
          results.append(row);
        }
        if (result.more) results.append(el('div', 'text-body-secondary', 'N\'hi ha més: escriu més text o filtra per categoria.'));
      } catch (err) {
        if (mine === token) results.replaceChildren(el('div', 'text-danger', `No s'ha pogut cercar: ${/** @type {Error} */ (err).message}`));
      }
    };
    category.addEventListener('change', () => {
      this.#searchCategory = category.value;
      fillSubcategories();
      search();
    });
    subcategory.addEventListener('change', () => {
      this.#searchSubcategory = subcategory.value;
      search();
    });
    input.addEventListener('input', () => {
      clearTimeout(timer);
      timer = window.setTimeout(search, 250);
    });
    // Enter al cercador no ha de tancar la finestra (el formulari és de diàleg)
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') e.preventDefault(); });
    search();
    input.focus();
  }

  /** Finestra per triar una campanya (campaigns.list): primer les actives, després els esborranys. @param {(c: any) => void} onPick */
  async #pickCampaign(onPick) {
    const list = el('div', 'ee-results');
    const dialog = this.#modal('bi-megaphone', 'Afegeix una campanya', [
      el('p', 'ob-dialog-message', 'A cada passi sonarà la falca que toca segons el pla de la campanya. Les campanyes es fan a la pestanya Campanyes.'), list]);
    list.append(el('div', 'text-body-secondary', 'Carregant…'));
    try {
      const { campaigns } = await this.#send('campaigns.list');
      const order = { ACTIVA: 0, ESBORRANY: 1, ACABADA: 2 };
      const shown = campaigns.filter((/** @type {any} */ c) => c.effectiveState !== 'ACABADA')
        .sort((/** @type {any} */ a, /** @type {any} */ b) => order[/** @type {keyof typeof order} */ (a.effectiveState)] - order[/** @type {keyof typeof order} */ (b.effectiveState)]);
      list.replaceChildren();
      if (shown.length === 0) list.append(el('div', 'text-body-secondary', 'No hi ha cap campanya activa ni cap esborrany. Crea-la primer a la pestanya Campanyes.'));
      for (const c of shown) {
        const row = el('button', `ee-result${c.effectiveState !== 'ACTIVA' ? ' is-missing' : ''}`);
        row.type = 'button';
        row.append(el('span', 'ee-result-label', c.name), el('span', 'ee-result-detail', campaignDetail(c)));
        row.addEventListener('click', () => {
          dialog.close();
          onPick(c);
        });
        list.append(row);
      }
    } catch (err) {
      list.replaceChildren(el('div', 'text-danger', `No s'han pogut llegir les campanyes: ${/** @type {Error} */ (err).message}`));
    }
  }

  /** Finestra per triar la categoria de sweepers. @param {(sub: { id: number, label: string }) => void} onPick */
  #pickSweepers(onPick) {
    const list = el('div', 'ee-results');
    if (this.#sweeperOptions.length === 0) list.append(el('div', 'text-body-secondary', 'No hi ha cap subcategoria amb pistes.'));
    const dialog = this.#modal('bi-soundwave', 'Afegeix un canvi de sweepers', [
      el('p', 'ob-dialog-message', 'Tria la categoria de sweepers del trepitjador (sonen a sobre de l\'inici de les cançons).'), list]);
    for (const sub of this.#sweeperOptions) {
      const row = el('button', 'ee-result');
      row.type = 'button';
      row.append(el('span', 'ee-result-label', sub.label));
      row.addEventListener('click', () => {
        dialog.close();
        onPick(sub);
      });
      list.append(row);
    }
  }

  // --- Peces del formulari ---

  /** Un camp amb l'etiqueta a sobre. @param {string} label @param {HTMLElement} control @param {string} [help] */
  #labelled(label, control, help) {
    const box = el('div', 'ee-field');
    if (label) {
      const l = /** @type {HTMLLabelElement} */ (el('label', 'form-label', label));
      if (control.id) l.htmlFor = control.id;
      box.append(l);
    }
    box.append(control);
    if (help) box.append(el('div', 'form-text', help));
    return box;
  }

  /**
   * @param {string} label @param {string} type @param {string} value @param {(value: string) => void} onInput
   * @param {Record<string, string>} [attributes] @param {string} [help]
   */
  #field(label, type, value, onInput, attributes = {}, help) {
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    input.id = uid();
    input.type = type;
    input.value = value;
    for (const [name, v] of Object.entries(attributes)) input.setAttribute(name, v);
    input.addEventListener('input', () => {
      onInput(input.value);
      this.#changed();
    });
    return this.#labelled(label, input, help);
  }

  /** @param {string} label @param {string} value @param {(value: string) => void} onInput @param {Record<string, string>} [attributes] @param {string} [help] */
  #textField(label, value, onInput, attributes = {}, help) {
    return this.#field(label, 'text', value, onInput, attributes, help);
  }

  /** @param {string} label @param {string[][]} options  [valor, text] @param {string} value @param {(value: string) => void} onChange */
  #select(label, options, value, onChange) {
    const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    select.id = uid();
    for (const [optionValue, text] of options) {
      const option = /** @type {HTMLOptionElement} */ (el('option', '', text));
      option.value = optionValue;
      select.append(option);
    }
    select.value = value;
    select.addEventListener('change', () => onChange(select.value));
    return this.#labelled(label, select);
  }

  /** @param {string} label @param {boolean} checked @param {(on: boolean) => void} onChange */
  #check(label, checked, onChange) {
    const box = el('div', 'form-check form-check-inline');
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    input.type = 'checkbox';
    input.id = uid();
    input.checked = checked;
    input.addEventListener('change', () => {
      onChange(input.checked);
      this.#changed();
    });
    const l = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', label));
    l.htmlFor = input.id;
    box.append(input, l);
    return box;
  }

  /** @param {string} label @param {boolean} checked @param {(on: boolean) => void} onChange */
  #switch(label, checked, onChange) {
    const box = this.#check(label, checked, onChange);
    box.className = 'form-check form-switch ee-field';
    box.querySelector('input')?.setAttribute('role', 'switch');
    return box;
  }

  /**
   * Set caselles, de dilluns a diumenge.
   * @param {string} label @param {number} mask @param {(mask: number) => void} onChange
   * @param {number} [allowed]  només aquests dies es poden marcar (els de l'esdeveniment, per a una acció)
   */
  #days(label, mask, onChange, allowed = 127) {
    const group = el('div', 'ee-checks');
    let current = mask;
    for (let day = 0; day < 7; day++) {
      if ((allowed & (1 << day)) === 0) continue;
      group.append(this.#check(DAY_NAMES[day], (current & (1 << day)) !== 0, (on) => {
        current = setDay(current, day, on);
        onChange(current);
      }));
    }
    return this.#labelled(label, group);
  }

  /**
   * Una llista de dates: un camp per afegir-ne i una etiqueta per cada una, amb el botó per treure-la.
   * @param {string} label @param {string[]} dates @param {(dates: string[]) => void} onChange
   * @param {string} [notBefore]  'AAAA-MM-DD': no es deixa afegir cap data anterior (p. ex. avui, per a les excepcions)
   */
  #dateList(label, dates, onChange, notBefore) {
    const box = el('div', 'ee-datelist');
    const chips = el('div', 'ee-chips');
    for (const date of [...dates].sort()) {
      const chip = el('span', 'ee-chip');
      const d = parseDate(date);
      const remove = el('button', 'ee-chip-remove');
      remove.type = 'button';
      remove.title = `Treu el ${shortDate(date)}`;
      remove.setAttribute('aria-label', remove.title);
      remove.append(icon('bi-x'));
      remove.addEventListener('click', () => {
        onChange(dates.filter((x) => x !== date));
        this.#restructured();
      });
      chip.append(el('span', '', `${DAY_NAMES[(d.getDay() + 6) % 7]} ${shortDate(date)}`), remove);
      chips.append(chip);
    }
    const row = el('div', 'ee-datelist-add');
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    input.type = 'date';
    input.setAttribute('aria-label', `${label}: data per afegir`);
    if (notBefore) input.min = notBefore;
    const problem = el('div', 'form-text text-danger d-none');
    const add = el('button', 'btn btn-sm btn-outline-secondary');
    add.type = 'button';
    add.append(icon('bi-plus-lg'), ' ', el('span', '', 'Afegeix'));
    add.addEventListener('click', () => {
      if (!input.value || dates.includes(input.value)) return;
      if (notBefore && input.value < notBefore) {
        problem.textContent = 'No es pot posar una data que ja ha passat.';
        problem.classList.remove('d-none');
        return;
      }
      onChange([...dates, input.value].sort());
      this.#restructured();
    });
    input.addEventListener('input', () => problem.classList.add('d-none'));
    row.append(input, add);
    box.append(chips, row, problem);
    return this.#labelled(label, box);
  }

  // --- Estat dels botons, errors i previsualització ---

  #syncButtons() {
    const save = /** @type {HTMLButtonElement | null} */ (this.#panel.querySelector('#ee-save'));
    const discard = /** @type {HTMLButtonElement | null} */ (this.#panel.querySelector('#ee-discard'));
    if (!save || !discard) return;
    const dirty = this.dirty;
    save.disabled = !dirty || this.#saving;
    discard.disabled = !dirty || this.#saving;
    discard.classList.toggle('d-none', !dirty);
    const more = this.#panel.querySelector('.ee-more');
    more?.classList.toggle('d-none', !more.querySelector('.ee-menu-item:not(.d-none)'));
    if (this.#error === null) this.#showError();
  }

  #showError() {
    const box = this.#panel.querySelector('#ee-error');
    if (!box) return;
    box.classList.toggle('d-none', !this.#error);
    const text = box.querySelector('span');
    if (text) text.textContent = this.#error ?? '';
  }

  /** Demana al motor les pròximes emissions de l'esborrany, una mica després de l'últim canvi. */
  #schedulePreview(delay = PREVIEW_DELAY_MS) {
    clearTimeout(this.#previewTimer);
    this.#previewTimer = window.setTimeout(() => this.#preview(), delay);
  }

  async #preview() {
    const box = this.#panel.querySelector('#ee-preview');
    if (!box || !this.#draft) return;
    const token = ++this.#previewToken;
    const draft = eventToSave(this.#draft);
    if (!draft.name) draft.name = 'Esdeveniment nou'; // el nom no canvia quan sona
    try {
      const result = await this.#send('scheduler.preview', { event: draft, limit: 10 });
      if (token !== this.#previewToken) return;
      this.#showWarnings(result.warnings ?? []);
      box.replaceChildren();
      if (!this.#draft.enabled) box.append(statusLabel('text-warning', 'bi-pause-circle-fill', 'Desactivat: si s\'activés, sonaria'));
      if (result.occurrences.length === 0) box.append(el('span', 'text-body-secondary', 'Cap en els pròxims 400 dies.'));
      const first = result.occurrences.find((/** @type {any} */ o) => !o.skippedClockChange);
      this.#setNextSummary(first ? `la pròxima, ${occurrenceText(first)}` : 'cap en els pròxims 400 dies');
      for (const o of result.occurrences) {
        const chip = el('span', `pl-next-chip${o.skippedClockChange ? ' is-skipped' : ''}`, occurrenceText(o));
        if (o.skippedClockChange) {
          chip.prepend(icon('bi-clock-history'), ' ');
          chip.title = 'Aquesta hora no existeix pel canvi d\'hora: no sonarà';
        }
        box.append(chip);
      }
    } catch (err) {
      if (token !== this.#previewToken) return;
      const warning = el('span', 'text-warning');
      warning.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', /** @type {Error} */ (err).message));
      box.replaceChildren(warning);
    }
  }

  /** @param {string} text */
  #setNextSummary(text) {
    this.#nextSummary = text;
    const span = this.#panel.querySelector('[data-summary="next"]');
    if (span) span.textContent = text;
  }

  /**
   * Avisos del motor: què passarà els pròxims dies (programa que es tallarà, Encua que no sonarà, fitxers que falten…).
   * @param {{ text: string, dates: string[] }[]} warnings
   */
  #showWarnings(warnings) {
    const box = this.#panel.querySelector('#ee-warnings');
    if (!box) return;
    box.replaceChildren();
    box.classList.toggle('d-none', warnings.length === 0);
    if (warnings.length === 0) return;
    const title = el('div', 'ee-warnings-title');
    title.append(icon('bi-exclamation-triangle-fill'), ' ',
      el('span', '', warnings.length === 1 ? 'Un avís' : `${warnings.length} avisos`),
      el('span', 'ee-warnings-note', ' · els pròxims 14 dies, sense comptar la cançó que soni'));
    const list = el('ul', 'ee-warnings-list');
    for (const w of warnings) {
      const li = el('li', '', w.text);
      if (w.dates.length > 0) {
        const days = w.dates.slice(0, 4).map((d) => {
          const date = parseDate(d);
          return `${DAY_NAMES[(date.getDay() + 6) % 7]} ${date.getDate()}/${date.getMonth() + 1}`;
        });
        if (w.dates.length > 4) days.push(`i ${w.dates.length - 4} més`);
        li.append(el('span', 'ee-warnings-dates', ` (${days.join(', ')})`));
      }
      list.append(li);
    }
    box.append(title, list);
  }

  // --- Desar i esborrar ---

  async #save() {
    if (!this.#draft || this.#saving) return;
    const problem = quickCheck(this.#draft);
    if (problem) {
      this.#error = problem;
      this.#showError();
      return;
    }
    this.#saving = true;
    this.#syncButtons();
    try {
      const result = await this.#send('scheduler.save', { event: eventToSave(this.#draft) });
      this.#saving = false;
      this.open(result.event);
      this.#callbacks.onSaved(result.event);
    } catch (err) {
      this.#saving = false;
      this.#error = `No s'ha desat: ${/** @type {Error} */ (err).message}`;
      this.#showError();
      this.#syncButtons();
    }
  }

  async #delete() {
    const ev = this.#original;
    if (!ev) return;
    const ok = await confirmAction({
      title: 'Esborrar l\'esdeveniment',
      message: `«${ev.name}» (${shortTime(ev.startTime)}, ${daysText(ev.daysMask)}) deixarà de sonar i desapareixerà de la graella.\n` +
        'El registre del que ja ha sonat es conserva. No es pot desfer.',
      confirm: 'Esborra l\'esdeveniment',
    });
    if (!ok) return;
    try {
      await this.#send('scheduler.delete', { eventId: ev.id });
      this.open(null);
      this.#callbacks.onDeleted(ev.id);
    } catch (err) {
      this.#error = `No s'ha esborrat: ${/** @type {Error} */ (err).message}`;
      this.#showError();
    }
  }
}

/** Què fa cada mode, en una frase (docs/planificador.md, secció 2). */
const MODE_HELP = {
  TALLA: 'Talla el que sona i el contingut comença a l\'hora exacta.',
  ENCUA: 'Posa el contingut com a següent a sonar; la pista que sona continua.',
  SUPERPOSA: 'Sona per sobre del que soni (senyals horaris), sense tocar la cua.',
  SISTEMA: 'Només accions sense àudio (canviar la rotació, l\'AutoDJ…).',
};

/** «−10 dB», o «0 dB (sense atenuació)». @param {number} db */
function dbText(db) {
  return db === 0 ? '0 dB (sense atenuació)' : `${String(db).replace('.', ',').replace('-', '−')} dB`;
}

/** «dl 16/11 09:00». @param {{ date: string, time: string }} o */
function occurrenceText(o) {
  const date = parseDate(o.date);
  return `${DAY_NAMES[(date.getDay() + 6) % 7]} ${date.getDate()}/${date.getMonth() + 1} ${shortTime(o.time)}`;
}

/** Resum de la secció «Accions»: quantes n'hi ha i la primera. @param {any[]} actions */
function actionsSummary(actions) {
  if (actions.length === 0) return 'cap';
  const first = actions[0].label ?? ACTION_NAMES[/** @type {keyof typeof ACTION_NAMES} */ (actions[0].type)] ?? actions[0].type;
  return actions.length === 1 ? first : `${actions.length} accions: ${first}…`;
}

/** «3 falques · del 1/11/2026 al 30/11/2026 · activa» (com el motor, ActionDescriber). @param {any} c */
function campaignDetail(c) {
  const dmy = (/** @type {string} */ iso) => iso.split('-').reverse().map((x, i) => (i < 2 ? String(Number(x)) : x)).join('/');
  const state = c.effectiveState === 'ACTIVA' ? (c.spots.length === 0 ? 'sense falques: no sona' : 'activa')
    : c.effectiveState === 'ESBORRANY' ? 'esborrany: no sona' : 'acabada: no sona';
  return `${c.spots.length === 1 ? '1 falca' : `${c.spots.length} falques`} · del ${dmy(c.startDate)} al ${dmy(c.endDate)} · ${state}`;
}

/** El desplegable «Afegeix» de les accions. */
const ADD_TYPES = [
  ['PISTA', 'Pista'], ['CAMPANYA', 'Campanya'], ['AUXILIAR', 'Auxiliar'], ['ROTACIO', 'Rotació'], ['AUTODJ', 'AutoDJ'],
  ['SWEEPERS', 'Sweepers'], ['BOSSA', 'Bossa'], ['TITOL', 'Títol per a BUTT'], ['ENTRADA', 'Entrada de línia'], // els streams, com a pista (D-109)
];

/** Què fa cada tipus d'acció, en una frase (a sota del desplegable). */
const ADD_HELP = {
  PISTA: "Una pista de la biblioteca, a la cua. Un stream (p. ex. de «Radio Streams») també s'hi afegeix així, i porta la durada.",
  CAMPANYA: "Una campanya de falques: a cada passi sona la falca que toca segons el seu pla (pestanya Campanyes).",
  AUXILIAR: 'Una pista que sona per sobre del que soni (senyals horaris).',
  ROTACIO: 'Canvia la rotació de l\'AutoDJ.',
  AUTODJ: 'Activa o atura l\'AutoDJ.',
  SWEEPERS: 'Canvia la categoria de sweepers del trepitjador (sonen a sobre de l\'inici de les cançons).',
  BOSSA: 'Canvia la subcategoria d\'una bossa amb nom (p. ex. el dia 1 de cada mes).',
  ENTRADA: 'Una entrada de línia de Windows (un receptor, un altre ordinador…) com a font del programa: sona com un stream, amb la durada.',
  TITOL: 'Un text fix per a BUTT durant una estona (p. ex. el nom d\'un directe que no surt de la cua). Normalment, en un esdeveniment SISTEMA.',
};

const ACTION_NAMES = {
  PISTA: 'Pista', AUXILIAR: 'Auxiliar', ROTACIO: 'Rotació', AUTODJ: 'AutoDJ', STREAM: 'Stream', SWEEPERS: 'Sweepers', BOSSA: 'Bossa',
  CAMPANYA: 'Campanya', TITOL: 'Títol per a BUTT', ENTRADA: 'Entrada', PLANTILLA: 'Plantilla', ESPERA: 'Espera', ATURA: 'Atura', ENTRADA: 'Entrada', EXECUTA: 'Executa', REPRODUEIX: 'Reprodueix',
};

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
