// @ts-check
// Usuaris i sessió a la interfície (fase 5, pas 8b; D-164). Qui pot fer què ho decideix el motor; aquí:
// - es porta l'estat de la sessió (es recupera si es perd la connexió: l'id es guarda a sessionStorage);
// - s'envia session.touch quan es fa servir el teclat o el ratolí (com a molt un cop per minut);
// - si una ordre respon «Cal iniciar sessió» just després d'un clic o una tecla, s'obre la finestra d'inici de sessió i,
//   si s'entra, l'ordre es torna a enviar (l'operador no ha de tornar a fer clic);
// - els elements marcats amb data-needs="operator|programmer|administrator" es veuen atenuats si no es poden fer servir,
//   i en fer-hi clic s'obre l'inici de sessió (o es diu que el rol no ho permet).

import { el, statusLabel } from './ui/dom.js';
import { formDialog, dialogField } from './ui/form-dialog.js';
import { confirmAction } from './ui/confirm.js';

/**
 * @typedef {{ id: string, user: string, displayName: string, role: 'operator' | 'programmer' | 'administrator',
 *   roleName: string, mustChangePassword: boolean }} Session
 * @typedef {{ usersExist: boolean, inactivityMinutes: number, session: Session | null }} SessionState
 */

/** Els rols, del que pot fer menys al que pot fer més. */
const ROLES = ['operator', 'programmer', 'administrator'];
const ROLE_NAMES = { operator: 'operador', programmer: 'programador', administrator: 'administrador' };
const STORAGE_KEY = 'ob24.sessionId';
const TOUCH_EVERY_MS = 60_000;

/** Si aquest rol arriba al que cal. @param {string | undefined} role @param {string} needed */
export function roleAllows(role, needed) {
  return role !== undefined && ROLES.indexOf(role) >= ROLES.indexOf(needed);
}

export class SessionController {
  /** @type {SessionState} */
  state = { usersExist: false, inactivityMinutes: 30, session: null };
  /** @type {(cmd: string, params?: object, timeoutMs?: number) => Promise<any>} */
  #request;
  /** @type {Set<(state: SessionState) => void>} */
  #listeners = new Set();
  /** @type {Promise<boolean> | null} */
  #loginOpen = null;
  #lastTouch = 0;

