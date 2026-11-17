// @ts-check
// ⚙ Configuració → pestanya «Usuaris» (fase 5, pas 8b; D-164): la llista d'usuaris, afegir-ne i canviar-los (es desa de
// seguida, com les accions de les altres pestanyes) i els minuts d'inactivitat (amb el «Desa» de la finestra).
// Els usuaris no s'esborren: es desactiven. El primer ha de ser administrador; en crear-lo, s'hi entra de seguida.

import { el, icon, statusLabel } from './dom.js';
import { formDialog, dialogField } from './form-dialog.js';

/**
 * @typedef {{ id: number, name: string, fullName: string, role: string, roleName: string, active: boolean,
 *   provisional: boolean, createdAt: string, lastLoginAt: string | null }} UserRow
 */

const ROLE_OPTIONS = [
  ['operator', 'Operador', 'La pantalla d\'emissió: la cua, el reproductor, els auxiliars, les notes, l\'AutoDJ i la sortida d\'àudio.'],
  ['programmer', 'Programador', 'A més: esdeveniments, campanyes, pistes, rotacions, bosses i calendaris.'],
  ['administrator', 'Administrador', 'Tot: la configuració, els usuaris i les còpies de seguretat.'],
];

/** @param {string | null} iso */
function dateText(iso) {
  if (!iso) return 'Mai';
  const at = new Date(iso);
  return `${at.toLocaleDateString('ca')} ${at.toLocaleTimeString('ca', { hour: '2-digit', minute: '2-digit' })}`;
}

/**
 * La pestanya. <c>inactivityMinutes()</c>: el que s'ha d'enviar a security.set en desar.
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {import('../session.js').SessionController} session
 * @param {UserRow[]} users
 * @param {{ inactivityMinutes: number, standardInactivityMinutes: number }} security
 * @returns {{ element: HTMLElement, inactivityMinutes: () => number }}
 */
