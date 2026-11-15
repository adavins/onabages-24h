// @ts-check
// Configuració (botó ⚙): les dades de l'emissora (fase 4, pas 4b; D-138), el nom, la freqüència i el logotip que surten
// a la capçalera dels informes, les dates de la temporada (pas 6a; D-145), per al botó «Temporada», i l'accés a les
// còpies de seguretat (pas 7b; D-152).
// Es desen al motor (station.set, season.set); el logotip es tria amb el diàleg de Windows (el procés principal el llegeix).

import { el, icon, statusLabel } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';
import { confirmAction } from './confirm.js';

/**
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {() => Promise<{ dataUrl?: string, error?: string } | null>} chooseImage
 * @param {() => Promise<void>} openBackups  la finestra «Còpies de seguretat»
 */
export async function openStationDialog(send, chooseImage, openBackups) {
  /** @type {{ name: string, frequency: string, logo: string | null }} */
  let station;
  /** @type {{ startDate: string | null, endDate: string | null }} */
  let season;
  try {
    [station, season] = await Promise.all([send('station.get').then((r) => r.station), send('season.get').then((r) => r.season)]);
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

  // La temporada: dues dates (o cap)
  const seasonFrom = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  seasonFrom.type = 'date';
  seasonFrom.value = season.startDate ?? '';
  const seasonTo = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  seasonTo.type = 'date';
  seasonTo.value = season.endDate ?? '';
  const seasonDates = el('div', 'st-season');
  seasonDates.append(dialogField('Comença', seasonFrom), dialogField('Acaba', seasonTo));
  const seasonBox = el('div', 'mb-2');
  seasonBox.append(el('h3', 'h6 mt-3 mb-1', 'Temporada'), seasonDates,
    el('div', 'form-text', 'El botó «Temporada» de les dates d\'una pista o d\'una campanya posa aquestes. Buides = sense temporada.'));

  // Còpies de seguretat (D-152): es tanca la configuració abans, perquè després de restaurar no es desin les dades
  // d'abans que hi havia a la finestra
  const backups = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  backups.type = 'button';
  backups.append(icon('bi-database-check'), ' Còpies de seguretat…');
  backups.title = 'Fer una còpia ara o restaurar-ne una sencera (es tanca la configuració sense desar)';
  let wantBackups = false;
  backups.addEventListener('click', () => {
    wantBackups = true;
    /** @type {HTMLDialogElement | null} */ (backups.closest('dialog'))?.close();
  });
  const backupsBox = el('div', 'mb-2');
  backupsBox.append(el('h3', 'h6 mt-3 mb-1', 'Còpies de seguretat'), backups,
    el('div', 'form-text', 'El motor en fa una cada dia. Des d\'aquí en pots fer una ara o restaurar-ne una sencera.'));

  for (;;) {
    const ok = await formDialog({
      title: 'Configuració',
      icon: 'bi-gear',
      body: [
        el('h3', 'h6 mb-1', 'Dades de l\'emissora'),
        el('div', 'form-text mb-2', 'Surten a la capçalera de l\'informe de programació, del certificat d\'emissió i de l\'historial.'),
        dialogField('Nom de l\'emissora', name), dialogField('Freqüència', frequency), logoBox, seasonBox, backupsBox,
      ],
      confirm: 'Desa',
    });
    if (wantBackups) {
      await openBackups();
      return;
    }
    if (!ok) return;
    try {
      await send('station.set', { name: name.value, frequency: frequency.value, logo });
      await send('season.set', { startDate: seasonFrom.value || null, endDate: seasonTo.value || null });
      return;
    } catch (err) {
      // P. ex. una temporada amb una sola data: es torna a obrir amb el que s'havia escrit
      await confirmAction({ title: 'No s\'han pogut desar les dades', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}