  /** @param {(cmd: string, params?: object, timeoutMs?: number) => Promise<any>} request  l'ordre al motor, sense res més */
  constructor(request) {
    this.#request = request;
    // L'activitat: el teclat, el ratolí o la roda (només amb sessió, i com a molt un cop per minut)
    for (const type of ['keydown', 'pointerdown', 'wheel']) {
      window.addEventListener(type, () => this.#touch(), { passive: true, capture: true });
    }
    // Els elements que demanen un rol: el clic s'atura abans d'arribar-hi si no es poden fer servir
    document.addEventListener('click', (e) => this.#guardClick(e), { capture: true });
  }

  /** @param {(state: SessionState) => void} listener */
  onChange(listener) {
    this.#listeners.add(listener);
  }

  /** Si la sessió d'ara permet fer el que demana aquest rol (sense usuaris, tot). @param {string} needed */
  can(needed) {
    return !this.state.usersExist || roleAllows(this.state.session?.role, needed);
  }

  /** @param {SessionState} state */
  #set(state) {
    this.state = state;
    try {
      if (state.session) sessionStorage.setItem(STORAGE_KEY, state.session.id);
      else sessionStorage.removeItem(STORAGE_KEY);
    } catch { /* sense sessionStorage: només es perd la sessió si es recarrega la finestra */ }
    document.body.dataset.access = !state.usersExist ? 'all' : state.session?.role ?? 'none';
    for (const listener of this.#listeners) listener(state);
  }

  /** En connectar-se (o reconnectar-se) amb el motor: recupera la sessió si n'hi havia una. */
  async connected() {
    let saved = null;
    try { saved = sessionStorage.getItem(STORAGE_KEY); } catch { /* res */ }
    if (saved) {
      try {
        this.#set(await this.#request('session.resume', { sessionId: saved }));
        return;
      } catch { /* ja s'ha tancat (o el motor s'ha reiniciat): mode de consulta */ }
    }
    try {
      this.#set(await this.#request('session.get'));
    } catch { /* un motor sense sessions: es queda com està */ }
  }

  /** El motor ha tancat la sessió (session.changed). @param {SessionState & { reason?: string }} state */
  changed(state) {
    const hadSession = this.state.session !== null;
    this.#set(state);
    if (hadSession && !state.session && state.reason === 'inactivity') {
      confirmAction({ title: 'S\'ha tancat la sessió', message: `Fa ${state.inactivityMinutes} minuts que no s'ha fet res: la sessió s'ha tancat. L'emissió continua. Per tornar a operar, inicia la sessió.`, confirm: 'D\'acord', info: true });
    }
  }

  /** Els usuaris han canviat (p. ex. s'ha creat el primer): es torna a llegir l'estat. */
  async refresh() {
    try { this.#set(await this.#request('session.get')); } catch { /* res */ }
  }

  #touch() {
    if (!this.state.session || Date.now() - this.#lastTouch < TOUCH_EVERY_MS) return;
    this.#lastTouch = Date.now();
    this.#request('session.touch').then((s) => this.#set(s)).catch(() => {});
  }

  /** @param {MouseEvent} e */
  #guardClick(e) {
    const target = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target)?.closest?.('[data-needs]'));
    if (!target) return;
    const needed = target.dataset.needs ?? 'administrator';
    if (this.can(needed)) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (!this.state.session) this.login();
    else this.#forbidden(needed);
  }

