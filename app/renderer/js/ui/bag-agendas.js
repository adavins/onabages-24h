// @ts-check
// Finestra «Agendes de bosses» (D-170), des de Rotacions → Bosses → «Agenda…». A l'esquerra, les agendes (què toca
// aquest mes i el que ve); a la dreta, l'editor: nom, mes d'inici, activa, les bosses i les subcategories per ordre, i
// una taula amb els pròxims 12 mesos (agendas.preview, sense desar). El motor fa el canvi el dia 1 a les 00:00; en
// desar una agenda activa, les bosses passen de seguida a la subcategoria d'aquest mes (es demana confirmació).

import { el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { monthOf, monthName, monthShort, nextMonth, agendaProblem, assignmentText, shortSubcategory, moveItem, bagsThatChange } from '../agenda-form.js';

/** Espera després d'un canvi a l'editor abans de demanar la taula al motor. */
const PREVIEW_DELAY_MS = 250;

/**
 * Obre la finestra. Es tanca amb «Tanca» o Esc (si hi ha canvis sense desar, ho pregunta).
 * @param {(cmd: string, params?: object) => Promise<any>} send
 */
export async function openBagAgendas(send) {
  /** @type {any[]} */
  let agendas = [];
  /** @type {any[]} */
  let bags = [];
  /** @type {any[]} */
  let categories = [];
  let currentMonth = monthOf(new Date());
  /** L'agenda oberta a l'editor (null = cap; id null = nova). @type {{ id: number | null, name: string, startMonth: string, active: boolean, bagIds: number[], subcategoryIds: number[] } | null} */
  let editing = null;
  /** Com era en obrir-la, per saber si hi ha canvis. */
  let original = '';
  /** @type {number | undefined} */
  let previewTimer;
  let previewRequest = 0;

  const dialog = /** @type {HTMLDialogElement} */ (el('dialog', 'ob-dialog ob-dialog-wide ag-dialog'));
  const title = el('h2', 'h5');
  title.append(icon('bi-calendar2-range'), ' ', el('span', '', 'Agendes de bosses'));
  const intro = el('p', 'form-text mt-0',
    'Canvien soles les subcategories de les bosses el dia 1 de cada mes a les 00:00. Cada mes sonen tantes subcategories seguides com bosses hi ha, i avancen d\'una; la que continua es queda a la mateixa bossa.');
  const list = el('div', 'ag-list');
  const addAgenda = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  addAgenda.type = 'button';
  addAgenda.dataset.needs = 'programmer';
  addAgenda.append(icon('bi-plus-lg'), ' Nova agenda');
  const left = el('div', 'ag-left');
  left.append(addAgenda, list);
  const editor = el('div', 'ag-editor');
  const cols = el('div', 'ag-cols');
  cols.append(left, editor);
  const close = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-outline-secondary', 'Tanca'));
  close.type = 'button';
  const footer = el('div', 'd-flex justify-content-end gap-2 mt-3');
  footer.append(close);
  dialog.append(title, intro, cols, footer);
  document.body.append(dialog);

  const isDirty = () => editing !== null && JSON.stringify(editing) !== original;
  const askDiscard = async () => !isDirty() || await confirmAction({
    title: 'Descartar els canvis?', message: 'L\'agenda té canvis sense desar.', confirm: 'Descarta\'ls',
  });

  const finish = async () => {
    if (!(await askDiscard())) return;
    clearTimeout(previewTimer);
    dialog.close();
    dialog.remove();
  };
  close.addEventListener('click', finish);
  dialog.addEventListener('cancel', (e) => {
    e.preventDefault(); // Esc: com «Tanca» (pregunta si hi ha canvis)
    finish();
  });
  addAgenda.addEventListener('click', async () => {
    if (!(await askDiscard())) return;
    open({ id: null, name: '', startMonth: currentMonth, active: true, bagIds: [], subcategoryIds: [] });
  });

  /** El nom sencer d'una subcategoria («Music / Adult 1»). @param {number} id */
  const subcategoryName = (id) => {
    for (const c of categories) {
      const s = c.subcategories.find((/** @type {any} */ x) => x.id === id);
      if (s) return `${c.name} / ${s.name}`;
    }
    return `Subcategoria ${id}`;
  };
  /** @param {number} id */
  const bagName = (id) => bags.find((b) => b.id === id)?.name ?? `Bossa ${id}`;

  async function load() {
    try {
      const [a, b, f] = await Promise.all([send('agendas.list'), send('bags.list'), send('library.filters')]);
      agendas = a.agendas;
      currentMonth = a.month;
      bags = b.bags;
      categories = f.categories;
    } catch (err) {
      list.replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'han pogut llegir les agendes: ${/** @type {Error} */ (err).message}`));
      return;
    }
    renderList();
  }

  function renderList() {
    if (agendas.length === 0) {
      list.replaceChildren(el('p', 'form-text', 'Encara no n\'hi ha cap.'));
      return;
    }
    list.replaceChildren(...agendas.map((agenda) => {
      const item = /** @type {HTMLButtonElement} */ (el('button', 'ag-item'));
      item.type = 'button';
      item.classList.toggle('active', editing?.id === agenda.id);
      const head = el('div', 'ag-item-head');
      head.append(el('strong', '', agenda.name), agenda.active
        ? statusLabel('text-success small', 'bi-check-circle-fill', 'Activa')
        : statusLabel('text-body-secondary small', 'bi-pause-circle', 'Aturada'));
      item.append(head,
        el('div', 'ag-item-line', `Aquest mes: ${assignmentText(agenda.current)}`),
        el('div', 'ag-item-line', `${capitalize(monthName(nextMonth(currentMonth)))}: ${assignmentText(agenda.next)}`));
      item.addEventListener('click', async () => {
        if (editing?.id === agenda.id || !(await askDiscard())) return;
        open({ id: agenda.id, name: agenda.name, startMonth: agenda.startMonth, active: agenda.active,
          bagIds: agenda.bags.map((/** @type {any} */ b) => b.id), subcategoryIds: agenda.subcategories.map((/** @type {any} */ s) => s.id) });
      });
      return item;
    }));
  }

  /** @param {NonNullable<typeof editing>} agenda */
  function open(agenda) {
    editing = agenda;
    original = JSON.stringify(agenda);
    renderList();
    renderEditor();
  }

  function renderEditor() {
    if (!editing) {
      editor.replaceChildren(el('p', 'form-text', 'Tria una agenda o crea\'n una de nova.'));
      return;
    }
    const agenda = editing;
    const changed = () => {
      problemBox.replaceChildren();
      schedulePreview();
    };

    const name = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    name.id = 'ag-name';
    name.value = agenda.name;
    name.maxLength = 100;
    name.placeholder = 'p. ex. «Adult»';
    name.addEventListener('input', () => { agenda.name = name.value; changed(); });

    const start = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    start.type = 'month';
    start.id = 'ag-start';
    start.value = agenda.startMonth;
    start.addEventListener('change', () => { agenda.startMonth = start.value; changed(); });

    const active = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    active.type = 'checkbox';
    active.id = 'ag-active';
    active.checked = agenda.active;
    active.addEventListener('change', () => { agenda.active = active.checked; changed(); });
    const activeLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Activa (si no, no canvia res)'));
    activeLabel.htmlFor = active.id;
    const activeBox = el('div', 'form-check mt-1');
    activeBox.append(active, activeLabel);

    const fields = el('div', 'ag-fields');
    fields.append(field('Nom', name), field('Mes d\'inici', start, 'El mes en què sonen les primeres subcategories de la llista.'), activeBox);

    // Les bosses que no són en cap altra agenda
    const usedElsewhere = new Set(agendas.filter((a) => a.id !== agenda.id).flatMap((a) => a.bags.map((/** @type {any} */ b) => b.id)));
    const bagList = orderedList('Bosses', agenda.bagIds, bagName, (ids) => { agenda.bagIds = ids; renderEditor(); schedulePreview(); },
      () => {
        const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
        select.append(new Option('Afegeix una bossa…', ''),
          ...bags.filter((b) => !agenda.bagIds.includes(b.id) && !usedElsewhere.has(b.id)).map((b) => new Option(b.name, String(b.id))));
        return select;
      }, 'Per ordre: el primer mes, la primera subcategoria va a la primera bossa.');

    const subList = orderedList('Subcategories', agenda.subcategoryIds, (id) => shortSubcategory(subcategoryName(id)),
      (ids) => { agenda.subcategoryIds = ids; renderEditor(); schedulePreview(); },
      () => {
        const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
        select.append(new Option('Afegeix una subcategoria…', ''));
        for (const c of categories) {
          const free = c.subcategories.filter((/** @type {any} */ s) => !agenda.subcategoryIds.includes(s.id));
          if (free.length === 0) continue;
          const group = document.createElement('optgroup');
          group.label = c.name;
          group.append(...free.map((/** @type {any} */ s) => new Option(`${s.name} (${s.count})`, String(s.id))));
          select.append(group);
        }
        return select;
      }, 'Per on van rotant; després de l\'última es torna a la primera.');

    const lists = el('div', 'ag-lists');
    lists.append(bagList, subList);

    const save = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-primary'));
    save.type = 'button';
    save.dataset.needs = 'programmer';
    save.append(icon('bi-check-lg'), ' Desa');
    save.addEventListener('click', () => saveAgenda());
    const tools = el('div', 'd-flex gap-2 align-items-center mt-2');
    if (agenda.id != null) {
      const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-danger'));
      remove.type = 'button';
      remove.dataset.needs = 'programmer';
      remove.append(icon('bi-trash'), ' Esborra');
      remove.addEventListener('click', () => deleteAgenda());
      tools.append(remove);
    }
    tools.append(el('span', 'flex-grow-1'), save);

    editor.replaceChildren(fields, lists, el('h3', 'ag-subtitle', 'Els pròxims 12 mesos'), previewBox, problemBox, tools);
    for (const control of [name, start, active]) control.dataset.needs = 'programmer';
    schedulePreview();
  }

  const previewBox = el('div', 'ag-preview');
  const problemBox = el('div', '');

  function schedulePreview() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(preview, PREVIEW_DELAY_MS);
  }

  async function preview() {
    if (!editing) return;
    const problem = agendaProblem(editing.name || '-', editing.bagIds, editing.subcategoryIds, editing.startMonth);
    if (problem) {
      previewBox.replaceChildren(el('p', 'form-text', problem));
      return;
    }
    const mine = ++previewRequest;
    let months;
    try {
      months = (await send('agendas.preview', { startMonth: editing.startMonth, bagIds: editing.bagIds, subcategoryIds: editing.subcategoryIds })).months;
    } catch (err) {
      if (mine === previewRequest) previewBox.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
      return;
    }
    if (mine !== previewRequest || !editing) return;
    const table = el('table', 'tm-table ag-table');
    const head = el('tr');
    head.append(el('th', '', 'Mes'), ...editing.bagIds.map((id) => el('th', '', bagName(id))));
    const thead = el('thead');
    thead.append(head);
    const body = el('tbody');
    /** @type {Map<number, number>} */
    const previous = new Map();
    for (const m of months) {
      const tr = el('tr', m.month === currentMonth ? 'ag-now' : '');
      tr.append(el('td', 'ag-month', m.month === currentMonth ? `${monthShort(m.month)} (ara)` : monthShort(m.month)));
      if (m.bags.length === 0) {
        const td = el('td', 'text-body-secondary', 'encara no ha començat');
        td.setAttribute('colspan', String(editing.bagIds.length));
        tr.append(td);
      }
      for (const a of m.bags) {
        // Les que canvien respecte del mes anterior, marcades (icona + negreta, no només color)
        const changes = previous.has(a.bagId) && previous.get(a.bagId) !== a.subcategoryId;
        const td = el('td', changes ? 'ag-changes' : '');
        if (changes) td.append(icon('bi-arrow-repeat'), ' ');
        td.append(shortSubcategory(a.subcategory));
        if (changes) td.title = 'Canvia aquest mes';
        tr.append(td);
        previous.set(a.bagId, a.subcategoryId);
      }
      body.append(tr);
    }
    table.append(thead, body);
    previewBox.replaceChildren(table);
  }

  async function saveAgenda() {
    if (!editing) return;
    const agenda = editing;
    const problem = agendaProblem(agenda.name, agenda.bagIds, agenda.subcategoryIds, agenda.startMonth);
    if (problem) {
      problemBox.replaceChildren(statusLabel('text-danger small', 'bi-exclamation-triangle-fill', problem));
      return;
    }
    // Si és activa i ja ha començat, les bosses canvien de seguida: es diu quines i es demana confirmació
    if (agenda.active) {
      try {
        const now = (await send('agendas.preview', { startMonth: agenda.startMonth, bagIds: agenda.bagIds, subcategoryIds: agenda.subcategoryIds, months: 1 })).months[0];
        const changes = bagsThatChange(now?.bags ?? [], bags);
        if (changes.length > 0) {
          const sure = await confirmAction({
            title: 'Canviar les bosses ara?',
            message: 'En desar-la, ara mateix: ' + changes.map((c) => `«${c.bag}» passa a ${shortSubcategory(c.subcategory)}`).join('; ') +
              '. Les cançons de la cua que en venien es tornaran a triar.',
            confirm: 'Desa i canvia-les', danger: false,
          });
          if (!sure) return;
        }
      } catch { /* el desament ja dirà l'error */ }
    }
    try {
      const result = await send('agendas.save', {
        ...(agenda.id != null ? { agendaId: agenda.id } : {}),
        name: agenda.name.trim(), startMonth: agenda.startMonth, active: agenda.active, bagIds: agenda.bagIds, subcategoryIds: agenda.subcategoryIds,
      });
      editing = null;
      await load();
      const saved = agendas.find((a) => a.id === result.agenda.id);
      if (saved) open({ id: saved.id, name: saved.name, startMonth: saved.startMonth, active: saved.active,
        bagIds: saved.bags.map((/** @type {any} */ b) => b.id), subcategoryIds: saved.subcategories.map((/** @type {any} */ s) => s.id) });
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut desar l\'agenda', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  async function deleteAgenda() {
    if (!editing || editing.id == null) return;
    const sure = await confirmAction({
      title: 'Esborrar l\'agenda',
      message: `S'esborrarà l'agenda «${editing.name}». Les bosses es queden amb la subcategoria que tenen ara i ja no canviaran soles.`,
      confirm: 'Esborra-la',
    });
    if (!sure) return;
    try {
      await send('agendas.delete', { agendaId: editing.id });
      editing = null;
      await load();
      renderEditor();
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut esborrar l\'agenda', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  renderEditor();
  dialog.showModal();
  await load();
}

/**
 * Una llista ordenada (bosses o subcategories): cada element amb ↑ ↓ i ✕, i un desplegable per afegir-ne.
 * @param {string} label @param {number[]} ids @param {(id: number) => string} nameOf
 * @param {(ids: number[]) => void} onChange @param {() => HTMLSelectElement} makeAdd @param {string} help
 */
function orderedList(label, ids, nameOf, onChange, makeAdd, help) {
  const box = el('div', 'ag-ordered');
  box.append(el('div', 'form-label mb-1', label));
  const ol = el('ol', 'ag-ol');
  ids.forEach((id, index) => {
    const li = el('li', 'ag-li');
    li.append(el('span', 'ag-li-name', nameOf(id)));
    const button = (/** @type {string} */ iconName, /** @type {string} */ text, /** @type {() => void} */ run, disabled = false) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link p-0 text-body-secondary'));
      b.type = 'button';
      b.title = text;
      b.setAttribute('aria-label', `${text}: ${nameOf(id)}`);
      b.disabled = disabled;
      b.dataset.needs = 'programmer';
      b.append(icon(iconName));
      b.addEventListener('click', run);
      li.append(b);
    };
    button('bi-arrow-up', 'Puja', () => onChange(moveItem(ids, index, -1)), index === 0);
    button('bi-arrow-down', 'Baixa', () => onChange(moveItem(ids, index, 1)), index === ids.length - 1);
    button('bi-x-lg', 'Treu', () => onChange(ids.filter((x) => x !== id)));
    ol.append(li);
  });
  if (ids.length === 0) box.append(el('p', 'form-text my-1', 'Cap.'));
  else box.append(ol);
  const add = makeAdd();
  add.setAttribute('aria-label', `Afegeix: ${label}`);
  add.dataset.needs = 'programmer';
  add.addEventListener('change', () => {
    if (add.value) onChange([...ids, Number(add.value)]);
  });
  box.append(add, el('div', 'form-text', help));
  return box;
}

/** Un camp amb l'etiqueta a sobre. @param {string} label @param {HTMLElement} control @param {string} [help] */
function field(label, control, help) {
  const box = el('div', 'mb-2');
  const l = /** @type {HTMLLabelElement} */ (el('label', 'form-label mb-1', label));
  l.htmlFor = control.id;
  box.append(l, control);
  if (help) box.append(el('div', 'form-text', help));
  return box;
}

/** @param {string} text */
function capitalize(text) {
  return text.charAt(0).toUpperCase() + text.slice(1);
}
