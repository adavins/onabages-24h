// @ts-check
// Processador d'àudio de la sortida (fase 3, pas 6b; D-086). A la barra d'estat, una icona i la barreta de quant
// comprimeix ara (el preajust i el volum, en passar-hi el ratolí); en fer-hi clic, s'obre ⚙ Configuració → «Processador»
// (D-168), que és on es canvien. Els canvis s'apliquen de seguida al motor (sense clics) i es desen allà.

import { $, el, setStyle, setText } from './dom.js';
import { formatGainDb } from '../format.js';
import { confirmAction } from './confirm.js';

/** Fins a quants dB arriba la barreta de la compressió (plena = 12 dB o més). */
const REDUCTION_FULL_DB = 12;

/** @typedef {{ root: HTMLElement, preset: HTMLSelectElement, volume: HTMLInputElement, volumeValue: HTMLElement,
 *   reductionFill: HTMLElement, reductionValue: HTMLElement, ceiling: HTMLElement }} ProcessorPanel */

export class ProcessorView {
  #send;
  /** @type {any} */
  #state = null;
  /** @type {ProcessorPanel | null} */
  #panel = null;
  #reductionDb = 0;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {() => void} openSettings  obre ⚙ Configuració → «Processador»
   */
  constructor(send, openSettings) {
    this.#send = send;
    $('status-processor').addEventListener('click', () => openSettings());
  }

  /** Torna a demanar l'estat al motor (en connectar-s'hi). */
  async refresh() {
    try {
      this.render((await this.#send('processor.get')).processor);
    } catch {
      this.render(null);
    }
  }

  /** @param {any} processor  de processor.get o processor.changed (docs/protocol.md, 4.9), o null si no se sap */
  render(processor) {
    this.#state = processor;
    const button = $('status-processor');
    button.classList.toggle('d-none', !processor);
    if (!processor) return;
    button.title = `Processador: ${processor.presetName} · volum ${formatGainDb(processor.volumeDb)}. La barreta és quant comprimeix ara.\nClica per canviar-los (⚙ Configuració → Processador).`;
    this.#renderPanel();
  }

  /** Quant comprimeix ara (dels mesuradors, unes 20 vegades per segon). @param {number | undefined} db */
  setReduction(db) {
    this.#reductionDb = Number.isFinite(db) ? Math.max(0, /** @type {number} */ (db)) : 0;
    const width = `${Math.min(100, (this.#reductionDb / REDUCTION_FULL_DB) * 100).toFixed(0)}%`;
    setStyle($('status-processor-fill'), 'width', width);
    // La pestanya de ⚙, si s'ha obert (si ja s'ha tancat, actualitzar-la no fa res)
    const panel = this.#panel;
    if (panel) {
      setStyle(panel.reductionFill, 'width', width);
      setText(panel.reductionValue, `${this.#reductionDb.toFixed(1).replace('.', ',')} dB`);
    }
  }

  /** La pestanya de ⚙ (es crea cada vegada que s'obre la configuració). @returns {HTMLElement} */
  createPanel() {
    const preset = /** @type {HTMLSelectElement} */ (el('select', 'form-select mb-3'));
    preset.id = 'processor-preset';
    preset.addEventListener('change', () => this.#set({ preset: preset.value }));
    const presetLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-label', 'Preajust'));
    presetLabel.htmlFor = preset.id;

    const volume = /** @type {HTMLInputElement} */ (el('input', 'form-range'));
    volume.type = 'range';
    volume.step = '0.5';
    volume.id = 'processor-volume';
    const volumeValue = el('strong', '');
    // Mentre s'arrossega, només es mostra el valor; s'envia en deixar-lo anar
    volume.addEventListener('input', () => setText(volumeValue, formatGainDb(Number(volume.value))));
    volume.addEventListener('change', () => this.#set({ volumeDb: Number(volume.value) }));
    const volumeLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-label'));
    volumeLabel.htmlFor = volume.id;
    volumeLabel.append('Volum de sortida: ', volumeValue);
    const reset = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary', '0 dB'));
    reset.type = 'button';
    reset.title = 'Torna el volum a 0 dB';
    reset.addEventListener('click', () => this.#set({ volumeDb: 0 }));
    const volumeRow = el('div', 'd-flex align-items-center gap-2 mb-3');
    volumeRow.append(volume, reset);

    const reductionValue = el('strong', '', '0 dB');
    const reductionLabel = el('div', 'form-label');
    reductionLabel.append('Compressió ara: ', reductionValue);
    const reductionBar = el('div', 'reduction-bar reduction-bar-wide mb-2');
    reductionBar.setAttribute('aria-hidden', 'true');
    const reductionFill = el('span', 'reduction-fill');
    reductionBar.append(reductionFill);
    const ceiling = el('p', 'form-text');

    const left = el('div', '');
    left.append(el('div', 'form-text mt-0 mb-2', 'Compressor i volum de tot el que surt cap a la taula (música, auxiliars i sweepers). Els canvis se senten de seguida.'),
      presetLabel, preset, volumeLabel, volumeRow);
    const right = el('div', '');
    right.append(reductionLabel, reductionBar, ceiling,
      el('p', 'form-text', 'Els canvis s\'apliquen de seguida i es desen sols (no cal «Desa»).'));
    const root = el('div', 'cfg-cols');
    root.append(left, right);
    for (const control of [preset, volume, reset]) control.dataset.needs = 'administrator';

    this.#panel = { root, preset, volume, volumeValue, reductionFill, reductionValue, ceiling };
    this.#renderPanel();
    this.setReduction(this.#reductionDb);
    return root;
  }

  #renderPanel() {
    const panel = this.#panel;
    const processor = this.#state;
    if (!panel) return;
    if (!processor) {
      panel.preset.disabled = true;
      panel.volume.disabled = true;
      return;
    }
    panel.preset.disabled = false;
    panel.volume.disabled = false;
    panel.preset.replaceChildren(...processor.presets.map((/** @type {any} */ p) => new Option(p.name, p.id)));
    panel.preset.value = processor.preset;
    panel.volume.min = String(processor.minVolumeDb);
    panel.volume.max = String(processor.maxVolumeDb);
    panel.volume.value = String(processor.volumeDb);
    setText(panel.volumeValue, formatGainDb(processor.volumeDb));
    setText(panel.ceiling, `La sortida no passa mai de ${formatGainDb(processor.ceilingDb).replace(' dB', ' dBFS')}, encara que se solapin dues pistes fortes (sostre de seguretat, sempre actiu).`);
  }

  /** @param {object} params */
  async #set(params) {
    try {
      this.render((await this.#send('processor.set', params)).processor);
    } catch (err) {
      if (this.#state) this.render(this.#state); // torna a mostrar el que hi havia
      await confirmAction({ title: 'No s\'ha pogut canviar el processador', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}

