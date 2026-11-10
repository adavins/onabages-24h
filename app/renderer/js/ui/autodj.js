// @ts-check
// AutoDJ: si està actiu, quina rotació fa servir i quin és el proper pas; interruptor (aturar-lo demana confirmació,
// perquè si la cua es buida s'acaba la música) i selector de rotació. L'estat és del motor (autodj.changed).

import { $, el, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';

/** Text d'un pas de rotació: «Music / Ona Bages Adult 5 + etiqueta». @param {any} step */
function stepText(step) {
  let text = step.subcategory ? `${step.category} / ${step.subcategory}` : step.category;
  if (step.tag) text += ` + ${step.tag}`;
  return text;
}

export class AutoDjView {
  #send;
  /** @type {any} */
  #state = null;
  /** @type {any[]} */
  #rotations = [];

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    $('btn-autodj-toggle').addEventListener('click', () => this.#toggle());
    $('autodj-rotation').addEventListener('change', (e) => this.#changeRotation(/** @type {HTMLSelectElement} */ (e.target)));
  }

  /** @param {any[]} rotations  de rotations.list */
  setRotations(rotations) {
    this.#rotations = rotations;
    const select = /** @type {HTMLSelectElement} */ ($('autodj-rotation'));
    select.replaceChildren(...rotations.map((r) => {
      const option = /** @type {HTMLOptionElement} */ (el('option', '', `${r.name} (${r.steps.length} passos)`));
      option.value = String(r.id);
      return option;
    }));
    if (this.#state) this.render(this.#state);
  }

  /** @param {any} state  de autodj.get o autodj.changed (docs/protocol.md, 4.5) */
  render(state) {
    this.#state = state;

    $('autodj-state').replaceChildren(state.enabled
      ? statusLabel('text-success', 'bi-check-circle-fill', 'AutoDJ actiu')
      : statusLabel('text-warning', 'bi-pause-circle-fill', 'AutoDJ aturat: no afegeix música a la cua'));

    const toggle = $('btn-autodj-toggle');
    toggle.className = `btn btn-sm ${state.enabled ? 'btn-outline-warning' : 'btn-success'}`;
    toggle.textContent = state.enabled ? 'Atura l\'AutoDJ' : 'Activa l\'AutoDJ';

    const select = /** @type {HTMLSelectElement} */ ($('autodj-rotation'));
    select.value = state.rotation ? String(state.rotation.id) : '';

    // Proper pas de la rotació (també a la capçalera de la cua)
    const rotation = this.#rotations.find((r) => r.id === state.rotation?.id);
    const step = rotation?.steps[state.nextStep];
    const stepLine = rotation && step ? `Proper pas: ${state.nextStep + 1} de ${rotation.steps.length} · ${stepText(step)}` : '';
    $('autodj-next').textContent = stepLine;
    $('queue-rotation').textContent = !state.enabled
      ? 'AutoDJ aturat'
      : state.rotation
        ? `Rotació ${state.rotation.name}${rotation ? ` · proper pas ${state.nextStep + 1} de ${rotation.steps.length}` : ''}`
        : 'Sense rotació: música de tota la biblioteca';

    const problem = $('autodj-problem');
    problem.classList.toggle('d-none', !state.problem);
    $('autodj-problem-text').textContent = state.problem ?? '';
  }

  async #toggle() {
    if (!this.#state) return;
    const enable = !this.#state.enabled;
    if (!enable) {
      const ok = await confirmAction({
        title: 'Aturar l\'AutoDJ',
        message: 'Si atures l\'AutoDJ, ja no s\'afegirà música a la cua. Quan s\'acabin les pistes que hi ha, l\'emissió es quedarà en silenci.',
        confirm: 'Atura l\'AutoDJ',
      });
      if (!ok) return;
    }
    await this.#set({ enabled: enable });
  }

  /** @param {HTMLSelectElement} select */
  async #changeRotation(select) {
    const id = Number(select.value);
    if (!Number.isFinite(id)) return;
    await this.#set({ rotationId: id });
  }

  /** @param {object} params */
  async #set(params) {
    try {
      const { autodj } = await this.#send('autodj.set', params);
      this.render(autodj);
    } catch (err) {
      if (this.#state) this.render(this.#state); // torna a mostrar el que hi havia
      await confirmAction({ title: 'No s\'ha pogut canviar l\'AutoDJ', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}
