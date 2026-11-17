// @ts-check
// Desplegable de selecció múltiple (D-169): un botó que diu què hi ha triat i, en obrir-lo, una llista de caselles.
// Com el menú «Més» (D-168), sense el JavaScript de Bootstrap. Es tanca en fer clic a fora o amb Esc; marcar una
// casella no el tanca (se'n poden marcar diverses). Avisa dels canvis amb un esdeveniment «change» a l'element.

import { el, icon } from './dom.js';

/** @typedef {{ value: string, text: string, icon?: string }} MultiOption */

export class MultiSelect {
  /** L'element que es posa a la pàgina. */
  element = el('div', 'ms-wrap');
  #button = /** @type {HTMLButtonElement} */ (el('button', 'form-select form-select-sm ms-button'));
  #label = el('span', 'ms-label');
  #menu = el('div', 'ms-menu d-none');
  /** @type {Map<string, { box: HTMLInputElement, item: HTMLElement }>} */
  #items = new Map();
  #name;

  /**
   * @param {string} name  què es tria (per als lectors de pantalla i el text del botó), p. ex. «Origen»
   * @param {MultiOption[]} options
   * @param {string[]} selected  les marcades al principi
   */
  constructor(name, options, selected) {
    this.#name = name;
    const id = `ms-${Math.random().toString(36).slice(2, 8)}`;
    this.#button.type = 'button';
    this.#button.setAttribute('aria-haspopup', 'true');
    this.#button.setAttribute('aria-expanded', 'false');
    this.#button.setAttribute('aria-controls', id);
    this.#button.append(this.#label);
    this.#menu.id = id;
    this.#menu.setAttribute('role', 'group');
    this.#menu.setAttribute('aria-label', name);

    for (const option of options) {
      const box = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
      box.type = 'checkbox';
      box.id = `${id}-${option.value}`;
      box.checked = selected.includes(option.value);
      box.addEventListener('change', () => {
        this.#update();
        this.element.dispatchEvent(new Event('change'));
      });
      const label = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label'));
      label.htmlFor = box.id;
      if (option.icon) label.append(icon(option.icon), ' ');
      label.append(option.text);
      const item = el('div', 'form-check ms-item');
      item.append(box, label);
      this.#menu.append(item);
      this.#items.set(option.value, { box, item });
    }
    this.element.append(this.#button, this.#menu);

    this.#button.addEventListener('click', () => this.#setOpen(this.#menu.classList.contains('d-none')));
    document.addEventListener('pointerdown', (e) => {
      if (this.#isOpen() && !this.element.contains(/** @type {Node} */ (e.target))) this.#setOpen(false);
    });
    this.element.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && this.#isOpen()) {
        e.stopPropagation();
        this.#setOpen(false);
        this.#button.focus();
      }
    });
    this.#update();
  }

  /** Les marcades que es veuen (les amagades amb setHidden no compten). @returns {string[]} */
  get values() {
    return [...this.#items].filter(([, i]) => i.box.checked && !i.item.classList.contains('d-none')).map(([value]) => value);
  }

  /** @param {string[]} values */
  set values(values) {
    for (const [value, i] of this.#items) i.box.checked = values.includes(value);
    this.#update();
  }

  /** @param {boolean} disabled */
  set disabled(disabled) {
    this.#button.disabled = disabled;
    if (disabled) this.#setOpen(false);
  }

  /** Amaga (o torna a mostrar) una opció; amagada, no compta com a triada. @param {string} value @param {boolean} hidden */
  setHidden(value, hidden) {
    this.#items.get(value)?.item.classList.toggle('d-none', hidden);
    this.#update();
  }

  #isOpen() {
    return !this.#menu.classList.contains('d-none');
  }

  /** @param {boolean} open */
  #setOpen(open) {
    this.#menu.classList.toggle('d-none', !open);
    this.#button.setAttribute('aria-expanded', String(open));
    if (open) /** @type {HTMLElement | null} */ (this.#menu.querySelector('.ms-item:not(.d-none) input'))?.focus();
  }

  /** El text del botó: «Origen: tots», «Origen: cap» o les triades. */
  #update() {
    const visible = [...this.#items.values()].filter((i) => !i.item.classList.contains('d-none'));
    const chosen = visible.filter((i) => i.box.checked);
    const text = chosen.length === visible.length ? 'tots'
      : chosen.length === 0 ? 'cap'
        : chosen.map((i) => i.item.textContent?.trim() ?? '').join(', ');
    this.#label.textContent = `${this.#name}: ${text}`;
    this.#button.title = `${this.#name}: ${text}`;
  }
}
