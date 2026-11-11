// @ts-check
// Pantalla d'emissió (fase 1, passos 10b i 10c). Només mostra l'estat del motor i hi envia ordres: l'àudio,
// la cua i l'AutoDJ són al motor, que continua sonant encara que aquesta finestra es tanqui.

import { EngineClient } from './engine-client.js';
import { formatClock, formatLongDate } from './format.js';
import { $, setText } from './ui/dom.js';
import { confirmAction } from './ui/confirm.js';
import { OutputMeters } from './ui/meters.js';
import { NowPlaying } from './ui/now-playing.js';
import { QueueView, HistoryView } from './ui/queue.js';
import { AutoDjView } from './ui/autodj.js';
import { renderOutput, renderNowPlaying, renderWarnings } from './ui/statusbar.js';
import { PlannerView } from './ui/planner.js';
import { UpcomingEvents } from './ui/upcoming.js';

/**
 * @typedef {{
 *   startEngine: () => Promise<{ port: number, token: string }>,
 *   findEngine: () => Promise<{ port: number, token: string } | null>,
 *   onCloseRequested: (callback: () => void) => void,
 *   closeWindow: () => Promise<void>,
 * }} Bridge
 */
const bridge = /** @type {Bridge} */ (/** @type {any} */ (window).onabages);

// --- Rellotge (només per mostrar l'hora: la puntualitat és cosa del motor, D-008) ---

