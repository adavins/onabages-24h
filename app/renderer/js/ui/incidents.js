// @ts-check
// Incidències (fase 5, pas 1b; D-156): la finestra amb tot el que ha anat malament a l'emissió, desat pel motor
// (incidents.list). Filtres de període, tipus i «Només les obertes», les més recents primer i per pàgines. Mentre és oberta,
// es torna a carregar quan el motor n'anota una de nova o en tanca una.

import { el, icon, statusLabel } from './dom.js';
import { formDialog } from './form-dialog.js';
import {
  KINDS, PERIODS, kindText, severityInfo, stateInfo, dateTimeText, durationText, periodFrom, countText,
} from '../incidents-form.js';

/** Quantes es carreguen cada vegada. */
const PAGE = 100;

/** Per tornar a carregar la finestra oberta quan arriba incidents.changed. @type {(() => void) | null} */
let reloadOpen = null;

/** El motor ha anotat, canviat o tancat una incidència: si la finestra és oberta, es torna a carregar. */
export function incidentsChanged() {
  reloadOpen?.();
}

/**
 * Obre la finestra «Incidències».
 * @param {(cmd: string, params?: object) => Promise<any>} send
 */
export async function incidentsDialog(send) {
  // Filtres
  const period = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm inc-select'));
  period.append(...PERIODS.map(([value, text]) => new Option(text, value)));
  period.value = '7';
  const kind = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm inc-select'));
  kind.append(new Option('Tots els tipus', ''), ...KINDS.map(([value, text]) => new Option(text, value)));
  const onlyOpen = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  onlyOpen.type = 'checkbox';
  onlyOpen.id = 'inc-only-open';
  const onlyOpenLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Només les obertes'));
  onlyOpenLabel.htmlFor = onlyOpen.id;
  const onlyOpenBox = el('div', 'form-check mb-0');
  onlyOpenBox.append(onlyOpen, onlyOpenLabel);
  const summary = el('span', 'inc-summary text-body-secondary');
  summary.setAttribute('aria-live', 'polite');
  const filters = el('div', 'inc-filters');
  filters.append(period, kind, onlyOpenBox, summary);

  // Taula
  const rows = el('tbody');
  const table = el('table', 'tm-table inc-table');
  const head = el('tr');
  for (const [label, css] of [['Hora', 'inc-c-time'], ['Gravetat', 'inc-c-severity'], ['Tipus', 'inc-c-kind'],
    ['Què ha passat', 'inc-c-text'], ['Durada', 'inc-c-duration'], ['Estat', 'inc-c-state']]) {
    head.append(el('th', css, label));
  }
  const thead = el('thead');
  thead.append(head);
  table.append(thead, rows);
  const empty = el('div', 'empty-note d-none', 'No hi ha cap incidència amb aquests filtres.');
  const more = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary d-none inc-more'));
  more.type = 'button';
  more.append(icon('bi-chevron-down'), ' Carrega\'n més');
  const scroll = el('div', 'inc-scroll');
  scroll.append(table, empty, more);

  /** @type {any[]} */
  let loaded = [];
  let total = 0;
  let request = 0; // per no barrejar respostes si es canvien els filtres de pressa

  const params = () => {
    /** @type {Record<string, any>} */
    const p = { from: periodFrom(period.value, new Date()) };
    if (kind.value) p.kinds = [kind.value];
    return p;
  };

  /** @param {boolean} append */
  const load = async (append) => {
    const mine = ++request;
    let result;
    try {
      if (onlyOpen.checked) {
        // Les obertes, totes (el motor les té a la memòria); el tipus es filtra aquí
        result = await send('incidents.list', { open: true });
        result.incidents = result.incidents.filter((/** @type {any} */ i) => !kind.value || i.kind === kind.value);
        result.total = result.incidents.length;
      } else {
        result = await send('incidents.list', { ...params(), offset: append ? loaded.length : 0, limit: PAGE });
      }
    } catch (err) {
      if (mine !== request) return;
      summary.replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'han pogut llegir: ${/** @type {Error} */ (err).message}`));
      return;
    }
    if (mine !== request) return;
    loaded = append ? [...loaded, ...result.incidents] : result.incidents;
    total = result.total;
    render();
  };

  const render = () => {
    const now = new Date();
    rows.replaceChildren(...loaded.map((i) => {
      const tr = el('tr');
      const severity = severityInfo(i.severity);
      const state = stateInfo(i);
      const text = el('td', 'inc-c-text');
      text.append(el('div', '', i.text));
      if (i.detail) text.append(el('div', 'inc-detail', i.detail));
      text.title = i.detail ? `${i.text}\n${i.detail}` : i.text;
      const severityCell = el('td', 'inc-c-severity');
      severityCell.append(statusLabel(severity.className, severity.icon, severity.text));
      const stateCell = el('td', 'inc-c-state');
      stateCell.append(statusLabel(state.className, state.icon, state.text));
      tr.append(el('td', 'inc-c-time', dateTimeText(i.startedAt)), severityCell, el('td', 'inc-c-kind', kindText(i.kind)),
        text, el('td', 'inc-c-duration', durationText(i, now)), stateCell);
      return tr;
    }));
    empty.classList.toggle('d-none', loaded.length > 0);
    more.classList.toggle('d-none', loaded.length >= total);
    summary.textContent = loaded.length < total
      ? `${countText(total)} (se'n veuen ${loaded.length.toLocaleString('ca-ES')})`
      : countText(total);
  };

  for (const control of [period, kind, onlyOpen]) control.addEventListener('change', () => load(false));
  // Amb «Només les obertes», el període no compta
  onlyOpen.addEventListener('change', () => { period.disabled = onlyOpen.checked; });
  more.addEventListener('click', () => load(true));

  await load(false);
  reloadOpen = () => load(false);
  try {
    const closed = formDialog({ title: 'Incidències', icon: 'bi-journal-text', body: [filters, scroll], wide: true });
    // Més ampla que les altres finestres: la taula té sis columnes i el text ha de tenir lloc
    scroll.closest('dialog')?.classList.add('inc-dialog');
    await closed;
  } finally {
    reloadOpen = null;
  }
}