  /** @param {string} needed */
  #forbidden(needed) {
    confirmAction({
      title: 'No ho pots fer',
      message: `El teu rol (${this.state.session?.roleName}) no ho permet: cal ser ${ROLE_NAMES[/** @type {keyof ROLE_NAMES} */ (needed)] ?? needed}.`,
      confirm: 'D\'acord', info: true,
    });
  }

  /**
   * Envia una ordre. Si respon que cal iniciar sessió (o canviar la contrasenya provisional) just després que l'operador
   * hagi fet alguna cosa, ho demana i la torna a enviar. Si no, l'error arriba a qui l'ha enviada, com sempre.
   * @param {string} cmd @param {object} [params] @param {number} [timeoutMs]
   */
  async send(cmd, params = {}, timeoutMs = undefined) {
    const interactive = navigator.userActivation?.isActive ?? false;
    try {
      return await this.#request(cmd, params, timeoutMs);
    } catch (err) {
      const code = /** @type {{ code?: string }} */ (err).code;
      if (code === 'not_logged_in' || code === 'must_change_password') {
        if (code === 'not_logged_in' && this.state.session) await this.refresh(); // s'havia tancat
        if (interactive && await this.login()) return this.#request(cmd, params, timeoutMs);
      }
      throw err;
    }
  }

  /** Obre la finestra d'inici de sessió (una sola vegada encara que la demanin diverses ordres). Retorna si s'ha entrat. */
  login() {
    if (!this.#loginOpen) this.#loginOpen = this.#loginDialog().finally(() => { this.#loginOpen = null; });
    return this.#loginOpen;
  }

  async #loginDialog() {
    if (this.state.session?.mustChangePassword) return this.changePassword(true);
    const user = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    user.autocomplete = 'username';
    const password = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    password.type = 'password';
    password.autocomplete = 'current-password';
    const problem = el('div', 'small');
    for (;;) {
      const ok = await formDialog({
        title: 'Inicia la sessió',
        icon: 'bi-person-lock',
        message: 'Per operar cal iniciar sessió. L\'emissió continua igualment.',
        body: [dialogField('Usuari', user), dialogField('Contrasenya', password), problem],
        confirm: 'Entra',
      });
      if (!ok) return false;
      try {
        const state = await this.#request('session.login', { user: user.value.trim(), password: password.value });
        this.#set(state);
        this.#lastTouch = Date.now();
        if (state.session?.mustChangePassword) return this.changePassword(true);
        return true;
      } catch (err) {
        password.value = '';
        problem.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
      }
    }
  }

  /** Entra amb un usuari i contrasenya que ja se saben (p. ex. el primer administrador, just després de crear-lo). @param {string} user @param {string} password */
  async loginAs(user, password) {
    try {
      this.#set(await this.#request('session.login', { user, password }));
      this.#lastTouch = Date.now();
    } catch { await this.refresh(); }
  }

  /** La finestra de la sessió oberta: qui és, «Canvia la contrasenya…» i «Tanca la sessió». */
  async menu() {
    const session = this.state.session;
    if (!session) return;
    /** @type {'password' | 'logout' | null} */
    let choice = null;
    const actions = el('div', 'd-flex gap-2 flex-wrap');
    /** @param {string} text @param {string} iconName @param {'password' | 'logout'} value */
    const action = (text, iconName, value) => {
      const button = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-outline-secondary'));
      button.type = 'button';
      const i = el('i', `bi ${iconName}`);
      i.setAttribute('aria-hidden', 'true');
      button.append(i, ` ${text}`);
      button.addEventListener('click', () => {
        choice = value;
        /** @type {HTMLDialogElement | null} */ (button.closest('dialog'))?.close();
      });
      return button;
    };
    actions.append(action('Canvia la contrasenya…', 'bi-key', 'password'), action('Tanca la sessió', 'bi-box-arrow-right', 'logout'));
    await formDialog({
      title: 'Sessió',
      icon: 'bi-person-check',
      message: `Has entrat com a ${session.displayName} (${session.roleName}). La sessió es tanca sola després de ${this.state.inactivityMinutes} minuts sense fer res.`,
      body: [actions],
    });
    if (choice === 'password') await this.changePassword();
    else if (choice === 'logout') await this.logout();
  }

  /** Tanca la sessió (torna al mode de consulta). */
  async logout() {
    try { this.#set(await this.#request('session.logout')); } catch { /* res */ }
  }

  /**
   * Canviar la contrasenya de qui té la sessió. <c>required</c>: és provisional (no es pot fer res fins que es canviï).
   * @param {boolean} [required]
   * @returns {Promise<boolean>}
   */
  async changePassword(required = false) {
    const current = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    current.type = 'password';
    current.autocomplete = 'current-password';
    const fresh = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    fresh.type = 'password';
    fresh.autocomplete = 'new-password';
    const repeat = /** @type {HTMLInputElement} */ (el('input', 'form-control'));
    repeat.type = 'password';
    repeat.autocomplete = 'new-password';
    const problem = el('div', 'small');
    for (;;) {
      const ok = await formDialog({
        title: required ? 'Canvia la contrasenya provisional' : 'Canvia la contrasenya',
        icon: 'bi-key',
        message: required ? 'Aquesta contrasenya és provisional: abans de fer res, posa\'n una de nova.' : undefined,
        body: [dialogField(required ? 'Contrasenya provisional' : 'Contrasenya d\'ara', current),
          dialogField('Contrasenya nova', fresh, 'Com a mínim 8 caràcters.'), dialogField('Repeteix-la', repeat), problem],
        confirm: 'Canvia-la',
      });
      if (!ok) return false;
      if (fresh.value !== repeat.value) {
        problem.replaceChildren(statusLabel('text-danger', 'bi-x-circle', 'Les dues contrasenyes noves no són iguals.'));
        continue;
      }
      try {
        this.#set(await this.#request('session.changePassword', { current: current.value, new: fresh.value }));
        return true;
      } catch (err) {
        problem.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
      }
    }
  }
}
