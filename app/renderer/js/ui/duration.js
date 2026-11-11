// @ts-check
// Pregunta per quant temps s'atura alguna cosa (fase 3, pas 2b; D-073): l'AutoDJ i, al pas 2c, els esdeveniments.
// Mai indefinidament: sempre amb una durada, i després es torna a activar sol.

import { $ } from './dom.js';

/** Opcions (minuts i text). La de 2 h és la que surt triada. */
const OPTIONS = [[30, '30 minuts'], [60, '1 hora'], [120, '2 hores'], [240, '4 hores'], [480, '8 hores'], [720, '12 hores']];

/**
 * Pregunta quant temps. Retorna els minuts, o null si es cancel·la.
 * @param {{ title: string, message: string, confirm: string, defaultMinutes?: number, allowUntil?: boolean }} options
 *   allowUntil: també «Fins a una hora concreta» (els esdeveniments, D-073)
 * @returns {Promise<number | null>}
 */
export function askDuration({ title, message, confirm, defaultMinutes = 120, allowUntil = false }) {
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
  if (allowUntil) {
    const option = document.createElement('option');
    option.value = 'until';
    option.textContent = 'Fins a una hora concreta…';
    select.append(option);
  }
  select.value = String(defaultMinutes);
  const time = /** @type {HTMLInputElement} */ ($('duration-time'));
  time.value = '';
  /** Els minuts triats (amb una hora concreta: fins a la pròxima vegada que sigui aquella hora, com a molt 12 h). */
  const minutes = () => {
    if (select.value !== 'until') return Number(select.value);
    if (!time.value) return null;
    const [h, m] = time.value.split(':').map(Number);
    const now = new Date();
    const end = new Date(now.getFullYear(), now.getMonth(), now.getDate(), h, m);
    if (end <= now) end.setDate(end.getDate() + 1);
    const result = Math.ceil((end.getTime() - now.getTime()) / 60_000);
    return result <= 720 ? result : null;
  };
  const until = $('duration-until');
  const ok = /** @type {HTMLButtonElement} */ ($('duration-ok'));
  const showUntil = () => {
    time.classList.toggle('d-none', select.value !== 'until');
    const chosen = minutes();
    ok.disabled = chosen == null;
    if (chosen == null) {
      until.textContent = select.value === 'until' && time.value ? "Com a molt, 12 hores des d'ara." : "Tria l'hora.";
      return;
    }
    const end = new Date(Date.now() + chosen * 60_000);
    until.textContent = `Es tornarà a activar sol a les ${String(end.getHours()).padStart(2, '0')}:${String(end.getMinutes()).padStart(2, '0')}.`;
  };
  select.onchange = showUntil;
  time.oninput = showUntil;
  showUntil();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok' ? minutes() : null), { once: true });
    dialog.returnValue = '';
    dialog.showModal();
    /** @type {HTMLButtonElement} */ ($('duration-cancel')).focus(); // per defecte, el botó que no fa res
  });
}
