// @ts-check
// Dades de l'emissora (fase 4, pas 4b; D-138): el nom, la freqüència i el logotip que surten a la capçalera dels informes.
// Es desen al motor (station.set); el logotip es tria amb el diàleg de Windows (el procés principal el llegeix).

import { el, icon, statusLabel } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';
import { confirmAction } from './confirm.js';

/**
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {() => Promise<{ dataUrl?: string, error?: string } | null>} chooseImage
 */
export async function openStationDialog(send, chooseImage) {
  /** @type {{ name: string, frequency: string, logo: string | null }} */
  let station;
  try {
    station = (await send('station.get')).station;
  } catch (err) {
    await confirmAction({ title: 'No s\'han pogut llegir les dades', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return;
  }

  const name = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
  name.maxLength = 100;
  name.value = station.name;
  const frequency = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
  frequency.maxLength = 100;
  frequency.value = station.frequency;
  frequency.placeholder = 'p. ex. 90.2 FM';

  // El logotip: la imatge (o «Sense logotip»), «Tria la imatge…» i «Treu-lo»
  let logo = station.logo;
  const preview = el('div', 'st-logo');
  const problem = el('div', 'small');
  const pick = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  pick.type = 'button';
  pick.append(icon('bi-image'), ' Tria la imatge…');
  const remove = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  remove.type = 'button';
  remove.append(icon('bi-x-lg'), ' Treu-lo');
  const show = () => {
    if (logo) {
      const img = /** @type {HTMLImageElement} */ (el('img', 'st-logo-img'));
      img.src = logo;
      img.alt = 'Logotip de l\'emissora';
      preview.replaceChildren(img);
    } else preview.replaceChildren(el('span', 'text-body-secondary', 'Sense logotip'));
    remove.disabled = !logo;
  };
  show();
  pick.addEventListener('click', async () => {
    const result = await chooseImage();
    if (!result) return;
    if (result.error) {
      problem.replaceChildren(statusLabel('text-danger', 'bi-x-circle', result.error));
      return;
    }
    problem.replaceChildren();
    logo = result.dataUrl ?? null;
    show();
  });
  remove.addEventListener('click', () => {
    logo = null;
    show();
  });
  const logoTools = el('div', 'd-flex gap-2 mt-2');
  logoTools.append(pick, remove);
  const logoBox = el('div', 'mb-2');
  logoBox.append(el('div', 'form-label', 'Logotip'), preview, logoTools, problem,
    el('div', 'form-text', 'PNG, JPEG o SVG. Si és gran, es redueix sol (com a molt 800 × 400 píxels). Es desa a la configuració de l\'aplicació.'));

  const ok = await formDialog({
    title: 'Dades de l\'emissora',
    icon: 'bi-gear',
    message: 'Surten a la capçalera de l\'informe de programació i del certificat d\'emissió.',
    body: [dialogField('Nom de l\'emissora', name), dialogField('Freqüència', frequency), logoBox],
    confirm: 'Desa',
  });
  if (!ok) return;
  try {
    await send('station.set', { name: name.value, frequency: frequency.value, logo });
  } catch (err) {
    await confirmAction({ title: 'No s\'han pogut desar les dades', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
  }
}
