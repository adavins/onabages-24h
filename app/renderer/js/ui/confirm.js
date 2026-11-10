// @ts-check
// Confirmació de les accions que interrompen l'emissió (saltar, treure de la cua, aturar l'AutoDJ).
// Fa servir l'element <dialog> del navegador: Esc o «Cancel·la» no fan res.

import { $ } from './dom.js';

/**
 * Pregunta a l'operador. Retorna true si confirma.
 * @param {{ title: string, message: string, confirm: string, danger?: boolean, info?: boolean }} options  info: només un avís (sense «Cancel·la»)
 * @returns {Promise<boolean>}
 */
export function confirmAction({ title, message, confirm, danger = true, info = false }) {
  const dialog = /** @type {HTMLDialogElement} */ ($('confirm-dialog'));
  $('confirm-title').textContent = title;
  $('confirm-message').textContent = message;
  const ok = /** @type {HTMLButtonElement} */ ($('confirm-ok'));
  ok.textContent = confirm;
  ok.className = `btn ${danger && !info ? 'btn-danger' : 'btn-primary'}`;
  const cancel = /** @type {HTMLButtonElement} */ ($('confirm-cancel'));
  cancel.classList.toggle('d-none', info);

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true });
    dialog.returnValue = '';
    dialog.showModal();
    (info ? ok : cancel).focus(); // per defecte, el botó que no fa res
  });
}
