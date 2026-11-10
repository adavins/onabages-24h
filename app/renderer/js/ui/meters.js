// @ts-check
// Vúmetre d'un canal: el motor envia el pic cada 50 ms (esdeveniment «meters»); aquí es fa la caiguda suau
// i la retenció del pic. Es dibuixa movent dues capes (sense canvas ni efectes): va bé per escriptori remot.

import { meterFraction } from '../format.js';

const FALL_DB_PER_SECOND = 24;   // velocitat de caiguda de la barra
const PEAK_HOLD_MS = 1500;       // quant es queda quiet el marcador de pic
const SILENCE_DB = -90;
const STALE_MS = 300;            // sense dades fa aquesta estona (motor desconnectat): la barra cau a zero

export class ChannelMeter {
  #cover;
  #peakMark;
  #shown = SILENCE_DB;
  #peak = SILENCE_DB;
  #peakAt = 0;
  #level = SILENCE_DB;
  #levelAt = 0;

  /** @param {HTMLElement} element  el .meter, amb .meter-cover i .meter-peak a dins */
  constructor(element) {
    this.#cover = /** @type {HTMLElement} */ (element.querySelector('.meter-cover'));
    this.#peakMark = /** @type {HTMLElement} */ (element.querySelector('.meter-peak'));
  }

  /** Nou nivell del motor (dBFS). @param {number} db */
  set(db) {
    this.#level = db;
    this.#levelAt = performance.now();
  }

  /** Es crida a cada fotograma. @param {number} now  @param {number} elapsedMs */
  draw(now, elapsedMs) {
    const target = now - this.#levelAt > STALE_MS ? SILENCE_DB : this.#level;

    // La barra puja de cop i baixa a poc a poc
    this.#shown = Math.max(target, this.#shown - (FALL_DB_PER_SECOND * elapsedMs) / 1000);
    if (target >= this.#peak || now - this.#peakAt > PEAK_HOLD_MS) {
      this.#peak = target;
      this.#peakAt = now;
    }

    const fraction = meterFraction(this.#shown);
    this.#cover.style.width = `${(1 - fraction) * 100}%`;

    const peakFraction = meterFraction(this.#peak);
    this.#peakMark.style.display = peakFraction > 0 ? 'block' : 'none';
    this.#peakMark.style.left = `calc(${peakFraction * 100}% - 2px)`;
  }
}

/** Vúmetres E i D de la sortida, amb un sol bucle de dibuix. */
export class OutputMeters {
  #left;
  #right;
  #last = performance.now();

  /** @param {HTMLElement} left  @param {HTMLElement} right */
  constructor(left, right) {
    this.#left = new ChannelMeter(left);
    this.#right = new ChannelMeter(right);
    const frame = (/** @type {number} */ now) => {
      const elapsed = Math.min(200, now - this.#last);
      this.#last = now;
      this.#left.draw(now, elapsed);
      this.#right.draw(now, elapsed);
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  }

  /** @param {[number, number]} output  [esquerre, dret] en dBFS */
  set(output) {
    this.#left.set(output[0]);
    this.#right.set(output[1]);
  }
}
