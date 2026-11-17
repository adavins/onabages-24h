// @ts-check
// ⚙ Configuració: una sola finestra gran amb pestanyes, sense desplaçar-se (redisseny del 2026-11-17).
// - Dades generals: el nom, la freqüència i el logotip de l'emissora, que surten a la capçalera dels informes (D-138), i
//   les dates de la temporada, per al botó «Temporada» (D-145).
// - Avisos per correu (D-160): settings-mail.js.
// - Còpies de seguretat (D-152): backups.js.
// - Eines: el silenci a la sortida (D-158) i la carpeta de pistes d'emergència (D-159).
// - Usuaris (D-164): settings-users.js.
// - Sortida d'àudio (settings-output.js) i Processador (processor.js): abans, a la barra d'estat (D-168). Es canvien de
//   seguida, sense «Desa». L'operador i el programador només hi veuen «Sortida» (D-164: la sortida la pot canviar l'operador).
// «Desa» desa les dades generals, els avisos, el silenci i els minuts d'inactivitat (station.set, season.set, mail.set,
// silence.set, security.set). Les accions
// (triar la carpeta d'emergència, el correu de prova, fer o restaurar una còpia) es fan de seguida, com abans.

import { el, icon, statusLabel } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';
import { confirmAction } from './confirm.js';
import { mailPanel } from './settings-mail.js';
import { backupsPanel } from './backups.js';
import { usersPanel } from './settings-users.js';
import { outputPanel } from './settings-output.js';

/** @typedef {'general' | 'output' | 'processor' | 'mail' | 'backups' | 'tools' | 'users'} SettingsTab */

/** Les pestanyes, en ordre: id, text i icona. */
const TABS = /** @type {const} */ ([
  ['general', 'General', 'bi-broadcast'],
  ['output', 'Sortida', 'bi-speaker-fill'],
  ['processor', 'Processador', 'bi-sliders2-vertical'],
  ['mail', 'Correu', 'bi-envelope-exclamation'],
  ['backups', 'Còpies', 'bi-database-check'],
  ['tools', 'Eines', 'bi-tools'],
  ['users', 'Usuaris', 'bi-people'],
]);

/**
 * @param {(cmd: string, params?: object, timeoutMs?: number) => Promise<any>} send
 * @param {{
 *   chooseImage: () => Promise<{ dataUrl?: string, error?: string } | null>,
 *   chooseFolder: (defaultPath?: string) => Promise<string | null>,
 *   chooseBackup: () => Promise<string | null>,
 *   showFile: (path: string) => Promise<string>,
 *   processor: import('./processor.js').ProcessorView,
 * }} bridge  els diàlegs de Windows (el procés principal) i el processador (la seva pestanya)
 * @param {import('../session.js').SessionController} session  per a la pestanya «Usuaris» (crear el primer hi entra)
 * @param {SettingsTab} [firstTab]  la pestanya que s'obre (per defecte, «General»; sense ser administrador, «Sortida»)
 */
