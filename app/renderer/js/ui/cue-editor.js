// @ts-check
// Editor senzill de punts (fase 3, pas 7e; D-093): sense escolta (la interfície no fa sonar res i el motor només té la
// sortida d'emissió). La forma d'ona la calcula el motor (library.waveform, en una escala de dB de −60 a 0) i aquí es
// dibuixa en un <canvas> 2D (sense acceleració gràfica: funciona per escriptori remot). Tres vistes: tota la pista, el
// principi i el final; els tres punts (inici, mescla, final) s'hi poden arrossegar o escriure.

import { $, el, icon } from './dom.js';
import { confirmAction } from './confirm.js';
import { parseSeconds, formatSeconds, cueProblems } from '../track-form.js';

/** Quant temps mostren les vistes ampliades. */
const START_VIEW_MS = 20_000;
const END_VIEW_MS = 45_000;

/** Llindars de l'analitzador (CueThresholds.Default): les línies discontínues. */
const THRESHOLDS = [
  { db: -25, key: 'start' },
  { db: -15, key: 'mix' },
  { db: -30, key: 'end' },
];

/** Colors dels punts (amb lletra a més del color). */
const COLORS = { start: '#4cc38a', mix: '#f0b44c', end: '#e5534b' };
const LETTERS = { start: 'I', mix: 'M', end: 'F' };

/** @typedef {'start' | 'mix' | 'end'} CueKey */
/** @typedef {{ canvas: HTMLCanvasElement, fromMs: number, toMs: number, peaks: number[] }} View */