function tickClock() {
  const now = new Date();
  setText($('clock'), formatClock(now));
  setText($('clock-date'), formatLongDate(now));
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

/** @type {EngineClient | null} */
let client = null;
let failedAttempts = 0;

/** Envia una ordre al motor. @param {string} cmd  @param {object} [params] */
function send(cmd, params = {}) {
  if (!client) return Promise.reject(new Error('El motor no està connectat'));
  return client.request(cmd, params);
}

const meters = new OutputMeters($('meter-left'), $('meter-right'));
const nowPlaying = new NowPlaying();
const queueView = new QueueView(send);
const historyView = new HistoryView(send);
const autoDjView = new AutoDjView(send);
const planner = new PlannerView(send);
/** L'última cua rebuda: la previsió es torna a calcular quan canvien els pròxims esdeveniments. @type {any} */
let lastQueue = { current: null, currentEndsAt: null, upcoming: [] };
const upcoming = new UpcomingEvents(send, () => queueView.render(lastQueue, upcoming.events));
const problems = { autodj: /** @type {string | null} */ (null), output: /** @type {string | null} */ (null) };
renderWarnings(problems);

/** @param {any} queue  docs/protocol.md, 4.2 */
function showQueue(queue) {
  nowPlaying.setCurrent(queue.current);
  lastQueue = queue;
  queueView.render(queue, upcoming.events);
  /** @type {HTMLButtonElement} */ ($('btn-skip')).disabled = !queue.current;
}

/** @param {any} state */
function showAutoDj(state) {
  autoDjView.render(state);
  problems.autodj = state.problem;
  renderWarnings(problems);
}

/** @param {any} output */
function showOutput(output) {
  renderOutput(output);
  problems.output = output.problem;
  renderWarnings(problems);
}

async function refreshNowPlayingStatus() {
  try {
    renderNowPlaying((await send('nowplaying.get')).nowPlaying);
  } catch { /* sense connexió: ja ho diu la capçalera */ }
}

// --- Pantalles: Emissió i Planificador (fase 2, pas 8a). L'emissió continua igual, es mostri la que es mostri. ---

/** @param {'emissio' | 'planificador'} name */
function showView(name) {
  for (const view of /** @type {const} */ (['emissio', 'planificador'])) {
    const active = view === name;
    $(`view-${view}`).classList.toggle('d-none', !active);
    const tab = $(`nav-${view}`);
    tab.classList.toggle('active', active);
    if (active) tab.setAttribute('aria-current', 'page');
    else tab.removeAttribute('aria-current');
  }
  if (name === 'planificador') planner.show();
  else planner.hide();
}
$('nav-emissio').addEventListener('click', () => showView('emissio'));
$('nav-planificador').addEventListener('click', () => showView('planificador'));

// Saltar a la següent: interromp la cançó, per tant demana confirmació
// «Regenera la cua» (D-034): només canvia les pistes que ha posat l'AutoDJ i no interromp res, per això no demana confirmació
$('btn-regenerate').addEventListener('click', async () => {
  const button = /** @type {HTMLButtonElement} */ ($('btn-regenerate'));
  button.disabled = true;
  try {
    await send('queue.regenerate');
    // La cua nova arriba amb queue.changed
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut regenerar la cua', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
  } finally {
    button.disabled = false;
  }
});

$('btn-skip').addEventListener('click', async () => {
  const title = $('np-title').textContent;
  const ok = await confirmAction({
    title: 'Saltar a la següent',
    message: `«${title}» baixarà amb un fos d'1 segon i començarà la pista següent de la cua.`,
    confirm: 'Salta a la següent',
  });
  if (!ok) return;
  try {
    await send('player.next', { fadeMs: 1000 });
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut saltar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
  }
});

// Els avisos, sencers, en clicar el comptador de la barra de sota
$('status-warnings').addEventListener('click', () => {
  const list = [problems.output, problems.autodj].filter(Boolean);
  if (list.length === 0) return;
  confirmAction({ title: 'Avisos', message: list.join('\n\n'), confirm: 'D\'acord', info: true });
});

// En clicar la X (D-054): per defecte només es tanca la finestra i l'emissió continua.
// «Atura l'emissió i tanca» atura el motor; si no hi ha connexió amb el motor, aquesta opció no hi és.
bridge.onCloseRequested(async () => {
  const dialog = /** @type {HTMLDialogElement} */ ($('close-dialog'));
  if (dialog.open) return;
  const connected = client?.connected ?? false;
  /** @type {HTMLButtonElement} */ ($('close-stop')).disabled = !connected;
  $('close-offline').classList.toggle('d-none', connected);

  const choice = await new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue), { once: true });
    dialog.returnValue = '';
    dialog.showModal();
    $('close-window').focus(); // per defecte, l'opció que no atura res
  });

  if (choice === 'window') {
    await bridge.closeWindow();
  } else if (choice === 'stop') {
    try {
      await send('engine.shutdown');
      await bridge.closeWindow();
    } catch (err) {
      await confirmAction({
        title: 'No s\'ha pogut aturar l\'emissió',
        message: `${/** @type {Error} */ (err).message}\n\nLa finestra no s'ha tancat.`,
        confirm: 'D\'acord',
        info: true,
      });
    }
  }
});

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
      showQueue({ current: null, upcoming: [] });
      // Potser el motor ha tornat a arrencar amb un altre testimoni: se'n llegeix el fitxer d'execució
      const found = await bridge.findEngine().catch(() => null);
      if (found) client?.setEngine(found.port, found.token);
    }
  });

  const detail = (/** @type {Event} */ e) => /** @type {CustomEvent} */ (e).detail;
  client.addEventListener('ev:queue.changed', (e) => {
    showQueue(detail(e).queue);
    // autodj.changed no s'envia cada vegada que l'AutoDJ afegeix una pista: el proper pas es demana aquí
    send('autodj.get').then((r) => showAutoDj(r.autodj)).catch(() => {});
  });
  client.addEventListener('ev:autodj.changed', (e) => showAutoDj(detail(e).autodj));
  client.addEventListener('ev:output.changed', (e) => showOutput(detail(e).output));
  client.addEventListener('ev:player.trackStarted', () => {
    historyView.refresh();
    setTimeout(refreshNowPlayingStatus, 500); // el fitxer s'escriu en un fil a part
  });
  client.addEventListener('ev:player.trackEnded', () => historyView.refresh());
  client.addEventListener('ev:scheduler.changed', () => {
    planner.refresh();
    upcoming.refresh();
  });
  client.addEventListener('ev:scheduler.fired', () => upcoming.refresh());
  client.addEventListener('ev:meters', (e) => {
    const ev = detail(e);
    meters.set(ev.output);
    nowPlaying.setPosition(ev.position);
  });

  client.connect();
}

/** En connectar-se (o tornar-s'hi a connectar): tot l'estat actual. */
async function loadState() {
  try {
    const [queue, autodj, rotations, output] = await Promise.all([
      send('queue.get'), send('autodj.get'), send('rotations.list'), send('output.get'),
    ]);
    showQueue(queue.queue);
    autoDjView.setRotations(rotations.rotations);
    showAutoDj(autodj.autodj);
    showOutput(output.output);
    await Promise.all([historyView.refresh(), refreshNowPlayingStatus()]);
    planner.refresh(); // només si es mostra
    upcoming.refresh();
  } catch (err) {
    console.error('No s\'ha pogut llegir l\'estat del motor:', err);
  }
}

// El títol per a BUTT no té esdeveniment propi: es mira de tant en tant si s'ha pogut escriure
setInterval(() => {
  if (client?.connected) refreshNowPlayingStatus();
}, 10_000);

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
