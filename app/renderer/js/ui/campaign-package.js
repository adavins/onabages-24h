// @ts-check
// Paquet de campanya (fase 4, pas 7a; D-150): exportar una campanya a un JSON a la carpeta compartida (Google Drive), al
// costat de les falques, i importar-lo en una altra emissora, amb l'informe del que es farà abans de fer res.
// Els fitxers els llegeix i els escriu el motor; aquí només es tria on són (diàleg de Windows, al procés principal).

import { el, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { formDialog, dialogField } from './form-dialog.js';
import { packageFileName, spotStatusInfo, exportSummary, importPlan } from '../campaign-package-form.js';
import { slotsText, appliedText } from '../saved-calendar-form.js';
import { datesText } from '../track-form.js';
import { localDateTime } from '../history-form.js';

/**
 * Exporta el paquet d'una campanya desada. Si l'editor té canvis sense desar, avisa que no hi seran.
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {(suggestedName: string) => Promise<string | null>} chooseSave  on es desa (diàleg de Windows)
 * @param {{ id: number, name: string }} campaign
 * @param {boolean} dirty
 */
export async function exportPackage(send, chooseSave, campaign, dirty) {
  if (dirty && !(await confirmAction({
    title: 'Canvis sense desar',
    message: 'El paquet es fa amb la campanya tal com està desada: els canvis que no has desat no hi seran.',
    confirm: 'Continua', danger: false,
  }))) return;
  const path = await chooseSave(packageFileName(campaign.name));
  if (!path) return;
  let result;
  try {
    result = await send('campaigns.exportPackage', { campaignId: campaign.id, path });
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut exportar el paquet', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return;
  }
  const { summary, warning } = exportSummary(result);
  await confirmAction({
    title: 'Paquet desat',
    message: `${summary}\n\n${warning ?? 'A l\'altra emissora, importa\'l des de la pestanya Campanyes («Importa un paquet…»).'}\n\n${result.path}`,
    confirm: 'D\'acord', info: true,
  });
}

/**
 * Importa un paquet: es tria el fitxer, el motor en fa una prova i es mostra l'informe; si es confirma, s'importa.
 * Retorna l'id de la campanya creada (per obrir-la), o null.
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {() => Promise<string | null>} chooseOpen  quin fitxer (diàleg de Windows)
 * @returns {Promise<number | null>}
 */
export async function importPackage(send, chooseOpen) {
  const path = await chooseOpen();
  if (!path) return null;
  /** @type {any} */
  let report;
  /** @type {any} */
  let filters = null;
  try {
    report = await send('campaigns.importPackage', { path, dryRun: true });
    filters = await send('library.filters').catch(() => null);
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut llegir el paquet', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return null;
  }
  const plan = importPlan(report);

  // L'informe: la campanya, d'on ve, les falques (cadascuna amb què hi passarà) i el calendari
  const c = report.campaign;
  const head = el('div', 'pk-head');
  head.append(el('strong', '', c.name || '(sense nom)'));
  if (c.advertiser && c.advertiser !== c.name) head.append(el('div', 'text-body-secondary', c.advertiser));
  head.append(el('div', '', c.startDate && c.endDate ? datesText(c.startDate, c.endDate) : ''));
  if (report.station || report.exported) {
    const when = report.exported ? localDateTime(report.exported).date : '';
    head.append(el('div', 'form-text m-0', `Exportat${report.station ? ` per ${report.station}` : ''}${when ? ` el ${when}` : ''}.`));
  }
  const list = el('div', 'pk-spots');
  for (const s of report.spots) {
    const info = spotStatusInfo(s.status);
    const row = el('div', 'pk-spot');
    const name = el('div', 'pk-spot-name');
    name.append(el('span', '', s.artist ? `${s.artist} - ${s.title}` : s.title || s.file), el('span', 'pk-spot-file', s.file));
    row.append(name, statusLabel(info.className, info.icon, info.label));
    if (s.reason) row.append(el('div', 'form-text m-0', s.reason));
    list.append(row);
  }
  if (report.spots.length === 0) list.append(el('div', 'form-text m-0', 'El paquet no porta cap falca.'));
  const calendar = el('div', 'pk-calendar', report.calendar.cells > 0
    ? `Calendari: ${slotsText(report.calendar.slots)}. On no hi hagi cap esdeveniment a aquella hora, es crearà un bloc de falques.`
    : 'El paquet no porta cap casella del calendari.');

  /** @type {HTMLElement[]} */
  const body = [head, el('h3', 'h6 mt-3 mb-1', 'Falques'), list, el('h3', 'h6 mt-3 mb-1', 'Calendari'), calendar];
  // Les pistes noves: a quina subcategoria (opcional; si no, queden «sense classificar»)
  const subcategory = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
  if (plan.created > 0) {
    subcategory.append(new Option('(sense classificar)', ''));
    let preferred = '';
    for (const cat of filters?.categories ?? []) {
      const group = document.createElement('optgroup');
      group.label = cat.name;
      for (const sub of cat.subcategories ?? []) {
        group.append(new Option(sub.name, String(sub.id)));
        if (!preferred && sub.mainType === 'commercial') preferred = String(sub.id);
      }
      subcategory.append(group);
    }
    subcategory.value = preferred;
    body.push(dialogField('Subcategoria de les pistes noves', subcategory, 'Hi pots posar la de les falques d\'aquesta emissora. Es pot canviar després a la pestanya Pistes.'));
  }
  body.push(el('p', `mt-3 mb-0 ${plan.missing > 0 ? 'text-warning' : ''}`, plan.found > 0 ? plan.text : 'No es pot importar: no s\'ha trobat cap falca al costat del paquet.'));

  const ok = await formDialog({
    title: 'Importa un paquet de campanya',
    icon: 'bi-box-arrow-in-down',
    body,
    wide: true,
    confirm: plan.found > 0 ? 'Importa' : null,
  });
  if (!ok) return null;

  let result;
  try {
    result = await send('campaigns.importPackage', { path, subcategoryId: subcategory.value === '' ? null : Number(subcategory.value) });
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut importar el paquet', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return null;
  }
  const applied = appliedText(result.applied);
  await confirmAction({
    title: 'Campanya importada',
    message: `S'ha creat la campanya «${c.name}» en esborrany. Revisa-la i, quan estigui bé, posa-la activa.\n\n` +
      `Calendari: ${applied.summary}${applied.problems.length > 0 ? `\n${applied.problems.join('\n')}` : ''}`,
    confirm: 'D\'acord', info: true,
  });
  return result.campaignId;
}
