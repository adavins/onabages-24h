// @ts-check
// Pregunta per quant temps s'atura alguna cosa (fase 3, pas 2b; D-073): l'AutoDJ i, al pas 2c, els esdeveniments.
// Mai indefinidament: sempre amb una durada, i després es torna a activar sol.

import { $ } from './dom.js';

/** Opcions (minuts i text). La de 2 h és la que surt triada. */
const OPTIONS = [[30, '30 minuts'], [60, '1 hora'], [120, '2 hores'], [240, '4 hores'], [480, '8 hores'], [720, '12 hores']];

/**
 * Pregunta quant temps. Retorna els minuts, o null si es cancel·la.
 * @param {{ title: string, message: string, confirm: string, defaultMinutes?: number }} options
 * @returns {Promise<number | null>}
 */
export function askDuration({ title, message, confirm, defaultMinutes = 120 }) {
  const dialog = /** @type {HTMLDialogElement} */ ($('duration-dialog'));
  $('duration-title').textContent = title;
  $('duration-message').textContent = message;
  $('duration-ok').textContent = confirm;
  const select = /** @type {HTMLSelectElement} */ ($('duration-select'));
  select.replaceChildren(...OPTIONS.map(([minutes, text]) => {
    const option = document.createElement('option');
    option.value = String(minutes);
    option.textContent = String(text);
    return option;
  }));
  select.value = String(defaultMinutes);
  const until = $('duration-until');
  const showUntil = () => {
    const end = new Date(Date.now() + Number(select.value) * 60_000);
    until.textContent = `Es tornarà a activar sol a les ${String(end.getHours()).padStart(2, '0')}:${String(end.getMinutes()).padStart(2, '0')}.`;
  };
  select.onchange = showUntil;
  showUntil();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok' ? Number(select.value) : null), { once: true });
    dialog.returnValue = '';
    dialog.showModal();
    /** @type {HTMLButtonElement} */ ($('duration-cancel')).focus(); // per defecte, el botó que no fa res
  });
}
