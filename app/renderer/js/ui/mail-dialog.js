// @ts-check
// Avisos per correu (fase 5, pas 5; D-160): el servidor, l'usuari i la contrasenya, els destinataris i quins tipus
// d'incidència s'envien, i el correu de prova. S'obre des de ⚙ Configuració. Ho desa el motor (mail.set): la
// contrasenya hi queda xifrada i no torna mai a la interfície (només se sap si n'hi ha una de desada).

import { el, icon, statusLabel } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';
import { confirmAction } from './confirm.js';
import { KINDS } from '../incidents-form.js';

/**
 * @typedef {{ enabled: boolean, host: string, port: number, security: 'starttls' | 'none', user: string, from: string,
 *   to: string[], excludedKinds: string[], standardExcludedKinds: string[], hasPassword: boolean, problem: string | null,
 *   lastSentAt: string | null }} MailConfig
 */

/** Els tipus que es poden triar (els problemes del correu mateix no s'envien mai: no arribarien). */
const MAIL_KINDS = KINDS.filter(([kind]) => kind !== 'CORREU');

/**
 * @param {(cmd: string, params?: object, timeoutMs?: number) => Promise<any>} send
 * @returns {Promise<void>}
 */
export async function openMailDialog(send) {
  /** @type {MailConfig} */
  let mail;
  try {
    mail = await send('mail.get');
  } catch (err) {
    await confirmAction({ title: 'Avisos per correu', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    return;
  }

  /** @param {string} type @param {string} value */
  const input = (type, value) => {
    const i = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    i.type = type;
    i.value = value;
    return i;
  };

  const enabled = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  enabled.type = 'checkbox';
  enabled.checked = mail.enabled;
  enabled.id = 'ml-enabled';
  const enabledLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Envia avisos per correu'));
  enabledLabel.htmlFor = enabled.id;
  const enabledBox = el('div', 'form-check mb-2');
  enabledBox.append(enabled, enabledLabel);

  const host = input('text', mail.host);
  host.placeholder = 'p. ex. smtp.gmail.com';
  const port = input('number', String(mail.port));
  port.min = '1';
  port.max = '65535';
  const security = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
  security.append(new Option('STARTTLS (el normal, port 587)', 'starttls'), new Option('Cap (només un servidor de la xarxa local)', 'none'));
  security.value = mail.security;
  const serverFields = el('div', 'ml-server');
  serverFields.append(dialogField('Servidor SMTP', host), dialogField('Port', port), dialogField('Xifratge', security));

  const user = input('text', mail.user);
  user.autocomplete = 'off';
  const password = input('password', '');
  password.autocomplete = 'new-password';
  password.placeholder = mail.hasPassword ? 'Desada (escriu-ne una de nova per canviar-la)' : '';
  const accountFields = el('div', 'st-season');
  accountFields.append(dialogField('Usuari', user), dialogField('Contrasenya', password));

  const from = input('email', mail.from);
  from.placeholder = 'Si és buit, l\'usuari';
  const to = /** @type {HTMLTextAreaElement} */ (el('textarea', 'form-control form-control-sm'));
  to.rows = 2;
  to.value = mail.to.join(', ');

  // Quins tipus s'envien (marcat = s'envia); el motor desa els que no, perquè un tipus nou s'enviï sense haver-lo de marcar
  /** @type {Map<string, HTMLInputElement>} */
  const kindBoxes = new Map();
  const kinds = el('div', 'ml-kinds');
  for (const [kind, name] of MAIL_KINDS) {
    const box = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    box.type = 'checkbox';
    box.id = `ml-kind-${kind}`;
    box.checked = !mail.excludedKinds.includes(kind);
    const label = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', name));
    label.htmlFor = box.id;
    const item = el('div', 'form-check');
    item.append(box, label);
    kinds.append(item);
    kindBoxes.set(kind, box);
  }
  const standardNames = mail.standardExcludedKinds.map((k) => KINDS.find(([code]) => code === k)?.[1] ?? k).join(' i ');

  /** El que hi ha escrit al formulari (la contrasenya, només si se n'ha escrit una). */
  const values = () => ({
    host: host.value.trim(),
    port: Number(port.value),
    security: security.value,
    user: user.value.trim(),
    from: from.value.trim(),
    to: to.value,
    excludedKinds: MAIL_KINDS.filter(([kind]) => !kindBoxes.get(kind)?.checked).map(([kind]) => kind),
    ...(password.value ? { password: password.value } : {}),
  });

  // Correu de prova: amb el que hi ha escrit, encara que no s'hagi desat (el motor espera el servidor fins a 30 s)
  const testState = el('div', 'small mt-1');
  const test = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  test.type = 'button';
  test.append(icon('bi-envelope-check'), ' Envia un correu de prova');
  test.addEventListener('click', async () => {
    test.disabled = true;
    testState.replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Enviant… (pot tardar fins a 30 s)'));
    try {
      const result = await send('mail.test', values(), 40000);
      testState.replaceChildren(result.sent
        ? statusLabel('text-success', 'bi-check-circle', `Enviat. Mira la safata d'entrada de ${to.value.trim()} (i el correu brossa).`)
        : statusLabel('text-danger', 'bi-x-circle', `No s'ha pogut enviar: ${result.error}`));
    } catch (err) {
      testState.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
    } finally {
      test.disabled = false;
    }
  });

  const state = el('div', 'small mb-2');
  if (mail.problem) state.append(statusLabel('text-warning', 'bi-exclamation-triangle-fill', mail.problem));
  else if (mail.lastSentAt) {
    const at = new Date(mail.lastSentAt);
    state.append(statusLabel('text-success', 'bi-check-circle',
      `Últim avís enviat: ${at.toLocaleDateString('ca')} a les ${at.toLocaleTimeString('ca', { hour: '2-digit', minute: '2-digit' })}`));
  }

  for (;;) {
    const ok = await formDialog({
      title: 'Avisos per correu',
      icon: 'bi-envelope-exclamation',
      wide: true,
      body: [
        state,
        enabledBox,
        serverFields,
        accountFields,
        el('div', 'form-text mb-2', 'Amb Gmail, cal una contrasenya d\'aplicació (Compte de Google → Seguretat → Contrasenyes d\'aplicacions). Millor un compte només per als avisos. La contrasenya es desa xifrada i només la pot llegir aquest ordinador.'),
        dialogField('Remitent', from),
        dialogField('Destinataris', to, 'Separats per comes.'),
        el('h3', 'h6 mt-3 mb-1', 'Què s\'envia'),
        kinds,
        el('div', 'form-text', `Per defecte no s'envien: ${standardNames}. Els avisos arriben agrupats: el primer correu, 2 minuts després de la primera incidència; després, com a molt un cada 10 minuts (si n'hi ha una de greu, no s'esperen els 10). Quan una incidència es resol, també s'avisa.`),
        el('div', 'mt-3'),
        test,
        testState,
      ],
      confirm: 'Desa',
    });
    if (!ok) return;
    try {
      await send('mail.set', { enabled: enabled.checked, ...values() });
      return;
    } catch (err) {
      // P. ex. una adreça mal escrita: es torna a obrir amb el que s'havia escrit
      await confirmAction({ title: 'No s\'han pogut desar els avisos', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}
