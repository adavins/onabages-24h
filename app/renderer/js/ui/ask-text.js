// @ts-check
// Demana un text en un diàleg (p. ex. el nom d'una categoria). Esc o «Cancel·la» no fan res.

import { $ } from './dom.js';

/**
 * @param {{ title: string, label: string, confirm: string, value?: string }} options
 * @returns {Promise<string | null>}  el text (sense espais als extrems), o null si es cancel·la o és buit
 */
export function askText({ title, label, confirm, value = '' }) {
  const dialog = /** @type {HTMLDialogElement} */ ($('text-dialog'));
  $('text-title').textContent = title;
  $('text-label').textContent = label;
  $('text-ok').textContent = confirm;
  const input = /** @type {HTMLInputElement} */ ($('text-input'));
  input.value = value;
  // Retorn = confirmar (sense això, el formulari faria servir el primer botó, «Cancel·la»)
  input.onkeydown = (e) => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    dialog.close('ok');
  };

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      const text = input.value.trim();
      resolve(dialog.returnValue === 'ok' && text ? text : null);
    }, { once: true });
    dialog.returnValue = '';
    dialog.showModal();
    input.select();
  });
}
