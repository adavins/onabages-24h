// @ts-check
// AutoDJ: si està actiu, quina rotació fa servir, d'on ve i quin és el proper pas; interruptor i selector de rotació.
// L'estat és del motor (autodj.changed). Aturar-lo (D-073) és sempre per un temps: després es torna a activar sol, i
// també abans si la cua s'acaba (mai silenci).

import { $, el, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { askDuration } from './duration.js';

/** «18:30» d'una hora ISO. @param {string} iso */
function hhmm(iso) {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

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

    // A la barra de la cua (D-083): curt; l'explicació, en passar-hi el ratolí
    $('autodj-state').replaceChildren(state.enabled
      ? statusLabel('text-success', 'bi-check-circle-fill', 'AutoDJ actiu')
      : statusLabel('text-warning', 'bi-pause-circle-fill',
        `AutoDJ aturat${state.disabledUntil ? ` fins a les ${hhmm(state.disabledUntil)}` : ''}`));
    $('autodj-state').title = state.enabled
      ? 'L\'AutoDJ afegeix música a la cua amb la rotació triada'
      : 'L\'AutoDJ no afegeix música a la cua. Si la cua s\'acaba, es torna a activar sol.';

    const toggle = $('btn-autodj-toggle');
    toggle.className = `btn btn-sm ${state.enabled ? 'btn-outline-warning' : 'btn-success'}`;
    toggle.textContent = state.enabled ? 'Atura l\'AutoDJ' : 'Activa l\'AutoDJ';

    const select = /** @type {HTMLSelectElement} */ ($('autodj-rotation'));
    select.value = state.rotation ? String(state.rotation.id) : '';

    // Proper pas de la rotació i d'on ve (pantalla-principal-i-encadenament.md, millora 6).
    // Si no hi cap a la barra, es talla: el text sencer surt en passar-hi el ratolí.
    const rotation = this.#rotations.find((r) => r.id === state.rotation?.id);
    const step = rotation?.steps[state.nextStep];
    const stepLine = !state.rotation
      ? 'Sense rotació: música de tota la biblioteca'
      : rotation && step ? `Proper pas ${state.nextStep + 1} de ${rotation.steps.length} · ${stepText(step)}` : '';
    const origin = state.rotation && state.rotationChangedAt
      ? `Rotació des de les ${hhmm(state.rotationChangedAt)}${state.rotationChangedBy ? `, ${state.rotationChangedBy}` : ''}`
      : '';
    $('autodj-next').textContent = stepLine;
    $('autodj-next').title = [stepLine, origin].filter(Boolean).join('\n');
    select.title = origin || 'Rotació de l\'AutoDJ';

    const problem = $('autodj-problem');
    problem.classList.toggle('d-none', !state.problem);
    $('autodj-problem-text').textContent = state.problem ?? '';

    // Quan s'ha tornat a activar sol (per temps o perquè la cua s'acabava)
    $('autodj-notice').classList.toggle('d-none', !state.notice);
    $('autodj-notice-text').textContent = state.notice ?? '';
  }

  async #toggle() {
    if (!this.#state) return;
    const enable = !this.#state.enabled;
    if (!enable) {
      const minutes = await askDuration({
        title: 'Aturar l\'AutoDJ',
        message: 'Mentre estigui aturat, l\'AutoDJ no afegirà música a la cua: sonarà només el que hi posis. Si la cua s\'acaba, ' +
          'es tornarà a activar sol perquè no hi hagi silenci.',
        confirm: 'Atura l\'AutoDJ',
      });
      if (minutes == null) return;
      await this.#set({ enabled: false, minutes });
      return;
    }
    await this.#set({ enabled: true });
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
