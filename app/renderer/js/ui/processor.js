// @ts-check
// Processador d'àudio de la sortida (fase 3, pas 6b; D-086): a la barra d'estat, el preajust, el volum i quant comprimeix
// ara; en fer-hi clic, un diàleg per canviar-los. Els canvis s'apliquen de seguida al motor (sense clics) i es desen allà.

import { $, setStyle, setText } from './dom.js';
import { formatGainDb } from '../format.js';
import { confirmAction } from './confirm.js';

/** Fins a quants dB arriba la barreta de la compressió (plena = 12 dB o més). */
const REDUCTION_FULL_DB = 12;

export class ProcessorView {
  #send;
  /** @type {any} */
  #state = null;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    $('status-processor').addEventListener('click', () => this.#open());

    const presets = /** @type {HTMLSelectElement} */ ($('processor-preset'));
    presets.addEventListener('change', () => this.#set({ preset: presets.value }));

    const volume = /** @type {HTMLInputElement} */ ($('processor-volume'));
    // Mentre s'arrossega, només es mostra el valor; s'envia en deixar-lo anar
    volume.addEventListener('input', () => setText($('processor-volume-value'), formatGainDb(Number(volume.value))));
    volume.addEventListener('change', () => this.#set({ volumeDb: Number(volume.value) }));
    $('processor-volume-reset').addEventListener('click', () => this.#set({ volumeDb: 0 }));
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

    setText($('status-processor-text'), `Processador: ${processor.presetName} · ${formatGainDb(processor.volumeDb)}`);
    button.title = 'Compressor i volum de la sortida. Fes-hi clic per canviar-los.';

    // El diàleg, si és obert, també es posa al dia (algú altre ho pot haver canviat)
    const presets = /** @type {HTMLSelectElement} */ ($('processor-preset'));
    presets.replaceChildren(...processor.presets.map((/** @type {any} */ p) => {
      const option = document.createElement('option');
      option.value = p.id;
      option.textContent = p.name;
      return option;
    }));
    presets.value = processor.preset;
    const volume = /** @type {HTMLInputElement} */ ($('processor-volume'));
    volume.min = String(processor.minVolumeDb);
    volume.max = String(processor.maxVolumeDb);
    volume.value = String(processor.volumeDb);
    setText($('processor-volume-value'), formatGainDb(processor.volumeDb));
    setText($('processor-ceiling'), `La sortida no passa mai de ${formatGainDb(processor.ceilingDb).replace(' dB', ' dBFS')}, encara que se solapin dues pistes fortes (sostre de seguretat, sempre actiu).`);
  }

  /** Quant comprimeix ara (del mesuradors, unes 20 vegades per segon). @param {number | undefined} db */
  setReduction(db) {
    const value = Number.isFinite(db) ? Math.max(0, /** @type {number} */ (db)) : 0;
    const width = `${Math.min(100, (value / REDUCTION_FULL_DB) * 100).toFixed(0)}%`;
    setStyle($('status-processor-fill'), 'width', width);
    setStyle($('processor-reduction-fill'), 'width', width);
    setText($('processor-reduction-value'), `${value.toFixed(1).replace('.', ',')} dB`);
  }

  #open() {
    if (!this.#state) return;
    const dialog = /** @type {HTMLDialogElement} */ ($('processor-dialog'));
    this.render(this.#state);
    dialog.showModal();
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
