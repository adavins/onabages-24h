// @ts-check
// Triar la sortida d'àudio des de la barra d'estat (D-120). El motor ja ho sap fer en calent (output.set, D-038):
// tanca la sortida d'ara i obre la nova. Talla l'emissió un instant, per això es demana confirmació.

import { el, icon } from './dom.js';
import { formDialog } from './form-dialog.js';
import { confirmAction } from './confirm.js';

const CONNECTORS = /** @type {Record<string, string>} */ ({
  analog: 'analògica',
  digital: 'digital (S/PDIF)',
  hdmi: 'HDMI',
  remote: 'remota',
  unknown: 'connector desconegut',
});

const STATUSES = /** @type {Record<string, string>} */ ({
  disabled: 'desactivada a Windows',
  unplugged: 'desconnectada',
});

/**
 * Obre la finestra per triar la sortida i, si se'n tria una altra i es confirma, hi passa.
 * @param {(cmd: string, params?: any) => Promise<any>} send
 */
export async function openOutputPicker(send) {
  /** @type {any} */
  let output;
  try {
    output = (await send('output.get')).output;
  } catch (err) {
    await confirmAction({ title: 'No es pot triar la sortida', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return;
  }

  /** @type {string | null} */
  let chosen = output.current?.id ?? null;
  const list = el('div', 'op-list');
  list.setAttribute('role', 'radiogroup');
  const showAll = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  showAll.type = 'checkbox';
  showAll.id = 'op-show-all';
  const showAllLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Mostra també les desactivades i les desconnectades'));
  showAllLabel.htmlFor = showAll.id;
  const showAllBox = el('div', 'form-check mt-2');
  showAllBox.append(showAll, showAllLabel);

  const fill = async () => {
    list.replaceChildren(el('div', 'text-body-secondary', 'Carregant…'));
    /** @type {any[]} */
    let devices;
    try {
      devices = (await send('devices.list', { kind: 'output', includeInactive: showAll.checked })).devices;
    } catch (err) {
      list.replaceChildren(el('div', 'text-danger', `No s'han pogut llegir les sortides: ${/** @type {Error} */ (err).message}`));
      return;
    }
    list.replaceChildren(...devices.map((device) => deviceRow(device, output, chosen, (id) => { chosen = id; })));
    if (devices.length === 0) list.append(el('div', 'text-body-secondary', 'Windows no té cap sortida d\'àudio.'));
    // La configurada que no hi és (per això s'emet per la d'emergència)
    if (output.configured?.id && !devices.some((d) => d.id === output.configured.id)) {
      const missing = el('div', 'op-missing text-warning');
      missing.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', `La sortida configurada, «${output.configured.name ?? output.configured.id}», no hi és.`));
      list.append(missing);
    }
  };
  showAll.addEventListener('change', fill);

  /** @type {HTMLElement[]} */
  const body = [];
  if (output.emergency && output.problem) {
    const warning = el('div', 'alert alert-danger py-2');
    warning.append(icon('bi-exclamation-triangle-fill'), ' ', el('span', '', output.problem));
    body.push(warning);
  }
  body.push(list, showAllBox);
  fill();

  const ok = await formDialog({
    title: 'Sortida d\'àudio',
    icon: 'bi-speaker-fill',
    message: 'Per on surt l\'emissió cap a la taula. Canviar-la talla el so un instant.',
    body,
    confirm: 'Canvia la sortida',
    wide: true,
  });
  if (!ok || !chosen || chosen === output.current?.id) return;

  const name = /** @type {HTMLElement | null} */ (list.querySelector(`[data-id="${CSS.escape(chosen)}"]`))?.dataset.name ?? chosen;
  const sure = await confirmAction({
    title: 'Canviar la sortida d\'àudio?',
    message: `L'emissió passarà a sortir per «${name}». El so es tallarà un instant mentre es canvia de targeta.`,
    confirm: 'Canvia la sortida',
  });
  if (!sure) return;
  try {
    await send('output.set', { deviceId: chosen });
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut canviar la sortida', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
  }
}

/**
 * Una sortida de la llista: botó d'opció, nom, connector, i si és la d'ara o la configurada.
 * @param {any} device  de devices.list (docs/protocol.md, 4.1)
 * @param {any} output  de output.get (4.3)
 * @param {string | null} chosen
 * @param {(id: string) => void} onChoose
 */
function deviceRow(device, output, chosen, onChoose) {
  const row = /** @type {HTMLLabelElement} */ (el('label', 'op-row'));
  row.dataset.id = device.id;
  row.dataset.name = device.name;
  const active = device.status === 'active';
  if (!active) row.classList.add('is-inactive');

  const radio = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  radio.type = 'radio';
  radio.name = 'op-device';
  radio.checked = device.id === chosen;
  radio.disabled = !active; // per una desactivada o desconnectada no sortiria res
  radio.addEventListener('change', () => { if (radio.checked) onChoose(device.id); });

  const text = el('div', 'op-text');
  text.append(el('div', 'op-name', device.name));
  const details = [CONNECTORS[device.connector] ?? CONNECTORS.unknown];
  if (device.format) details.push(`${device.format.sampleRate / 1000} kHz, ${device.format.channels} canals`);
  if (!active) details.push(STATUSES[device.status] ?? device.status);
  text.append(el('div', 'op-details', details.join(' · ')));

  const marks = el('div', 'op-marks');
  if (device.id === output.current?.id) {
    const now = el('span', output.emergency ? 'badge text-bg-danger' : 'badge text-bg-success');
    now.append(icon(output.emergency ? 'bi-exclamation-triangle-fill' : 'bi-volume-up-fill'), ' ', output.emergency ? 'Ara (emergència)' : 'Ara');
    marks.append(now);
  }
  if (device.id === output.configured?.id && device.id !== output.current?.id) {
    const configured = el('span', 'badge text-bg-secondary');
    configured.append(icon('bi-bookmark-fill'), ' ', 'Configurada');
    marks.append(configured);
  }

  row.append(radio, text, marks);
  return row;
}
