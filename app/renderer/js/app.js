// @ts-check
// Pantalla d'emissió (fase 1, pas 10b). Només mostra l'estat del motor i, més endavant, hi envia ordres:
// l'àudio, la cua i l'AutoDJ són al motor, que continua sonant encara que aquesta finestra es tanqui.

import { EngineClient } from './engine-client.js';
import { formatClock, formatLongDate } from './format.js';
import { OutputMeters } from './ui/meters.js';
import { NowPlaying } from './ui/now-playing.js';

/** @typedef {{ startEngine: () => Promise<{ port: number, token: string }>, findEngine: () => Promise<{ port: number, token: string } | null> }} Bridge */
const bridge = /** @type {Bridge} */ (/** @type {any} */ (window).onabages);

/** @param {string} id */
function $(id) {
  return /** @type {HTMLElement} */ (document.getElementById(id));
}

// --- Rellotge (només per mostrar l'hora: la puntualitat és cosa del motor, D-008) ---

function tickClock() {
  const now = new Date();
  $('clock').textContent = formatClock(now);
  $('clock-date').textContent = formatLongDate(now);
}
tickClock();
setInterval(tickClock, 250);

// --- Estat de la connexió: sempre color + icona + text ---

const STATUS = {
  starting: { css: 'status-connecting', icon: 'bi-hourglass-split', text: 'Arrencant el motor…' },
  connecting: { css: 'status-connecting', icon: 'bi-hourglass-split', text: 'Connectant amb el motor…' },
  connected: { css: 'status-connected', icon: 'bi-check-circle-fill', text: 'Motor connectat' },
  disconnected: { css: 'status-disconnected', icon: 'bi-x-octagon-fill', text: 'Sense connexió amb el motor' },
};

/** @param {keyof typeof STATUS} state */
function showStatus(state) {
  const s = STATUS[state];
  const el = $('engine-status');
  el.className = `engine-status ${s.css}`;
  el.innerHTML = '';
  const icon = document.createElement('i');
  icon.className = `bi ${s.icon}`;
  const label = document.createElement('span');
  label.textContent = s.text;
  el.append(icon, ' ', label);
}

/**
 * Avís a sota de la capçalera quan no hi ha motor.
 * @param {string | null} title  null = amaga l'avís
 * @param {string} [detail]
 */
function showBanner(title, detail = '') {
  $('engine-banner').classList.toggle('d-none', title === null);
  if (title === null) return;
  $('engine-banner-title').textContent = title;
  $('engine-banner-detail').textContent = detail;
}

// --- Pantalla ---

const meters = new OutputMeters($('meter-left'), $('meter-right'));
const nowPlaying = new NowPlaying();

/** @type {EngineClient | null} */
let client = null;
let failedAttempts = 0;

/** Es connecta al motor amb aquest port i testimoni (o hi torna a apuntar el client, si ja n'hi ha). */
function connect(/** @type {{ port: number, token: string }} */ info) {
  if (client) {
    client.setEngine(info.port, info.token);
    return;
  }
  client = new EngineClient(info.port, info.token);

  client.addEventListener('status', async (e) => {
    const { state } = /** @type {CustomEvent} */ (e).detail;
    if (state === 'connected') {
      failedAttempts = 0;
      showStatus('connected');
      showBanner(null);
      await loadState();
    } else if (state === 'connecting') {
      if (failedAttempts === 0) showStatus('connecting');
    } else {
      failedAttempts++;
      showStatus('disconnected');
      showBanner('No hi ha connexió amb el motor.',
        'Si el motor funciona, l\'emissió continua. Es torna a provar cada pocs segons.');
      nowPlaying.setCurrent(null);
      // Potser el motor ha tornat a arrencar amb un altre testimoni: se'n llegeix el fitxer d'execució
      const found = await bridge.findEngine().catch(() => null);
      if (found) client?.setEngine(found.port, found.token);
    }
  });

  client.addEventListener('ev:queue.changed', (e) => nowPlaying.setCurrent(/** @type {CustomEvent} */ (e).detail.queue.current));
  client.addEventListener('ev:meters', (e) => {
    const ev = /** @type {CustomEvent} */ (e).detail;
    meters.set(ev.output);
    nowPlaying.setPosition(ev.position);
  });

  client.connect();
}

/** En connectar-se (o tornar-s'hi a connectar): l'estat actual. */
async function loadState() {
  try {
    const { queue } = await /** @type {EngineClient} */ (client).request('queue.get');
    nowPlaying.setCurrent(queue.current);
  } catch (err) {
    console.error('No s\'ha pogut llegir la cua:', err);
  }
}

/** Arrenca el motor (si no n'hi ha cap) i s'hi connecta. */
async function start() {
  showStatus('starting');
  showBanner(null);
  /** @type {HTMLButtonElement} */ ($('btn-start-engine')).disabled = true;
  try {
    connect(await bridge.startEngine());
  } catch (err) {
    showStatus('disconnected');
    showBanner('No s\'ha pogut arrencar el motor.', String(/** @type {Error} */ (err)?.message ?? err));
  } finally {
    /** @type {HTMLButtonElement} */ ($('btn-start-engine')).disabled = false;
  }
}

$('btn-start-engine').addEventListener('click', start);
start();
