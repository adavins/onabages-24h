// @ts-check
// Còpies de seguretat (fase 4, pas 7b; D-152): la pestanya «Còpies de seguretat» de ⚙ Configuració. La llista de còpies, fer-ne
// una ara, restaurar-ne una sencera (o un altre fitxer) i obrir la carpeta. Les còpies les fa i les restaura el motor.

import { el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { formDialog } from './form-dialog.js'; // per a la confirmació de restaurar
import { backupDateText, kindText, sizeText, inspectLines, restoredText } from '../backups-form.js';

/**
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {{ chooseFile: () => Promise<string | null>, showFile: (path: string) => Promise<string> }} files
 * @param {() => void} onRestored  després de restaurar una còpia (la configuració es tanca sense desar: les dades han canviat)
 * @returns {HTMLElement}  la pestanya (la llista es llegeix sola)
 */
export function backupsPanel(send, files, onRestored) {
  const state = el('div', 'bk-state');
  const list = el('div', 'bk-list');
  /** @type {{ folder: string, backups: any[] }} */
  let data = { folder: '', backups: [] };

  const load = async () => {
    try {
      data = await send('backups.list');
    } catch (err) {
      list.replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'ha pogut llegir la llista: ${/** @type {Error} */ (err).message}`));
      return;
    }
    folderText.textContent = data.folder;
    if (data.backups.length === 0) {
      list.replaceChildren(el('p', 'form-text m-0', 'Encara no hi ha cap còpia. El motor en fa una cada dia; també en pots fer una ara.'));
      return;
    }
    list.replaceChildren(...data.backups.map((b) => {
      const row = el('div', 'bk-row');
      const info = el('div', 'bk-info');
      info.append(el('strong', '', backupDateText(b.created)),
        el('span', 'text-body-secondary', `${kindText(b.kind)} · ${sizeText(b.size)}`));
      const restore = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-danger'));
      restore.type = 'button';
      restore.append(icon('bi-arrow-counterclockwise'), ' Restaura…');
      restore.title = b.name;
      restore.addEventListener('click', () => restoreFlow(b.path));
      row.append(info, restore);
      return row;
    }));
  };

  /** @param {string} path */
  const restoreFlow = async (path) => {
    if (await restore(send, path)) onRestored();
  };

  // A dalt: on són i les eines
  const folderText = el('span', 'bk-folder');
  const where = el('div', 'form-text mt-0 mb-2');
  where.append('El motor en fa una cada dia i en guarda les dels últims 14 dies i les de cada mes de l\'últim any. Són a ', folderText, '.');
  const tools = el('div', 'bk-tools');
  const create = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  create.type = 'button';
  create.append(icon('bi-floppy'), ' Fes una còpia ara');
  create.addEventListener('click', async () => {
    create.disabled = true;
    state.replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Fent la còpia…'));
    try {
      await send('backups.create');
      state.replaceChildren(statusLabel('text-success', 'bi-check-circle', 'Còpia feta.'));
      await load();
    } catch (err) {
      state.replaceChildren(statusLabel('text-danger', 'bi-x-circle', `No s'ha pogut fer la còpia: ${/** @type {Error} */ (err).message}`));
    } finally {
      create.disabled = false;
    }
  });
  const other = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  other.type = 'button';
  other.append(icon('bi-file-earmark-arrow-up'), ' Restaura un altre fitxer…');
  other.title = 'Una còpia que no és en aquesta carpeta (p. ex. d\'un altre ordinador o d\'un disc extern)';
  other.addEventListener('click', async () => {
    const path = await files.chooseFile();
    if (path) await restoreFlow(path);
  });
  const folder = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  folder.type = 'button';
  folder.append(icon('bi-folder2-open'), ' Obre la carpeta');
  folder.addEventListener('click', () => {
    // Amb una còpia, l'Explorador la marca; sense cap, s'obre la carpeta
    if (data.folder) files.showFile(data.backups[0]?.path ?? `${data.folder}\\LLEGEIX-ME.txt`);
  });
  tools.append(create, other, folder, state);

  load();
  const element = el('div', 'cfg-backups');
  element.append(where, tools, list);
  return element;
}

/**
 * Restaurar una còpia sencera: es mira què hi ha, es pregunta (amb la casella de l'historial) i es torna a preguntar,
 * perquè no es pot desfer si no és restaurant l'altra còpia. Retorna si s'ha restaurat.
 * @param {(cmd: string, params?: object) => Promise<any>} send @param {string} path
 */
async function restore(send, path) {
  let info;
  try {
    info = await send('backups.inspect', { path });
  } catch (err) {
    await confirmAction({ title: 'No es pot llegir la còpia', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return false;
  }
  if (info.problem) {
    await confirmAction({ title: 'Aquesta còpia no es pot restaurar', message: info.problem, confirm: 'D\'acord', info: true });
    return false;
  }

  const keep = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  keep.type = 'checkbox';
  keep.checked = true;
  keep.id = 'bk-keep-history';
  const keepLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Conserva l\'historial d\'ara: el que ha sonat i el registre dels esdeveniments després de la còpia (per als certificats)'));
  keepLabel.htmlFor = keep.id;
  const keepBox = el('div', 'form-check mt-2');
  keepBox.append(keep, keepLabel);

  const contents = el('ul', 'bk-contents');
  contents.append(...inspectLines(info).map((line) => el('li', '', line)));
  const ok = await formDialog({
    title: 'Restaurar la còpia sencera?',
    icon: 'bi-exclamation-triangle',
    message: 'Tot tornarà a com era a la còpia: pistes, esdeveniments, rotacions, campanyes, calendaris desats i configuració. ' +
      'La sortida d\'àudio es queda com ara, i l\'emissió no s\'atura. Abans es fa una còpia de com està ara, per poder desfer-ho.',
    body: [el('div', 'bk-file', path), contents, keepBox],
    confirm: 'Restaura…',
    danger: true,
  });
  if (!ok) return false;
  // Segona confirmació: és l'acció més grossa de l'aplicació
  if (!(await confirmAction({
    title: 'Segur que vols restaurar-la?',
    message: 'Es perdran tots els canvis fets després de la còpia (esdeveniments, campanyes, pistes…). Només es podran recuperar restaurant la còpia «Abans de restaurar» que es farà ara.',
    confirm: 'Sí, restaura-la',
  }))) return false;

  let result;
  try {
    result = await send('backups.restore', { path, keepHistory: keep.checked });
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut restaurar', message: `${/** @type {Error} */ (err).message}\n\nNo s'ha tocat res.`, confirm: 'D\'acord', info: true });
    return false;
  }
  await confirmAction({ title: 'Còpia restaurada', message: restoredText(result, keep.checked), confirm: 'D\'acord', info: true });
  return true;
}
