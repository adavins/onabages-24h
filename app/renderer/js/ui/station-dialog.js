// @ts-check
// Configuració (botó ⚙): les dades de l'emissora (fase 4, pas 4b; D-138), el nom, la freqüència i el logotip que surten
// a la capçalera dels informes, les dates de la temporada (pas 6a; D-145), per al botó «Temporada», i l'accés a les
// còpies de seguretat (pas 7b; D-152), i el silenci a la sortida (fase 5, pas 3; D-158): a partir de quin nivell i quants
// segons el motor ho considera silenci; la carpeta de pistes d'emergència (fase 5, pas 4; D-159); i l'accés als avisos
// per correu (fase 5, pas 5; D-160), en una finestra a part.
// Es desen al motor (station.set, season.set); el logotip es tria amb el diàleg de Windows (el procés principal el llegeix).

import { el, icon, statusLabel } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';
import { confirmAction } from './confirm.js';
import { openMailDialog } from './mail-dialog.js';

/**
 * @param {(cmd: string, params?: object, timeoutMs?: number) => Promise<any>} send
 * @param {() => Promise<{ dataUrl?: string, error?: string } | null>} chooseImage
 * @param {(defaultPath?: string) => Promise<string | null>} chooseFolder  el diàleg de Windows per triar una carpeta
 * @param {() => Promise<void>} openBackups  la finestra «Còpies de seguretat»
 */
export async function openStationDialog(send, chooseImage, chooseFolder, openBackups) {
  /** @type {{ name: string, frequency: string, logo: string | null }} */
  let station;
  /** @type {{ startDate: string | null, endDate: string | null }} */
  let season;
  /** @type {{ thresholdDb: number, seconds: number, standardThresholdDb: number, standardSeconds: number }} */
  let silence;
  /** @type {{ folder: string, defaultFolder: string, count: number, problem: string | null }} */
  let emergency;
  /** @type {{ enabled: boolean, to: string[], problem: string | null }} */
  let mail;
  try {
    [station, season, silence, emergency, mail] = await Promise.all([send('station.get').then((r) => r.station), send('season.get').then((r) => r.season),
      send('silence.get'), send('emergency.get'), send('mail.get')]);
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

  // Silenci a la sortida (D-158): el nivell (dB) i els segons
  const silenceDb = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  silenceDb.type = 'number';
  silenceDb.min = '-70';
  silenceDb.max = '-30';
  silenceDb.step = '1';
  silenceDb.value = String(silence.thresholdDb);
  const silenceSeconds = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  silenceSeconds.type = 'number';
  silenceSeconds.min = '5';
  silenceSeconds.max = '120';
  silenceSeconds.step = '1';
  silenceSeconds.value = String(silence.seconds);
  const silenceFields = el('div', 'st-season');
  silenceFields.append(dialogField('Nivell (dB, de −70 a −30)', silenceDb), dialogField('Segons (de 5 a 120)', silenceSeconds));
  const silenceBox = el('div', 'mb-2');
  silenceBox.append(el('h3', 'h6 mt-3 mb-1', 'Silenci a la sortida'), silenceFields,
    el('div', 'form-text', `Si el so que surt es queda per sota d'aquest nivell aquests segons seguits, el motor passa a la pista següent i ho anota a les incidències (un stream només s'anota). Per defecte, ${silence.standardThresholdDb} dB i ${silence.standardSeconds} s.`));

  // Pistes d'emergència (D-159): la carpeta (es desa de seguida en triar-la, com a les carpetes vigilades)
  const emergencyPath = el('div', 'small text-break');
  const emergencyState = el('div', 'small');
  const showEmergency = () => {
    emergencyPath.textContent = emergency.folder;
    emergencyState.replaceChildren(emergency.problem
      ? statusLabel('text-warning', 'bi-exclamation-triangle-fill', emergency.problem)
      : statusLabel('text-success', 'bi-check-circle', `${emergency.count} ${emergency.count === 1 ? 'pista' : 'pistes'}`));
  };
  showEmergency();
  /** @param {string | null} folder */
  const setEmergency = async (folder) => {
    try {
      emergency = await send('emergency.set', { folder });
      showEmergency();
    } catch (err) {
      emergencyState.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
    }
  };
  const emergencyPick = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  emergencyPick.type = 'button';
  emergencyPick.append(icon('bi-folder2-open'), ' Tria la carpeta…');
  emergencyPick.addEventListener('click', async () => {
    const folder = await chooseFolder(emergency.folder);
    if (folder) await setEmergency(folder);
  });
  const emergencyDefault = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  emergencyDefault.type = 'button';
  emergencyDefault.append(icon('bi-arrow-counterclockwise'), ' La per defecte');
  emergencyDefault.title = emergency.defaultFolder;
  emergencyDefault.addEventListener('click', () => setEmergency(null));
  const emergencyTools = el('div', 'd-flex gap-2 mt-1');
  emergencyTools.append(emergencyPick, emergencyDefault);
  const emergencyBox = el('div', 'mb-2');
  emergencyBox.append(el('h3', 'h6 mt-3 mb-1', 'Pistes d\'emergència'), emergencyPath, emergencyState, emergencyTools,
    el('div', 'form-text', 'Cançons d\'un disc d\'aquest ordinador (no de xarxa). Si una pista tarda a obrir-se, en sona una d\'aquestes fins que la que tocava és a punt. Posa-n\'hi unes quantes: el canvi de carpeta es desa de seguida.'));

  // Avisos per correu (D-160): l'estat i el botó que obre la seva finestra (es desa allà, a part d'aquesta)
  const mailState = el('div', 'small');
  const showMail = () => {
    mailState.replaceChildren(!mail.enabled
      ? statusLabel('text-secondary', 'bi-slash-circle', 'Desactivats')
      : mail.problem
        ? statusLabel('text-warning', 'bi-exclamation-triangle-fill', mail.problem)
        : statusLabel('text-success', 'bi-check-circle', `Actius: a ${mail.to.join(', ')}`));
  };
  showMail();
  const mailOpen = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary mt-1'));
  mailOpen.type = 'button';
  mailOpen.append(icon('bi-envelope-exclamation'), ' Avisos per correu…');
  mailOpen.addEventListener('click', async () => {
    await openMailDialog(send);
    try {
      mail = await send('mail.get');
      showMail();
    } catch { /* es queda com estava */ }
  });
  const mailBox = el('div', 'mb-2');
  mailBox.append(el('h3', 'h6 mt-3 mb-1', 'Avisos per correu'), mailState, mailOpen);

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
        dialogField('Nom de l\'emissora', name), dialogField('Freqüència', frequency), logoBox, seasonBox, silenceBox, emergencyBox, mailBox, backupsBox,
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
      await send('silence.set', { thresholdDb: Number(silenceDb.value), seconds: Number(silenceSeconds.value) });
      return;
    } catch (err) {
      // P. ex. una temporada amb una sola data: es torna a obrir amb el que s'havia escrit
      await confirmAction({ title: 'No s\'han pogut desar les dades', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}
