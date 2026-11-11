// @ts-check
// Requadres plegables de la pantalla d'Emissió (D-083). Un requadre amb l'atribut data-collapse="nom" es plega i es
// desplega amb el botó .collapse-toggle de la capçalera; plegat, només se'n veu la capçalera. L'estat es desa al motor
// (ui.set, «collapsed»), perquè en tornar a obrir l'aplicació surti igual.
// Amb l'atribut data-expand-on-drag, el requadre s'obre quan s'hi arrossega una pista (sense desar-ho).

/** Com surten si encara no s'han tocat mai: els auxiliars, plegats. @type {Record<string, boolean>} */
const DEFAULTS = { aux: true };

export class CollapsibleCards {
  /** @type {Map<string, HTMLElement>} */
  #cards = new Map();

  /** @param {(name: string, collapsed: boolean) => void} onChange  es crida quan l'operador en plega o desplega un (per desar-ho) */
  constructor(onChange) {
    for (const card of /** @type {NodeListOf<HTMLElement>} */ (document.querySelectorAll('[data-collapse]'))) {
      const name = /** @type {string} */ (card.dataset.collapse);
      this.#cards.set(name, card);
      const toggle = /** @type {HTMLButtonElement} */ (card.querySelector('.collapse-toggle'));
      toggle.addEventListener('click', () => {
        const collapsed = !card.classList.contains('is-collapsed');
        this.#set(name, collapsed);
        onChange(name, collapsed);
      });
      if (card.hasAttribute('data-expand-on-drag')) {
        card.addEventListener('dragenter', () => {
          if (card.classList.contains('is-collapsed')) this.#set(name, false);
        });
      }
    }
    this.apply({});
  }

  /** Aplica els desats al motor (els que no hi són, com per defecte). @param {Record<string, boolean>} collapsed */
  apply(collapsed) {
    for (const name of this.#cards.keys()) {
      this.#set(name, collapsed[name] ?? DEFAULTS[name] ?? false);
    }
  }

  /** @param {string} name @param {boolean} collapsed */
  #set(name, collapsed) {
    const card = /** @type {HTMLElement} */ (this.#cards.get(name));
    card.classList.toggle('is-collapsed', collapsed);
    const toggle = /** @type {HTMLButtonElement} */ (card.querySelector('.collapse-toggle'));
    toggle.setAttribute('aria-expanded', String(!collapsed));
    toggle.title = collapsed ? 'Desplega' : 'Plega';
    const chevron = toggle.querySelector('.bi');
    if (chevron) chevron.className = `bi ${collapsed ? 'bi-chevron-right' : 'bi-chevron-down'}`;
  }
}
