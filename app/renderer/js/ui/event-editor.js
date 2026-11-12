// @ts-check
// Editor d'esdeveniments (fase 2, pas 8b), al panell de la dreta del planificador. Edita una còpia (l'esborrany) i no
// desa res fins que es clica «Desa»; el motor valida i és qui té l'última paraula (scheduler.save).
// Les 10 pròximes emissions es demanen al motor mentre s'edita (scheduler.preview): no es calculen aquí.

import { el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { formatDuration, typeName } from '../format.js';
import { MODES, modeInfo, parseDate, shortDate, shortTime, daysText } from '../schedule.js';
import {
  newEvent, cloneEvent, eventToSave, sameEvent, duplicateEvent, normalizeTime, minutesToSeconds, secondsToMinutes,
  intOrNull, numberOrNull, parseHours, formatHours, parseMonthDays, formatMonthDays, setDay, moveItem, withMode, quickCheck,
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
 * @typedef {{ onSaved: (ev: any) => void, onDeleted: (id: number) => void, onClosed: () => void }} EditorCallbacks
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
  /** Quin cercador de pistes és obert: per afegir una PISTA o un AUXILIAR. @type {'PISTA' | 'AUXILIAR' | null} */
  #searchFor = null;
  #previewTimer = 0;
  #previewToken = 0;
  #saving = false;

  /**
   * @param {HTMLElement} panel
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {EditorCallbacks} callbacks
   */
  constructor(panel, send, callbacks) {
    this.#panel = panel;
    this.#send = send;
    this.#callbacks = callbacks;
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
    this.#original = ev ? cloneEvent(ev) : null;
    this.#draft = ev ? cloneEvent(ev) : null;
    this.#error = null;
    this.#searchFor = null;
    this.#render();
  }

  /** Obre un esdeveniment nou. @param {object} [values]  valors que substitueixen els per defecte */
  create(values = {}) {
    this.#original = null;
    this.#draft = { ...newEvent(), ...values };
    this.#error = null;
    this.#searchFor = null;
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
    this.#schedulePreview();
  }

  /** Canvi que fa aparèixer o desaparèixer camps (mode, recurrència, accions…): es torna a dibuixar tot. */
  #restructured() {
    this.#error = null;
    this.#render();
  }

  // --- Dibuix ---

  #render() {
    const panel = this.#panel;
    const scroll = panel.scrollTop;
    panel.replaceChildren();
    const ev = this.#draft;
    if (!ev) {
      panel.append(el('p', 'pl-detail-empty', 'Tria un esdeveniment de la graella per veure\'l i editar-lo, o crea\'n un de nou.'));
      return;
    }
    const info = modeInfo(ev.mode);

    // Capçalera: què és i en quin estat
    const head = el('div', 'd-flex flex-wrap align-items-center gap-2 mb-2');
    const badge = el('span', `pl-chip ${info.css}`);
    badge.append(icon(info.icon), ' ', el('span', '', info.label));
    head.append(badge, el('strong', '', ev.id === 0 ? 'Esdeveniment nou' : `Esdeveniment ${ev.id}`));
    const close = el('button', 'btn btn-sm btn-outline-secondary ms-auto');
    close.type = 'button';
    close.title = 'Tanca l\'editor';
    close.append(icon('bi-x-lg'), ' ', el('span', '', 'Tanca'));
    close.addEventListener('click', async () => {
      if (!(await this.confirmDiscard())) return;
      this.open(null);
      this.#callbacks.onClosed();
    });
    head.append(close);
    panel.append(head);

    panel.append(this.#textField('Nom', ev.name, (v) => { ev.name = v; }, { placeholder: 'p. ex. El Magazín de la Comarca' }));

    // Mode: quatre botons amb color, icona i text
    const modes = el('div', 'ee-modes');
    modes.setAttribute('role', 'group');
    modes.setAttribute('aria-label', 'Mode');
    for (const [mode, m] of Object.entries(MODES)) {
      const button = el('button', `ee-mode ${m.css}${ev.mode === mode ? ' is-on' : ''}`);
      button.type = 'button';
      button.setAttribute('aria-pressed', String(ev.mode === mode));
      button.append(icon(m.icon), ' ', el('span', '', m.label));
      button.addEventListener('click', () => {
        if (ev.mode === mode) return;
        this.#draft = withMode(ev, mode);
        this.#restructured();
      });
      modes.append(button);
    }
    panel.append(this.#labelled('Mode', modes, MODE_HELP[ev.mode]));

    panel.append(this.#switch('Activat (si no, no es dispara)', ev.enabled, (on) => { ev.enabled = on; }));

    // Avisos (pas 8c): els calcula el motor amb l'esborrany, alhora que les pròximes emissions
    const warnings = el('div', 'ee-warnings d-none');
    warnings.id = 'ee-warnings';
    warnings.setAttribute('role', 'status');
    panel.append(warnings);

    // Quan
    panel.append(el('h3', 'ee-section', 'Quan'));
    const whenRow = el('div', 'ee-row');
    whenRow.append(
      this.#field('Hora d\'inici', 'time', ev.startTime, (v) => { ev.startTime = normalizeTime(v) || ev.startTime; }, { step: '1', required: 'true' }),
      this.#textField('Durada màxima (min)', secondsToMinutes(ev.maxDurationSeconds), (v) => { ev.maxDurationSeconds = minutesToSeconds(v); },
        { inputmode: 'decimal', placeholder: 'sense' }),
    );
    panel.append(whenRow);

    panel.append(this.#select('Recurrència', RECURRENCES, ev.recurrence, (v) => {
      ev.recurrence = v;
      if (v === 'CADA_N_SETMANES') ev.intervalWeeks ??= 2;
      if (v === 'MENSUAL_ORDINAL' && ev.ordinals.length === 0) ev.ordinals = [1];
      this.#restructured();
    }));
    this.#recurrenceFields(panel, ev);

    const datesRow = el('div', 'ee-row');
    datesRow.append(
      this.#field('Des del dia (opcional)', 'date', ev.startDate ?? '', (v) => { ev.startDate = v || null; }),
      this.#field('Fins al dia (opcional)', 'date', ev.endDate ?? '', (v) => { ev.endDate = v || null; }),
    );
    if (ev.recurrence !== 'DATES') {
      panel.append(datesRow);
      panel.append(this.#dateList('Dies que no s\'emet (excepcions)', ev.excludedDates, (dates) => { ev.excludedDates = dates; }));
    }

    panel.append(this.#select('Repetició dins del dia', REPEATS, ev.repeat, (v) => {
      ev.repeat = v;
      if (v === 'CADA_N_MIN') ev.intervalMinutes ??= 30;
      this.#restructured();
    }));
    if (ev.repeat === 'CADA_HORA') {
      panel.append(this.#textField('Quines hores (buit = totes)', formatHours(ev.hoursMask), (v) => {
        const mask = parseHours(v);
        if (mask !== undefined) ev.hoursMask = mask;
      }, { placeholder: 'p. ex. 7-23 o 9, 12, 18' }, `Sona cada hora al minut ${ev.startTime.slice(3, 5)} i segon ${ev.startTime.slice(6, 8)} de l'hora d'inici.`));
    }
    if (ev.repeat === 'CADA_N_MIN') {
      panel.append(this.#textField('Cada quants minuts', String(ev.intervalMinutes ?? ''), (v) => { ev.intervalMinutes = intOrNull(v); },
        { inputmode: 'numeric' }, 'A partir de l\'hora d\'inici i fins al final del dia.'));
    }

    // Pròximes emissions (les calcula el motor amb l'esborrany)
    panel.append(el('h3', 'ee-section', 'Pròximes emissions'));
    const preview = el('div', 'pl-next');
    preview.id = 'ee-preview';
    panel.append(preview);

    // Opcions del mode
    panel.append(el('h3', 'ee-section', 'Opcions'));
    if (ev.mode === 'TALLA' || ev.mode === 'ENCUA') {
      panel.append(this.#switch('Esborra la cua abans de posar-hi el contingut', ev.clearQueue, (on) => { ev.clearQueue = on; }));
    }
    if (ev.mode === 'TALLA') {
      panel.append(this.#textField('Fos de la pista que sonava (ms)', ev.fadeMs == null ? '' : String(ev.fadeMs), (v) => { ev.fadeMs = intOrNull(v); },
        { inputmode: 'numeric', placeholder: '1000' }, '0 = tall sec. Buit = 1.000 ms.'));
    }
    if (ev.mode === 'SUPERPOSA' || ev.actions.some((/** @type {any} */ a) => a.type === 'AUXILIAR')) {
      // Buit = la per defecte de la configuració (D-078); 0 = sense atenuació
      const standard = this.#ducking ? dbText(this.#ducking.auxiliary) : null;
      panel.append(this.#textField('Atenuació de la música mentre sona l\'auxiliar (dB)', ev.duckingDb == null ? '' : String(ev.duckingDb).replace('.', ','),
        (v) => { ev.duckingDb = numberOrNull(v); }, { inputmode: 'decimal', placeholder: standard ? `per defecte, ${standard}` : 'per defecte' },
        `Negatiu: −12 vol dir 12 dB més fluix. Buit = la per defecte${standard ? ` (${standard})` : ''}. 0 = no s'atenua.`));
    }
    const advanced = el('div', 'ee-row');
    advanced.append(
      this.#textField('Prioritat', String(ev.priority), (v) => { ev.priority = intOrNull(v) ?? 50; }, { inputmode: 'numeric' }),
      this.#textField('Tolerància (s)', String(ev.toleranceSeconds), (v) => { ev.toleranceSeconds = intOrNull(v) ?? 60; }, { inputmode: 'numeric' }),
    );
    panel.append(advanced, el('div', 'form-text mb-2',
      'Si dos Talla o Encua coincideixen, sona el de prioritat més alta. Si s\'hi arriba més tard que la tolerància, s\'omet.'));

    // Accions
    panel.append(el('h3', 'ee-section', 'Accions, en ordre d\'emissió'));
    panel.append(this.#actionList(ev));
    panel.append(this.#addActions(ev));

    if (ev.notes) panel.append(el('p', 'pl-notes', ev.notes));

    // Error i botons
    const error = el('div', 'alert alert-danger d-flex gap-2 mt-3 mb-0 d-none');
    error.id = 'ee-error';
    error.setAttribute('role', 'alert');
    error.append(icon('bi-exclamation-octagon-fill'), el('span', ''));
    panel.append(error);

    const buttons = el('div', 'ee-buttons');
    const save = el('button', 'btn btn-primary');
    save.id = 'ee-save';
    save.type = 'button';
    save.append(icon('bi-check-lg'), ' ', el('span', '', 'Desa'));
    save.addEventListener('click', () => this.#save());
    buttons.append(save);

    // Quants avisos hi ha: els avisos són a dalt i, si s'ha baixat fins aquí, no es veuen
    const warningCount = el('button', 'btn btn-sm btn-link ee-warn-count d-none');
    warningCount.id = 'ee-warn-count';
    warningCount.type = 'button';
    warningCount.title = 'Mostra els avisos';
    warningCount.addEventListener('click', () => this.#panel.querySelector('#ee-warnings')?.scrollIntoView({ block: 'start' }));
    buttons.append(warningCount);

    const discard = el('button', 'btn btn-outline-secondary');
    discard.id = 'ee-discard';
    discard.type = 'button';
    discard.append(el('span', '', 'Descarta els canvis'));
    discard.addEventListener('click', () => {
      if (this.#original) this.open(this.#original);
      else {
        this.open(null);
        this.#callbacks.onClosed();
      }
    });
    buttons.append(discard);

    if (ev.id !== 0) {
      const duplicate = el('button', 'btn btn-outline-secondary');
      duplicate.type = 'button';
      duplicate.append(icon('bi-copy'), ' ', el('span', '', 'Duplica'));
      duplicate.addEventListener('click', async () => {
        if (!(await this.confirmDiscard())) return;
        this.#original = null;
        this.#draft = duplicateEvent(ev);
        this.#restructured();
      });
      const remove = el('button', 'btn btn-outline-danger');
      remove.type = 'button';
      remove.append(icon('bi-trash'), ' ', el('span', '', 'Esborra…'));
      remove.addEventListener('click', () => this.#delete());
      buttons.append(duplicate, remove);
    }
    panel.append(buttons);

    panel.scrollTop = scroll;
    this.#showError();
    this.#syncButtons();
    this.#schedulePreview(0);
  }

  /** Els camps que depenen de la recurrència. @param {HTMLElement} panel @param {any} ev */
  #recurrenceFields(panel, ev) {
    const days = () => this.#days('Dies de la setmana', ev.daysMask, (mask) => { ev.daysMask = mask; });
    switch (ev.recurrence) {
      case 'CADA_N_SETMANES': {
        const row = el('div', 'ee-row');
        row.append(
          this.#textField('Cada quantes setmanes', String(ev.intervalWeeks ?? ''), (v) => { ev.intervalWeeks = intOrNull(v); }, { inputmode: 'numeric' }),
          this.#field('Un dia en què segur que toca', 'date', ev.anchorDate ?? '', (v) => { ev.anchorDate = v || null; }),
        );
        panel.append(days(), row);
        break;
      }
      case 'MENSUAL_ORDINAL': {
        const group = el('div', 'ee-checks');
        for (const [value, label] of ORDINALS) {
          group.append(this.#check(String(label), ev.ordinals.includes(value), (on) => {
            ev.ordinals = on ? [...new Set([...ev.ordinals, value])] : ev.ordinals.filter((/** @type {number} */ o) => o !== value);
          }));
        }
        panel.append(this.#labelled('Quin del mes', group), days());
        break;
      }
      case 'DIES_MES':
        panel.append(this.#textField('Dies del mes', formatMonthDays(ev.monthDays), (v) => {
          const list = parseMonthDays(v);
          if (list !== undefined) ev.monthDays = list;
        }, { placeholder: 'p. ex. 1, 15, últim' }));
        break;
      case 'DATES':
        panel.append(this.#dateList('Dates en què sona', ev.includedDates, (dates) => { ev.includedDates = dates; }));
        break;
      default:
        panel.append(days());
    }
  }

  /** La llista d'accions, amb els botons per pujar, baixar i treure. @param {any} ev */
  #actionList(ev) {
    const list = el('ol', 'pl-actions');
    if (ev.actions.length === 0) list.append(el('li', 'pl-action-empty', 'Encara no hi ha cap acció.'));
    ev.actions.forEach((/** @type {any} */ action, /** @type {number} */ index) => {
      const li = el('li', `pl-action${action.missing ? ' is-missing' : ''}`);
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
      tool('bi-arrow-up', 'Puja (sona abans)', () => { ev.actions = moveItem(ev.actions, index, -1); this.#restructured(); }, index === 0);
      tool('bi-arrow-down', 'Baixa (sona després)', () => { ev.actions = moveItem(ev.actions, index, 1); this.#restructured(); }, index === ev.actions.length - 1);
      tool('bi-x-lg', 'Treu aquesta acció', () => { ev.actions = ev.actions.filter((/** @type {any} */ a) => a !== action); this.#restructured(); });
      top.append(text, tools);
      li.append(top);

      if (action.missing) {
        const warning = el('div', 'pl-action-warning');
        warning.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', 'No sonarà: la pista o el fitxer falta'));
        li.append(warning);
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
    if (action.type === 'STREAM') {
      li.append(
        this.#textField('Adreça del stream', p.url ?? '', (v) => { p.url = v.trim(); action.label = p.url; }, { placeholder: 'http://…' }),
        this.#textField('Durada (min)', secondsToMinutes(p.durada_seg), (v) => {
          const seconds = minutesToSeconds(v);
          if (seconds == null) delete p.durada_seg;
          else p.durada_seg = seconds;
        }, { inputmode: 'decimal' }, 'Els streams encara no sonen: arribaran al pas 7.'),
      );
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

  /** Botons per afegir accions i, si cal, el cercador de pistes. @param {any} ev */
  #addActions(ev) {
    const box = el('div', 'ee-add');
    const buttons = el('div', 'ee-add-buttons');
    const add = (/** @type {string} */ iconName, /** @type {string} */ label, /** @type {string} */ title, /** @type {() => void} */ run, on = false) => {
      const b = el('button', `btn btn-sm ${on ? 'btn-secondary' : 'btn-outline-secondary'}`);
      b.type = 'button';
      b.title = title;
      b.append(icon(iconName), ' ', el('span', '', label));
      b.addEventListener('click', run);
      buttons.append(b);
    };
    const toggleSearch = (/** @type {'PISTA' | 'AUXILIAR'} */ kind) => () => {
      this.#searchFor = this.#searchFor === kind ? null : kind;
      this.#restructured();
    };
    add('bi-music-note-beamed', 'Pista', 'Una pista de la biblioteca, a la cua', toggleSearch('PISTA'), this.#searchFor === 'PISTA');
    add('bi-layers', 'Auxiliar', 'Una pista que sona per sobre del que soni (senyals horaris)', toggleSearch('AUXILIAR'), this.#searchFor === 'AUXILIAR');
    add('bi-arrow-repeat', 'Rotació', 'Canvia la rotació de l\'AutoDJ', () => {
      const first = this.#rotations[0];
      ev.actions = [...ev.actions, { type: 'ROTACIO', params: { rotacioId: first?.id ?? null }, daysMask: null, label: first ? `Rotació ${first.name}` : 'Rotació' }];
      this.#restructured();
    });
    add('bi-shuffle', 'AutoDJ', 'Activa o atura l\'AutoDJ', () => {
      ev.actions = [...ev.actions, { type: 'AUTODJ', params: { actiu: true }, daysMask: null, label: 'Activa l\'AutoDJ' }];
      this.#restructured();
    });
    add('bi-soundwave', 'Sweepers', 'Canvia la categoria de sweepers del trepitjador (sonen a sobre de l\'inici de les cançons)', () => {
      const first = this.#sweeperOptions[0];
      ev.actions = [...ev.actions, { type: 'SWEEPERS', params: { subcategoriaId: first?.id ?? null }, daysMask: null, label: first?.label ?? 'Sweepers' }];
      this.#restructured();
    });
    add('bi-bag', 'Bossa', 'Canvia la subcategoria d\'una bossa amb nom (p. ex. el dia 1 de cada mes)', () => {
      const first = this.#bags[0];
      ev.actions = [...ev.actions, { type: 'BOSSA', params: { bossaId: first?.id ?? null, subcategoriaId: null }, daysMask: null, label: first ? `Bossa «${first.name}»` : 'Bossa' }];
      this.#restructured();
    });
    add('bi-broadcast', 'Stream', 'Un stream d\'internet (sonarà a partir del pas 7)', () => {
      ev.actions = [...ev.actions, { type: 'STREAM', params: { url: '' }, daysMask: null, label: 'Stream' }];
      this.#restructured();
    });
    box.append(el('div', 'ee-add-title', 'Afegeix:'), buttons);

    if (this.#searchFor) box.append(this.#trackSearch(ev, this.#searchFor));
    return box;
  }

  /** Cercador de pistes (library.search): en clicar-ne una, s'afegeix com a acció. @param {any} ev @param {'PISTA' | 'AUXILIAR'} kind */
  #trackSearch(ev, kind) {
    const box = el('div', 'ee-search');
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    input.type = 'search';
    input.placeholder = kind === 'AUXILIAR' ? 'Cerca la pista que sonarà per sobre…' : 'Cerca una pista per títol, artista o carpeta…';
    input.setAttribute('aria-label', 'Cerca una pista');
    const results = el('div', 'ee-results');
    let timer = 0;
    let token = 0;
    const search = async () => {
      const mine = ++token;
      try {
        const result = await this.#send('library.search', { text: input.value, limit: 30 });
        if (mine !== token) return;
        results.replaceChildren();
        if (result.tracks.length === 0) results.append(el('div', 'text-body-secondary', 'Cap pista amb aquest text.'));
        for (const track of result.tracks) {
          const row = el('button', `ee-result${track.fileState === 'missing' ? ' is-missing' : ''}`);
          row.type = 'button';
          const label = track.artist ? `${track.artist} - ${track.title}` : track.title;
          const detail = [typeName(track.type), track.durationMs ? formatDuration(track.durationMs) : '',
            track.fileState === 'missing' ? 'el fitxer falta' : ''].filter(Boolean).join(' · ');
          row.append(el('span', 'ee-result-label', label), el('span', 'ee-result-detail', detail));
          row.title = track.path;
          row.addEventListener('click', () => {
            const params = kind === 'AUXILIAR' ? { songId: track.id, player: 1 } : { songId: track.id };
            ev.actions = [...ev.actions, { type: kind, params, daysMask: null, label, detail, missing: track.fileState === 'missing' }];
            this.#restructured();
          });
          results.append(row);
        }
        if (result.more) results.append(el('div', 'text-body-secondary', 'N\'hi ha més: escriu més text per afinar la cerca.'));
      } catch (err) {
        if (mine === token) results.replaceChildren(el('div', 'text-danger', `No s'ha pogut cercar: ${/** @type {Error} */ (err).message}`));
      }
    };
    input.addEventListener('input', () => {
      clearTimeout(timer);
      timer = window.setTimeout(search, 250);
    });
    box.append(input, results);
    search();
    setTimeout(() => input.focus(), 0);
    return box;
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

  /** Una llista de dates: un camp per afegir-ne i una etiqueta per cada una, amb el botó per treure-la. @param {string} label @param {string[]} dates @param {(dates: string[]) => void} onChange */
  #dateList(label, dates, onChange) {
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
    const add = el('button', 'btn btn-sm btn-outline-secondary');
    add.type = 'button';
    add.append(icon('bi-plus-lg'), ' ', el('span', '', 'Afegeix'));
    add.addEventListener('click', () => {
      if (!input.value || dates.includes(input.value)) return;
      onChange([...dates, input.value].sort());
      this.#restructured();
    });
    row.append(input, add);
    box.append(chips, row);
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
      for (const o of result.occurrences) {
        const date = parseDate(o.date);
        const chip = el('span', `pl-next-chip${o.skippedClockChange ? ' is-skipped' : ''}`,
          `${DAY_NAMES[(date.getDay() + 6) % 7]} ${date.getDate()}/${date.getMonth() + 1} ${shortTime(o.time)}`);
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

  /**
   * Avisos del motor: què passarà els pròxims dies (programa que es tallarà, Encua que no sonarà, fitxers que falten…).
   * @param {{ text: string, dates: string[] }[]} warnings
   */
  #showWarnings(warnings) {
    const box = this.#panel.querySelector('#ee-warnings');
    if (!box) return;
    box.replaceChildren();
    box.classList.toggle('d-none', warnings.length === 0);
    const count = /** @type {HTMLElement | null} */ (this.#panel.querySelector('#ee-warn-count'));
    if (count) {
      count.classList.toggle('d-none', warnings.length === 0);
      count.replaceChildren(icon('bi-exclamation-triangle-fill'), ' ',
        el('span', '', warnings.length === 1 ? '1 avís' : `${warnings.length} avisos`));
    }
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

const ACTION_NAMES = {
  PISTA: 'Pista', AUXILIAR: 'Auxiliar', ROTACIO: 'Rotació', AUTODJ: 'AutoDJ', STREAM: 'Stream', SWEEPERS: 'Sweepers', BOSSA: 'Bossa',
  CAMPANYA: 'Campanya', PLANTILLA: 'Plantilla', ESPERA: 'Espera', ATURA: 'Atura', ENTRADA: 'Entrada', EXECUTA: 'Executa', REPRODUEIX: 'Reprodueix',
};
