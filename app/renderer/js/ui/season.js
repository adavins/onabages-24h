// @ts-check
// Botó «Temporada» (fase 4, pas 6a; D-145): omple dues dates amb les de la temporada de la configuració (season.get),
// a la fitxa d'una pista i a l'editor d'una campanya. Les dates de la temporada es posen a ⚙ Configuració.

import { el, icon } from './dom.js';
import { confirmAction } from './confirm.js';

/**
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {HTMLInputElement} from  la data d'inici
 * @param {HTMLInputElement} to  la data de fi
 */
export function seasonButton(send, from, to) {
  const button = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary season-button'));
  button.type = 'button';
  button.append(icon('bi-calendar2-range'), ' Temporada');
  button.title = 'Posa-hi les dates de la temporada (es canvien a ⚙ Configuració)';
  button.addEventListener('click', async () => {
    let season;
    try {
      season = (await send('season.get')).season;
    } catch (err) {
      await confirmAction({ title: 'No s\'han pogut llegir les dates', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
      return;
    }
    if (!season.startDate || !season.endDate) {
      await confirmAction({
        title: 'Encara no hi ha dates de temporada',
        message: 'Posa-les a ⚙ Configuració (a dalt a la dreta), a «Temporada».',
        confirm: 'D\'acord', info: true,
      });
      return;
    }
    // Com si s'escrivissin: els formularis se n'adonen (canvis sense desar)
    for (const [input, value] of /** @type {[HTMLInputElement, string][]} */ ([[from, season.startDate], [to, season.endDate]])) {
      input.value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
    }
  });
  return button;
}