export class CueEditor {
  #send;
  /** @type {any} */
  #track = null;
  /** @type {Record<CueKey, number | null>} */
  #cues = { start: null, mix: null, end: null };
  /** @type {View[]} */
  #views = [];
  /** @type {{ key: CueKey, view: View } | null} */
  #drag = null;
  /** La pista desada (es retorna en tancar el diàleg). @type {any} */
  #saved = null;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    for (const key of /** @type {CueKey[]} */ (['start', 'mix', 'end'])) {
      $(`cue-${key}`).addEventListener('input', () => {
        this.#cues[key] = parseSeconds(/** @type {HTMLInputElement} */ ($(`cue-${key}`)).value);
        this.#validate();
        this.#draw();
      });
    }
    for (const id of ['cue-overview', 'cue-zoom-start', 'cue-zoom-end']) {
      const canvas = /** @type {HTMLCanvasElement} */ ($(id));
      canvas.addEventListener('pointerdown', (e) => this.#pointerDown(canvas, e));
      canvas.addEventListener('pointermove', (e) => this.#pointerMove(canvas, e));
      canvas.addEventListener('pointerup', () => { this.#drag = null; });
      canvas.addEventListener('pointercancel', () => { this.#drag = null; });
    }
    $('cue-save').addEventListener('click', () => this.#save());
    $('cue-recalculate').addEventListener('click', () => this.#recalculate());
  }

  /**
   * Obre l'editor d'una pista (la fitxa sencera de library.get). Retorna la pista desada (si s'ha desat o recalculat) o null.
   * @param {any} track
   * @returns {Promise<any>}
   */
  async open(track) {
    this.#track = track;
    this.#saved = null;
    this.#cues = { start: track.startMs ?? 0, mix: track.mixMs ?? track.durationMs ?? 0, end: track.endMs ?? track.durationMs ?? 0 };
    $('cue-title').textContent = track.artist ? `${track.artist} - ${track.title}` : track.title;
    $('cue-duration').textContent = `${formatSeconds(track.durationMs)} s`;
    this.#fillFields();
    this.#showSource(track.cueSource);
    this.#views = [];
    const dialog = /** @type {HTMLDialogElement} */ ($('cue-dialog'));
    dialog.returnValue = '';
    this.#showFileProblem(null);
    dialog.showModal();
    if (track.fileState === 'missing') this.#showFileProblem("El fitxer d'aquesta pista falta: no se'n pot veure la forma d'ona.");
    else await this.#loadWaveforms();
    await new Promise((resolve) => dialog.addEventListener('close', resolve, { once: true }));
    return this.#saved;
  }

  #fillFields() {
    for (const key of /** @type {CueKey[]} */ (['start', 'mix', 'end'])) {
      /** @type {HTMLInputElement} */ ($(`cue-${key}`)).value = formatSeconds(this.#cues[key]);
    }
    this.#validate();
  }

  /** @param {string | null} source */
  #showSource(source) {
    const text = { radiodj: 'Ara: els de RadioDJ (es recalcularan sols).', analyzer: 'Ara: calculats per l\'analitzador.',
      manual: 'Ara: editats a mà (l\'analitzador no els tocarà).' }[/** @type {'radiodj'} */ (source)] ?? 'Ara: encara no n\'hi ha.';
    $('cue-source').textContent = `${text} Si els deses, quedaran com a editats a mà.`;
  }

  async #loadWaveforms() {
    const t = this.#track;
    const duration = t.durationMs ?? 0;
    $('cue-loading').textContent = 'Carregant la forma d\'ona…';
    const ranges = [
      { id: 'cue-overview', from: 0, to: duration },
      { id: 'cue-zoom-start', from: 0, to: Math.min(duration, START_VIEW_MS) },
      { id: 'cue-zoom-end', from: Math.max(0, duration - END_VIEW_MS), to: duration },
    ];
    $('cue-start-range').textContent = `(0 – ${formatSeconds(ranges[1].to)} s)`;
    $('cue-end-range').textContent = `(${formatSeconds(ranges[2].from)} – ${formatSeconds(ranges[2].to)} s)`;
    try {
      const views = await Promise.all(ranges.map(async (r) => {
        const canvas = /** @type {HTMLCanvasElement} */ ($(r.id));
        const buckets = Math.max(100, Math.min(2000, Math.round(canvas.clientWidth)));
        const w = await this.#send('library.waveform', { trackId: t.id, fromMs: r.from, toMs: r.to, buckets });
        return /** @type {View} */ ({ canvas, fromMs: w.fromMs, toMs: w.toMs, peaks: w.peaks });
      }));
      if (this.#track !== t) return; // ja s'ha obert una altra pista
      this.#views = views;
      $('cue-loading').textContent = '';
      this.#draw();
    } catch (err) {
      if (this.#track === t) this.#showFileProblem(`No es pot mostrar la forma d'ona: ${/** @type {Error} */ (err).message}`);
    }
  }

  /**
   * Si el fitxer falta o no es pot llegir (D-112): un avís ben visible i els gràfics amagats (buits, no diuen res i
   * semblaria que la pista és en silenci). Els punts es poden continuar escrivint a mà. Amb null, tot torna a la normalitat.
   * @param {string | null} message
   */
  #showFileProblem(message) {
    const box = $('cue-file-problem');
    box.classList.toggle('d-none', !message);
    box.replaceChildren(...(message ? [icon('bi-exclamation-triangle-fill'), ' ', el('span', '', message), el('div', 'small mt-1 font-monospace text-break', this.#track?.path ?? '')] : []));
    $('cue-waves').classList.toggle('d-none', !!message);
    /** @type {HTMLButtonElement} */ ($('cue-recalculate')).disabled = !!message; // l'analitzador tampoc no el podria llegir
    if (message) $('cue-loading').textContent = '';
  }

  #draw() {
    for (const view of this.#views) drawView(view, this.#cues);
  }

  #validate() {
    const problems = cueProblems(this.#cues.start, this.#cues.mix, this.#cues.end, this.#track?.durationMs);
    $('cue-problem').textContent = problems.join(' ');
    /** @type {HTMLButtonElement} */ ($('cue-save')).disabled = problems.length > 0;
  }

  // --- Arrossegar ---

  /** @param {HTMLCanvasElement} canvas @param {PointerEvent} e */
  #pointerDown(canvas, e) {
    const view = this.#views.find((v) => v.canvas === canvas);
    if (!view) return;
    const x = e.offsetX;
    // El punt més proper (com a molt a 8 píxels)
    /** @type {CueKey | null} */
    let best = null;
    let bestDistance = 8;
    for (const key of /** @type {CueKey[]} */ (['start', 'mix', 'end'])) {
      const ms = this.#cues[key];
      if (ms == null) continue;
      const distance = Math.abs(xOf(view, ms, canvas.clientWidth) - x);
      if (distance <= bestDistance) {
        best = key;
        bestDistance = distance;
      }
    }
    if (!best) return;
    this.#drag = { key: best, view };
    canvas.setPointerCapture(e.pointerId);
  }

  /** @param {HTMLCanvasElement} canvas @param {PointerEvent} e */
  #pointerMove(canvas, e) {
    const view = this.#views.find((v) => v.canvas === canvas);
    if (!view) return;
    if (!this.#drag) {
      // Cursor de «es pot arrossegar» a prop d'un punt
      const near = /** @type {CueKey[]} */ (['start', 'mix', 'end'])
        .some((k) => this.#cues[k] != null && Math.abs(xOf(view, /** @type {number} */ (this.#cues[k]), canvas.clientWidth) - e.offsetX) <= 8);
      canvas.style.cursor = near ? 'ew-resize' : 'default';
      return;
    }
    const v = this.#drag.view;
    const ratio = Math.min(1, Math.max(0, e.offsetX / canvas.clientWidth));
    this.#cues[this.#drag.key] = Math.round(v.fromMs + ratio * (v.toMs - v.fromMs));
    /** @type {HTMLInputElement} */ ($(`cue-${this.#drag.key}`)).value = formatSeconds(this.#cues[this.#drag.key]);
    this.#validate();
    this.#draw();
  }

  // --- Desar i recalcular ---

  async #save() {
    const { start, mix, end } = this.#cues;
    if (cueProblems(start, mix, end, this.#track?.durationMs).length > 0) return;
    try {
      const { track } = await this.#send('library.setCues', { trackId: this.#track.id, startMs: start, mixMs: mix, endMs: end });
      this.#saved = track;
      /** @type {HTMLDialogElement} */ ($('cue-dialog')).close('saved');
    } catch (err) {
      $('cue-problem').textContent = /** @type {Error} */ (err).message;
    }
  }

  async #recalculate() {
    if (this.#track.cueSource === 'manual' && !(await confirmAction({
      title: 'Recalcular els punts',
      message: 'Els punts d\'ara estan editats a mà: es substituiran pels que calculi l\'analitzador.',
      confirm: 'Recalcula',
    }))) return;
    $('cue-loading').textContent = 'Calculant…';
    try {
      const { track } = await this.#send('library.recalculateCues', { trackId: this.#track.id });
      this.#track = track;
      this.#saved = track;
      this.#cues = { start: track.startMs, mix: track.mixMs, end: track.endMs };
      this.#fillFields();
      this.#showSource(track.cueSource);
      $('cue-loading').textContent = '';
      this.#draw();
    } catch (err) {
      $('cue-loading').textContent = /** @type {Error} */ (err).message;
    }
  }
}

/** On cau un temps dins d'una vista (en píxels CSS). @param {View} view @param {number} ms @param {number} width */
function xOf(view, ms, width) {
  return ((ms - view.fromMs) / (view.toMs - view.fromMs)) * width;
}

/**
 * Dibuixa una vista: la forma d'ona (escala de dB, de baix a dalt), els llindars i els punts.
 * @param {View} view @param {Record<CueKey, number | null>} cues
 */
function drawView(view, cues) {
  const canvas = view.canvas;
  const ratio = window.devicePixelRatio || 1;
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  const g = /** @type {CanvasRenderingContext2D} */ (canvas.getContext('2d'));
  g.setTransform(ratio, 0, 0, ratio, 0, 0);
  g.clearRect(0, 0, width, height);
  g.fillStyle = '#14171b';
  g.fillRect(0, 0, width, height);

  // Forma d'ona: 0 (−60 dB) a baix, 255 (0 dB) a dalt
  g.fillStyle = '#5a7fa8';
  const n = view.peaks.length;
  for (let i = 0; i < n; i++) {
    const h = (view.peaks[i] / 255) * height;
    const x = (i / n) * width;
    g.fillRect(x, height - h, Math.max(1, width / n), h);
  }

  // Llindars de l'analitzador
  g.setLineDash([4, 4]);
  g.lineWidth = 1;
  for (const t of THRESHOLDS) {
    const y = height - ((t.db + 60) / 60) * height;
    g.strokeStyle = COLORS[/** @type {CueKey} */ (t.key)];
    g.globalAlpha = 0.5;
    g.beginPath();
    g.moveTo(0, y);
    g.lineTo(width, y);
    g.stroke();
  }
  g.globalAlpha = 1;
  g.setLineDash([]);

  // Punts: línia, i una etiqueta amb la lletra (no només el color)
  g.font = 'bold 11px system-ui, sans-serif';
  for (const key of /** @type {CueKey[]} */ (['start', 'mix', 'end'])) {
    const ms = cues[key];
    if (ms == null || ms < view.fromMs || ms > view.toMs) continue;
    const x = Math.round(xOf(view, ms, width)) + 0.5;
    g.strokeStyle = COLORS[key];
    g.lineWidth = 2;
    g.beginPath();
    g.moveTo(x, 0);
    g.lineTo(x, height);
    g.stroke();
    const labelX = Math.min(Math.max(x - 7, 0), width - 14);
    g.fillStyle = COLORS[key];
    g.fillRect(labelX, 0, 14, 14);
    g.fillStyle = '#111';
    g.fillText(LETTERS[key], labelX + 3.5, 11);
  }
}
