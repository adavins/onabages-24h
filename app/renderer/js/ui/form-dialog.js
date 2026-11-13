// @ts-check
// Una finestra modal genèrica (element <dialog>, D-111): títol, el contingut que es vulgui (camps, una llista…),
// «Cancel·la» i, si cal, un botó per confirmar. Esc o «Cancel·la» no fan res. Es treu de la pàgina en tancar-se.

import { el, icon } from './dom.js';

/**
 * @param {{
 *   title: string, icon?: string, message?: string, body?: HTMLElement[],
 *   confirm?: string | null, danger?: boolean, wide?: boolean, cancel?: string
 * }} options  confirm: el text del botó per confirmar (null = només «Tanca»)
 * @returns {Promise<boolean>}  true si es confirma
 */
export function formDialog({ title, icon: iconName, message, body = [], confirm = null, danger = false, wide = false, cancel }) {
  const dialog = /** @type {HTMLDialogElement} */ (el('dialog', `ob-dialog${wide ? ' ob-dialog-wide' : ''}`));
  const form = el('form', '');
  form.setAttribute('method', 'dialog');
  const h = el('h2', 'h5');
  if (iconName) h.append(icon(iconName), ' ');
  h.append(el('span', '', title));
  form.append(h);
  if (message) form.append(el('p', 'ob-dialog-message', message));
  form.append(...body);
  const buttons = el('div', 'd-flex justify-content-end gap-2 mt-3');
  const no = el('button', 'btn btn-outline-secondary', cancel ?? (confirm ? 'Cancel·la' : 'Tanca'));
  no.setAttribute('value', 'cancel');
  buttons.append(no);
  if (confirm) {
    const ok = el('button', `btn ${danger ? 'btn-danger' : 'btn-primary'}`, confirm);
    ok.setAttribute('value', 'ok');
    buttons.append(ok);
  }
  form.append(buttons);
  // Retorn en un camp de text = confirmar (sense això, el formulari faria servir el primer botó, «Cancel·la»)
  form.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && confirm && /** @type {HTMLElement} */ (e.target).tagName === 'INPUT') {
      e.preventDefault();
      dialog.close('ok');
    }
  });
  dialog.append(form);
  document.body.append(dialog);
  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      resolve(dialog.returnValue === 'ok');
      dialog.remove();
    }, { once: true });
    dialog.returnValue = '';
    dialog.showModal();
    /** @type {HTMLElement | null} */ (form.querySelector('input, select'))?.focus();
  });
}

/**
 * Un camp amb l'etiqueta a sobre, per posar-lo dins de {@link formDialog}.
 * @param {string} label @param {HTMLElement} control @param {string} [help]
 */
export function dialogField(label, control, help) {
  const box = el('div', 'mb-2');
  const id = `fd-${Math.random().toString(36).slice(2, 9)}`;
  control.id = id;
  const l = /** @type {HTMLLabelElement} */ (el('label', 'form-label', label));
  l.htmlFor = id;
  box.append(l, control);
  if (help) box.append(el('div', 'form-text', help));
  return box;
}