export async function openSettingsDialog(send, bridge, session, firstTab = 'general') {
  // Sense ser administrador (amb usuaris), només la sortida d'àudio: no es llegeix res més (el motor no ho deixaria)
  if (!session.can('administrator')) {
    const tabs = buildTabs([['output', 'Sortida', 'bi-speaker-fill']], { output: outputPanel(send) }, 'output');
    const closed = formDialog({ title: 'Configuració', icon: 'bi-gear', body: [tabs.bar, tabs.box], wide: true });
    tabs.bar.closest('dialog')?.classList.add('cfg-dialog');
    await closed;
    return;
  }

  /** @type {{ name: string, frequency: string, logo: string | null }} */
  let station;
  /** @type {{ startDate: string | null, endDate: string | null }} */
  let season;
  /** @type {{ thresholdDb: number, seconds: number, standardThresholdDb: number, standardSeconds: number }} */
  let silence;
  /** @type {{ folder: string, defaultFolder: string, count: number, problem: string | null }} */
  let emergency;
  /** @type {import('./settings-mail.js').MailConfig} */
  let mail;
  /** @type {import('./settings-users.js').UserRow[]} */
  let users;
  /** @type {{ inactivityMinutes: number, standardInactivityMinutes: number }} */
  let security;
  try {
    [station, season, silence, emergency, mail, users, security] = await Promise.all([send('station.get').then((r) => r.station),
      send('season.get').then((r) => r.season), send('silence.get'), send('emergency.get'), send('mail.get'),
      send('users.list').then((r) => r.users), send('security.get')]);
  } catch (err) {
    await confirmAction({ title: 'No s\'han pogut llegir les dades', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return;
  }

  // --- Dades generals ---

  const name = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  name.maxLength = 100;
  name.value = station.name;
  const frequency = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  frequency.maxLength = 100;
  frequency.value = station.frequency;
  frequency.placeholder = 'p. ex. 90.2 FM';
  const stationFields = el('div', 'cfg-pair');
  stationFields.append(dialogField('Nom de l\'emissora', name), dialogField('Freqüència', frequency));

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
    const result = await bridge.chooseImage();
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
    el('div', 'form-text', 'PNG, JPEG o SVG. Si és gran, es redueix sol (com a molt 800 × 400 píxels).'));

  // La temporada: dues dates (o cap)
  const seasonFrom = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  seasonFrom.type = 'date';
  seasonFrom.value = season.startDate ?? '';
  const seasonTo = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  seasonTo.type = 'date';
  seasonTo.value = season.endDate ?? '';
  const seasonDates = el('div', 'cfg-pair');
  seasonDates.append(dialogField('Comença', seasonFrom), dialogField('Acaba', seasonTo));

  const generalLeft = el('div', '');
  generalLeft.append(el('h3', 'h6 mb-1', 'Emissora'),
    el('div', 'form-text mt-0 mb-2', 'Surten a la capçalera de l\'informe de programació, del certificat d\'emissió i de l\'historial.'),
    stationFields, logoBox);
  const generalRight = el('div', '');
  generalRight.append(el('h3', 'h6 mb-1', 'Temporada'), seasonDates,
    el('div', 'form-text', 'El botó «Temporada» de les dates d\'una pista o d\'una campanya posa aquestes. Buides = sense temporada.'));
  const general = el('div', 'cfg-cols');
  general.append(generalLeft, generalRight);

  // --- Avisos per correu ---

  const mailTab = mailPanel(send, mail);

  // --- Còpies de seguretat: després de restaurar-ne una, es tanca sense desar (les dades de les pestanyes han canviat) ---

  let restored = false;
  const backups = backupsPanel(send, { chooseFile: bridge.chooseBackup, showFile: bridge.showFile }, () => {
    restored = true;
    /** @type {HTMLDialogElement | null} */ (tabs.bar.closest('dialog'))?.close();
  });

  // --- Eines: silenci a la sortida (D-158) i pistes d'emergència (D-159) ---

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
  const silenceFields = el('div', 'cfg-pair');
  silenceFields.append(dialogField('Nivell (dB, de −70 a −30)', silenceDb), dialogField('Segons (de 5 a 120)', silenceSeconds));
  const silenceBox = el('div', '');
  silenceBox.append(el('h3', 'h6 mb-1', 'Silenci a la sortida'), silenceFields,
    el('div', 'form-text', `Si el so que surt es queda per sota d'aquest nivell aquests segons seguits, el motor passa a la pista següent i ho anota a les incidències (un stream només s'anota). Per defecte, ${silence.standardThresholdDb} dB i ${silence.standardSeconds} s.`));

  // La carpeta de pistes d'emergència es desa de seguida en triar-la, com les carpetes vigilades
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
    const folder = await bridge.chooseFolder(emergency.folder);
    if (folder) await setEmergency(folder);
  });
  const emergencyDefault = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  emergencyDefault.type = 'button';
  emergencyDefault.append(icon('bi-arrow-counterclockwise'), ' La per defecte');
  emergencyDefault.title = emergency.defaultFolder;
  emergencyDefault.addEventListener('click', () => setEmergency(null));
  const emergencyTools = el('div', 'd-flex gap-2 mt-1');
  emergencyTools.append(emergencyPick, emergencyDefault);
  const emergencyBox = el('div', '');
  emergencyBox.append(el('h3', 'h6 mb-1', 'Pistes d\'emergència'), emergencyPath, emergencyState, emergencyTools,
    el('div', 'form-text', 'Cançons d\'un disc d\'aquest ordinador (no de xarxa). Si una pista tarda a obrir-se, en sona una d\'aquestes fins que la que tocava és a punt. Posa-n\'hi unes quantes: el canvi de carpeta es desa de seguida.'));
  const tools = el('div', 'cfg-cols');
  tools.append(silenceBox, emergencyBox);

  // --- Usuaris ---

  const usersTab = usersPanel(send, session, users, security);

  // --- Les pestanyes (com les de «Cerca» i «Notes»: botons i panells que s'amaguen) ---

  /** @type {Record<SettingsTab, HTMLElement>} */
  const panes = { general, output: outputPanel(send), processor: bridge.processor.createPanel(), mail: mailTab.element, backups, tools,
    users: usersTab.element };
  const tabs = buildTabs(TABS, panes, firstTab);

  for (;;) {
    const closed = formDialog({ title: 'Configuració', icon: 'bi-gear', body: [tabs.bar, tabs.box], confirm: 'Desa', wide: true });
    tabs.bar.closest('dialog')?.classList.add('cfg-dialog');
    const ok = await closed;
    if (!ok || restored) return;
    /** @type {SettingsTab} */
    let failed = 'general';
    try {
      await send('station.set', { name: name.value, frequency: frequency.value, logo });
      await send('season.set', { startDate: seasonFrom.value || null, endDate: seasonTo.value || null });
      failed = 'mail';
      await send('mail.set', mailTab.params());
      failed = 'tools';
      await send('silence.set', { thresholdDb: Number(silenceDb.value), seconds: Number(silenceSeconds.value) });
      failed = 'users';
      await send('security.set', { inactivityMinutes: usersTab.inactivityMinutes() });
      await session.refresh();
      return;
    } catch (err) {
      // Es torna a obrir per la pestanya que no s'ha pogut desar, amb el que s'hi havia escrit
      tabs.show(failed);
      await confirmAction({ title: 'No s\'han pogut desar les dades', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}

/**
 * Les pestanyes (com les de «Cerca» i «Notes»: botons i panells que s'amaguen).
 * @param {readonly (readonly [SettingsTab, string, string])[]} list  id, text i icona, en ordre
 * @param {Partial<Record<SettingsTab, HTMLElement>>} panes
 * @param {SettingsTab} first
 */
function buildTabs(list, panes, first) {
  const bar = el('ul', 'nav nav-tabs cfg-tabs');
  bar.setAttribute('role', 'tablist');
  /** @type {Map<SettingsTab, HTMLButtonElement>} */
  const buttons = new Map();
  const box = el('div', 'cfg-panes');
  for (const [id, text, iconName] of list) {
    const pane = panes[id];
    if (!pane) continue;
    const button = /** @type {HTMLButtonElement} */ (el('button', 'nav-link'));
    button.type = 'button';
    button.setAttribute('role', 'tab');
    button.append(icon(iconName), ` ${text}`);
    button.addEventListener('click', () => show(id));
    const item = el('li', 'nav-item');
    item.setAttribute('role', 'presentation');
    item.append(button);
    bar.append(item);
    buttons.set(id, button);
    pane.classList.add('cfg-pane');
    pane.setAttribute('role', 'tabpanel');
    box.append(pane);
  }
  /** @param {SettingsTab} id */
  const show = (id) => {
    if (!buttons.has(id)) id = /** @type {SettingsTab} */ (buttons.keys().next().value);
    for (const [tab, button] of buttons) {
      button.classList.toggle('active', tab === id);
      button.setAttribute('aria-selected', String(tab === id));
      panes[tab]?.classList.toggle('d-none', tab !== id);
    }
  };
  show(first);
  return { bar, box, show };
}
