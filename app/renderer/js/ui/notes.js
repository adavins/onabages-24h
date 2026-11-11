// @ts-check
// Pestanya «Notes» de la pantalla d'Emissió (fase 3, pas 4b; D-081): un sol bloc de text lliure, desat al motor. Es desa
// sol un moment després de deixar d'escriure (i en sortir del quadre); les altres interfícies connectades el reben amb
// notes.changed. Si mentre s'escriu arriba un canvi d'una altra interfície, no es trepitja el que s'està escrivint.

import { $, icon, el } from './dom.js';

const SAVE_DELAY_MS = 800;

export class NotesView {
  #send;
  #timer = 0;
  /** L'últim text que sabem que té el motor. */
  #saved = '';
  /** Hi ha canvis escrits aquí que encara no s'han desat. */
  #dirty = false;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    const box = /** @type {HTMLTextAreaElement} */ ($('notes-text'));
    box.addEventListener('input', () => {
      this.#dirty = true;
      this.#state('text-body-secondary', 'bi-pencil', 'S\'està escrivint…');
      clearTimeout(this.#timer);
      this.#timer = window.setTimeout(() => this.save(), SAVE_DELAY_MS);
    });
    box.addEventListener('blur', () => {
      if (this.#dirty) {
        clearTimeout(this.#timer);
        this.save();
      }
    });
  }

  /** En connectar-se: el text del motor (si no s'està escrivint res de nou). */
  async refresh() {
    try {
      const result = await this.#send('notes.get');
      this.received(result.text);
    } catch { /* sense connexió: ja ho diu la capçalera */ }
  }

  /** Text nou del motor (notes.get o notes.changed). @param {string} text */
  received(text) {
    this.#saved = text;
    if (this.#dirty) return; // no es trepitja el que s'està escrivint: quan es desi, guanyarà això
    const box = /** @type {HTMLTextAreaElement} */ ($('notes-text'));
    if (box.value !== text) box.value = text;
    this.#state('text-body-secondary', 'bi-check2', 'Desat');
  }

  async save() {
    const box = /** @type {HTMLTextAreaElement} */ ($('notes-text'));
    const text = box.value;
    if (text === this.#saved) {
      this.#dirty = false;
      this.#state('text-body-secondary', 'bi-check2', 'Desat');
      return;
    }
    this.#state('text-body-secondary', 'bi-cloud-arrow-up', 'Desant…');
    try {
      await this.#send('notes.set', { text });
      this.#saved = text;
      // Si mentrestant s'ha continuat escrivint, ja hi ha un altre desament programat
      if (box.value === text) {
        this.#dirty = false;
        this.#state('text-body-secondary', 'bi-check2', 'Desat');
      }
    } catch (err) {
      this.#state('text-warning', 'bi-exclamation-triangle-fill', `No s'ha pogut desar: ${/** @type {Error} */ (err).message}`);
    }
  }

  /** @param {string} cls @param {string} iconName @param {string} text */
  #state(cls, iconName, text) {
    const node = $('notes-state');
    node.className = `notes-state ${cls}`;
    node.replaceChildren(icon(iconName), ' ', el('span', '', text));
  }
}