export function usersPanel(send, session, users, security) {
  const state = el('div', 'small mb-2');
  const list = el('div', 'us-list');

  const show = () => {
    state.replaceChildren(users.length === 0
      ? statusLabel('text-warning', 'bi-unlock', 'No hi ha cap usuari: tothom pot canviar-ho tot. Crea un administrador per començar.')
      : statusLabel('text-success', 'bi-lock', 'Per operar cal iniciar sessió (sense sessió, només es pot consultar).'));
    if (users.length === 0) {
      list.replaceChildren(el('p', 'form-text m-0', 'Encara no hi ha cap usuari.'));
      return;
    }
    const table = el('table', 'table table-sm table-hover align-middle mb-0 us-table');
    const head = el('thead', '');
    const headRow = el('tr', '');
    for (const text of ['Usuari', 'Nom', 'Rol', 'Estat', 'Últim accés', '']) headRow.append(el('th', '', text));
    head.append(headRow);
    const body = el('tbody', '');
    for (const user of users) {
      const row = el('tr', user.active ? '' : 'text-body-secondary');
      const status = user.active
        ? (user.provisional ? statusLabel('text-warning', 'bi-key', 'Contrasenya provisional') : statusLabel('text-success', 'bi-check-circle', 'Actiu'))
        : statusLabel('text-secondary', 'bi-slash-circle', 'Desactivat');
      const edit = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      edit.type = 'button';
      edit.append(icon('bi-pencil'), ' Edita…');
      edit.addEventListener('click', () => editUser(user));
      const actions = el('td', 'text-end');
      actions.append(edit);
      const statusCell = el('td', '');
      statusCell.append(status);
      row.append(el('td', '', user.name), el('td', '', user.fullName), el('td', '', user.roleName), statusCell,
        el('td', '', dateText(user.lastLoginAt)), actions);
      body.append(row);
    }
    table.append(head, body);
    list.replaceChildren(table);
  };

  const reload = async () => {
    try {
      users = (await send('users.list')).users;
    } catch { /* p. ex. s'ha tancat la sessió: es queda com estava */ }
    show();
  };

  /** Afegir (sense usuari) o canviar un usuari, en una finestra a sobre de la configuració. @param {UserRow} [user] */
  const editUser = async (user) => {
    const first = users.length === 0;
    const name = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    name.value = user?.name ?? '';
    name.autocomplete = 'off';
    const fullName = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    fullName.value = user?.fullName ?? '';
    const role = /** @type {HTMLSelectElement} */ (el('select', 'form-select form-select-sm'));
    for (const [value, text] of ROLE_OPTIONS) role.append(new Option(text, value));
    role.value = user?.role ?? (first ? 'administrator' : 'operator');
    role.disabled = first;
    const roleHelp = el('div', 'form-text');
    const showRoleHelp = () => { roleHelp.textContent = ROLE_OPTIONS.find(([value]) => value === role.value)?.[2] ?? ''; };
    role.addEventListener('change', showRoleHelp);
    showRoleHelp();
    const active = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    active.type = 'checkbox';
    active.id = 'us-active';
    active.checked = user?.active ?? true;
    const activeLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Actiu (si no, no pot iniciar sessió)'));
    activeLabel.htmlFor = active.id;
    const activeBox = el('div', 'form-check mb-2');
    activeBox.append(active, activeLabel);
    const password = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    password.type = 'password';
    password.autocomplete = 'new-password';
    const repeat = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    repeat.type = 'password';
    repeat.autocomplete = 'new-password';
    const passwords = el('div', 'cfg-pair');
    passwords.append(dialogField(user ? 'Contrasenya nova' : 'Contrasenya', password), dialogField('Repeteix-la', repeat));
    const problem = el('div', 'small');

    for (;;) {
      const ok = await formDialog({
        title: user ? `Usuari «${user.name}»` : (first ? 'El primer usuari: l\'administrador' : 'Usuari nou'),
        icon: 'bi-person-gear',
        message: first ? 'Quan el creïs, per operar caldrà iniciar sessió (s\'hi entra de seguida amb aquest usuari).' : undefined,
        body: [dialogField('Nom per iniciar sessió', name), dialogField('Nom complet', fullName), dialogField('Rol', role), roleHelp,
          ...(user ? [activeBox] : []), passwords,
          el('div', 'form-text mb-2', user ? 'Deixa-la buida per no canviar-la. Com a mínim 8 caràcters.' : 'Com a mínim 8 caràcters. Cada usuari la pot canviar després.'),
          problem],
        confirm: user ? 'Desa' : 'Crea l\'usuari',
      });
      if (!ok) return;
      if (password.value !== repeat.value) {
        problem.replaceChildren(statusLabel('text-danger', 'bi-x-circle', 'Les dues contrasenyes no són iguals.'));
        continue;
      }
      try {
        await send('users.save', {
          ...(user ? { userId: user.id } : {}),
          name: name.value.trim(), fullName: fullName.value.trim(), role: role.value, active: active.checked,
          ...(password.value ? { password: password.value } : {}),
        });
        if (first) await session.loginAs(name.value.trim(), password.value);
        else await session.refresh(); // p. ex. s'ha canviat el rol de qui té la sessió
        await reload();
        return;
      } catch (err) {
        problem.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
      }
    }
  };

  const add = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  add.type = 'button';
  add.append(icon('bi-person-plus'), ' Afegeix un usuari…');
  add.addEventListener('click', () => editUser());

  const minutes = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
  minutes.type = 'number';
  minutes.min = '5';
  minutes.max = '240';
  minutes.step = '1';
  minutes.value = String(security.inactivityMinutes);

  const left = el('div', 'us-left');
  left.append(state, list, el('div', 'mt-2'), add);
  const right = el('div', '');
  right.append(el('h3', 'h6 mb-1', 'Sessió'), dialogField('Minuts sense activitat per tancar-la (de 5 a 240)', minutes),
    el('div', 'form-text mb-2', `Per defecte, ${security.standardInactivityMinutes}. L'emissió no depèn mai de la sessió: sense sessió es veu tot, però no es pot canviar res.`),
    el('h3', 'h6 mt-3 mb-1', 'Rols'),
    ...ROLE_OPTIONS.map(([, text, help]) => {
      const p = el('div', 'form-text mt-0 mb-1');
      p.append(el('strong', '', text), `: ${help}`);
      return p;
    }),
    el('div', 'form-text', 'Si s\'oblida la contrasenya de l\'administrador: amb el motor aturat, «OnaBages24.Engine restableix-administrador» en dona una de provisional.'));
  const element = el('div', 'cfg-cols us-cols');
  element.append(left, right);
  show();
  return { element, inactivityMinutes: () => Number(minutes.value) };
}
