// @ts-check
// Registre de canvis (fase 5, pas 8c; D-164): la finestra amb qui ha canviat què i quan (changes.list), de l'administrador.
// Filtres de període, usuari i tipus, els més recents primer i per pàgines. S'obre des de ⚙ Configuració → Usuaris.

import { el, icon, statusLabel } from './dom.js';
import { formDialog } from './form-dialog.js';
import { PERIODS, periodFrom, dateTimeText } from '../incidents-form.js';
import { AREAS, areaPrefixes, areaText, userText, countText, detailText } from '../changes-form.js';

/** Quants es carreguen cada vegada. */
const PAGE = 100;

/**
 * Obre la finestra «Registre de canvis».
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {{ id: number, name: string }[]} users  per al filtre d'usuari
 */
export async function changesDialog(send, users) {
  // Filtres
  const period = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm inc-select'));
  period.append(...PERIODS.map(([value, text]) => new Option(text, value)));
  period.value = '7';
  const user = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm inc-select'));
  user.append(new Option('Tots els usuaris', ''), ...users.map((u) => new Option(u.name, String(u.id))));
  const area = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm inc-select'));
  area.append(new Option('Tots els tipus', ''), ...AREAS.map(([value, text]) => new Option(text, value)));
  const summary = el('span', 'inc-summary text-body-secondary');
  summary.setAttribute('aria-live', 'polite');
  const filters = el('div', 'inc-filters');
  filters.append(period, user, area, summary);

  // Taula
  const rows = el('tbody');
  const table = el('table', 'tm-table inc-table');
  const head = el('tr');
  for (const [label, css] of [['Hora', 'inc-c-time'], ['Usuari', 'ch-c-user'], ['Tipus', 'inc-c-kind'], ['Què ha fet', 'inc-c-text']]) {
    head.append(el('th', css, label));
  }
  const thead = el('thead');
  thead.append(head);
  table.append(thead, rows);
  const empty = el('div', 'empty-note d-none', 'No hi ha cap canvi amb aquests filtres.');
  const more = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary d-none inc-more'));
  more.type = 'button';
  more.append(icon('bi-chevron-down'), ' Carrega\'n més');
  const scroll = el('div', 'inc-scroll');
  scroll.append(table, empty, more);

  /** @type {any[]} */
  let loaded = [];
  let total = 0;
  let request = 0; // per no barrejar respostes si es canvien els filtres de pressa

  /** @param {boolean} append */
  const load = async (append) => {
    const mine = ++request;
    /** @type {Record<string, any>} */
    const params = { from: periodFrom(period.value, new Date()), offset: append ? loaded.length : 0, limit: PAGE };
    if (user.value) params.userId = Number(user.value);
    if (area.value) params.commands = areaPrefixes(area.value);
    let result;
    try {
      result = await send('changes.list', params);
    } catch (err) {
      if (mine !== request) return;
      summary.replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'han pogut llegir: ${/** @type {Error} */ (err).message}`));
      return;
    }
    if (mine !== request) return;
    loaded = append ? [...loaded, ...result.entries] : result.entries;
    total = result.total;
    render();
  };

  const render = () => {
    rows.replaceChildren(...loaded.map((c) => {
      const tr = el('tr');
      const text = el('td', 'inc-c-text');
      text.append(el('div', '', c.summary));
      const detail = detailText(c.detail);
      if (detail) text.append(el('div', 'inc-detail', detail));
      text.title = detail ? `${c.summary}\n${detail}` : c.summary;
      tr.append(el('td', 'inc-c-time', dateTimeText(c.at)), el('td', 'ch-c-user', userText(c)), el('td', 'inc-c-kind', areaText(c.command)), text);
      return tr;
    }));
    empty.classList.toggle('d-none', loaded.length > 0);
    more.classList.toggle('d-none', loaded.length >= total);
    summary.textContent = loaded.length < total
      ? `${countText(total)} (se'n veuen ${loaded.length.toLocaleString('ca-ES')})`
      : countText(total);
  };

  for (const control of [period, user, area]) control.addEventListener('change', () => load(false));
  more.addEventListener('click', () => load(true));

  await load(false);
  const closed = formDialog({ title: 'Registre de canvis', icon: 'bi-person-lines-fill', body: [filters, scroll], wide: true });
  scroll.closest('dialog')?.classList.add('inc-dialog'); // tan ampla com la de les incidències
  await closed;
}
