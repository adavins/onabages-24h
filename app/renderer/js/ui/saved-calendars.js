// @ts-check
// Calendaris desats (fase 4, pas 6a; D-145): les finestres «Desa com a calendari…» i «Aplica un calendari desat…» del
// calendari d'una pista o d'una campanya. Les dades les desa i les aplica el motor (savedCalendars.*).

import { el, icon } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';
import { confirmAction } from './confirm.js';
import { slotsText, cellsText, findByName } from '../saved-calendar-form.js';

/** @typedef {{ id: number, name: string, cells: number, slots: { time: string, daysMask: number }[] }} SavedCalendar */

/** @param {string} title @param {unknown} err */
const showError = (title, err) => confirmAction({ title, message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });

/** @param {(cmd: string, params?: object) => Promise<any>} send @returns {Promise<SavedCalendar[] | null>} */
async function loadList(send) {
  try {
    return (await send('savedCalendars.list')).calendars;
  } catch (err) {
    await showError('No s\'han pogut llegir els calendaris desats', err);
    return null;
  }
}

/**
 * «Desa com a calendari…»: el nom i, si ja n'hi ha un amb aquest nom, si es vol substituir.
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {{ campaignId: number } | { trackId: number }} target  de qui és el calendari
 * @param {number} cells  quantes caselles hi ha marcades ara
 * @param {string} suggested  el nom proposat
 * @returns {Promise<SavedCalendar | null>}  el calendari desat, o null si no s'ha desat
 */
export async function saveCalendarDialog(send, target, cells, suggested) {
  if (cells === 0) {
    await confirmAction({ title: 'No hi ha res per desar', message: 'Encara no hi ha cap casella marcada en aquest calendari.', confirm: 'D\'acord', info: true });
    return null;
  }
  const list = await loadList(send);
  if (!list) return null;
  const name = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
  name.maxLength = 100;
  name.value = suggested;
  // Els noms que ja hi ha, per triar-ne un i substituir-lo
  const options = el('datalist', '');
  options.id = `sc-names-${Date.now()}`;
  options.append(...list.map((c) => {
    const o = /** @type {HTMLOptionElement} */ (el('option', ''));
    o.value = c.name;
    return o;
  }));
  name.setAttribute('list', options.id);
  const warning = el('div', 'form-text');
  const check = () => {
    const same = findByName(list, name.value);
    warning.replaceChildren(...(same ? [icon('bi-exclamation-triangle-fill'), ` Ja hi ha un calendari desat amb aquest nom (${cellsText(same.cells)}): se substituirà.`] : []));
    warning.classList.toggle('text-warning', Boolean(same));
  };
  name.addEventListener('input', check);
  check();

  for (;;) {
    const ok = await formDialog({
      title: 'Desa com a calendari',
      icon: 'bi-bookmark-plus',
      message: `Es desen les ${cellsText(cells)} marcades ara (les hores i els dies, no els esdeveniments), per aplicar-les després al calendari d'una altra pista o campanya.`,
      body: [dialogField('Nom del calendari', name), options, warning],
      confirm: 'Desa',
    });
    if (!ok) return null;
    if (!name.value.trim()) {
      await confirmAction({ title: 'Falta el nom', message: 'Posa un nom al calendari.', confirm: 'D\'acord', info: true });
      continue;
    }
    const same = findByName(list, name.value);
    try {
      return (await send('savedCalendars.save', { ...target, name: name.value, ...(same ? { calendarId: same.id } : {}) })).calendar;
    } catch (err) {
      await showError('No s\'ha pogut desar el calendari', err);
      return null;
    }
  }
}

/**
 * «Aplica un calendari desat…»: quin calendari i com (afegeix o substitueix). També s'hi pot esborrar un calendari desat.
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {{ campaignId: number } | { trackId: number }} target
 * @param {string} word  «pista» o «campanya»
 * @returns {Promise<any | null>}  el resultat de savedCalendars.apply (el calendari i «applied»), o null si no s'ha aplicat
 */
export async function applyCalendarDialog(send, target, word) {
  let list = await loadList(send);
  if (!list) return null;
  if (list.length === 0) {
    await confirmAction({
      title: 'No hi ha cap calendari desat',
      message: 'Per desar-ne un, obre el calendari d\'una pista o d\'una campanya que ja tingui les caselles marcades i prem «Desa com a calendari…».',
      confirm: 'D\'acord', info: true,
    });
    return null;
  }

  const select = /** @type {HTMLSelectElement} */ (el('select', 'form-select'));
  const preview = el('div', 'form-text sc-preview');
  const fill = () => {
    const current = select.value;
    select.replaceChildren(...(list ?? []).map((c) => {
      const o = /** @type {HTMLOptionElement} */ (el('option', '', `${c.name} (${cellsText(c.cells)})`));
      o.value = String(c.id);
      return o;
    }));
    if ([...select.options].some((o) => o.value === current)) select.value = current;
    show();
  };
  const show = () => {
    const c = list?.find((x) => String(x.id) === select.value);
    preview.textContent = c ? slotsText(c.slots) : '';
  };
  select.addEventListener('change', show);

  // Esborrar el calendari triat (no toca els esdeveniments)
  const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link text-danger p-0 mt-1'));
  remove.type = 'button';
  remove.append(icon('bi-trash'), ' Esborra aquest calendari desat…');
  remove.addEventListener('click', async () => {
    const c = list?.find((x) => String(x.id) === select.value);
    if (!c) return;
    if (!(await confirmAction({
      title: `Esborrar el calendari «${c.name}»?`,
      message: 'Només s\'esborra el calendari desat: el que ja s\'hi havia aplicat als esdeveniments es queda.',
      confirm: 'Esborra',
    }))) return;
    try {
      await send('savedCalendars.delete', { calendarId: c.id });
      list = (list ?? []).filter((x) => x.id !== c.id);
      fill();
      remove.disabled = list.length === 0;
    } catch (err) {
      await showError('No s\'ha pogut esborrar', err);
    }
  });

  // Com: afegeix (per defecte) o substitueix
  const mode = el('div', 'mt-2');
  const radio = (/** @type {string} */ value, /** @type {string} */ text, /** @type {string} */ help, /** @type {boolean} */ checked) => {
    const wrap = el('div', 'form-check');
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    input.type = 'radio';
    input.name = 'sc-mode';
    input.value = value;
    input.checked = checked;
    input.id = `sc-mode-${value}`;
    const label = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label'));
    label.htmlFor = input.id;
    label.append(el('strong', '', text), el('span', 'text-body-secondary', ` — ${help}`));
    wrap.append(input, label);
    return wrap;
  };
  mode.append(el('div', 'form-label', 'Com s\'aplica'),
    radio('add', 'Afegeix', `les caselles que ja té la ${word} es queden`, true),
    radio('replace', 'Substitueix', `es treuen les caselles de la ${word} que no són al calendari desat`, false));

  fill();
  const ok = await formDialog({
    title: 'Aplica un calendari desat',
    icon: 'bi-bookmark-check',
    message: `Es marquen les caselles del calendari desat, com si les cliquessis. Si en alguna hora no hi ha esdeveniment, es crea el bloc de falques.`,
    body: [dialogField('Calendari', select), preview, remove, mode],
    confirm: 'Aplica',
  });
  if (!ok || !select.value) return null;
  const chosen = /** @type {HTMLInputElement | null} */ (mode.querySelector('input:checked'))?.value ?? 'add';
  try {
    return await send('savedCalendars.apply', { ...target, calendarId: Number(select.value), mode: chosen });
  } catch (err) {
    await showError('No s\'ha pogut aplicar el calendari', err);
    return null;
  }
}
