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
import { EventsPause } from './ui/events-pause.js';
import { LibraryView, TRACK_DRAG_TYPE } from './ui/library.js';
import { AuxPlayersView } from './ui/aux-players.js';
import { NotesView } from './ui/notes.js';
import { ProcessorView } from './ui/processor.js';
import { TrackManagerView } from './ui/track-manager.js';
import { ImportView } from './ui/import.js';
import { FoldersView } from './ui/folders.js';
import { RerootView } from './ui/reroot.js';
import { RotationsView } from './ui/rotations.js';
import { folderWarnings } from './folders-text.js';
import { setupZoom, applyZoom } from './ui/zoom.js';
import { setupWindowFrame } from './ui/window-frame.js';
import { CollapsibleCards } from './ui/collapsible.js';

/**
 * @typedef {{
 *   startEngine: () => Promise<{ port: number, token: string }>,
 *   findEngine: () => Promise<{ port: number, token: string } | null>,
 *   onCloseRequested: (callback: () => void) => void,
 *   closeWindow: () => Promise<void>,
 *   chooseAudioFile: (defaultPath?: string) => Promise<string | null>,
 *   chooseFolder: (defaultPath?: string) => Promise<string | null>,
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
const eventsPause = new EventsPause(send);
const library = new LibraryView(send);
queueView.acceptDrops(TRACK_DRAG_TYPE, (track, position) => library.add(track, position));
const auxPlayers = new AuxPlayersView(send, TRACK_DRAG_TYPE);
const notes = new NotesView(send);
const processor = new ProcessorView(send);
const folders = new FoldersView(send, (path) => bridge.chooseFolder(path));
const rotationsView = new RotationsView(send);
const trackManager = new TrackManagerView(send, (path) => bridge.chooseAudioFile(path), folders);
new RerootView(send, (path) => bridge.chooseFolder(path));
const importView = new ImportView(send, (path) => bridge.chooseFolder(path), () => trackManager.importContext());

// Pestanyes del quadre de la dreta: Cerca i Notes (fase 3, pas 4b)
/** @param {'search' | 'notes'} name */
function showSideTab(name) {
  for (const tab of /** @type {const} */ (['search', 'notes'])) {
    const active = tab === name;
    $(`pane-${tab}`).classList.toggle('d-none', !active);
    const button = $(`tab-${tab}`);
    button.classList.toggle('active', active);
    button.setAttribute('aria-selected', String(active));
  }
}
// --- Capçalera com a barra de títol, i zoom (desat al motor, ui.set) ---

setupWindowFrame();

/** @type {number | undefined} */
let zoomIndicatorTimer;
setupZoom(async (zoom) => {
  const indicator = $('zoom-indicator');
  indicator.textContent = `Zoom ${Math.round(zoom * 100)} %`;
  indicator.classList.add('visible');
  clearTimeout(zoomIndicatorTimer);
  zoomIndicatorTimer = window.setTimeout(() => indicator.classList.remove('visible'), 1500);
  try {
    await send('ui.set', { zoom });
  } catch (err) {
    console.warn('No s\'ha pogut desar el zoom', err); // sense motor: es queda aplicat fins que es tanqui
  }
});

// Requadres plegables (D-083): l'estat es desa al motor
const cards = new CollapsibleCards((name, collapsed) => {
  send('ui.set', { collapsed: { [name]: collapsed } })
    .catch((err) => console.warn('No s\'ha pogut desar el requadre plegat', err));
});

/** El zoom i els requadres plegats desats al motor (en connectar-s'hi). */
async function loadUiSettings() {
  try {
    const ui = await send('ui.get');
    applyZoom(ui.zoom);
    cards.apply(ui.collapsed);
  } catch { /* motor antic o sense connexió: es queda com està */ }
}

$('tab-search').addEventListener('click', () => showSideTab('search'));
$('tab-notes').addEventListener('click', () => showSideTab('notes'));
const problems = {
  autodj: /** @type {string | null} */ (null),
  output: /** @type {string | null} */ (null),
  /** Les carpetes vigilades que no es poden revisar o tenen fitxers que falten (fase 3, pas 7c-2). @type {string[]} */
  folders: [],
};
renderWarnings(problems);
folders.onChange((list) => {
  problems.folders = folderWarnings(list);
  renderWarnings(problems);
});

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
  rotationsView.setActive(state.rotation?.id ?? null);
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

/** @param {'emissio' | 'planificador' | 'biblioteca' | 'rotacions'} name */
function showView(name) {
  for (const view of /** @type {const} */ (['emissio', 'planificador', 'biblioteca', 'rotacions'])) {
    const active = view === name;
    $(`view-${view}`).classList.toggle('d-none', !active);
    const tab = $(`nav-${view}`);
    tab.classList.toggle('active', active);
    if (active) tab.setAttribute('aria-current', 'page');
    else tab.removeAttribute('aria-current');
  }
  if (name === 'planificador') planner.show();
  else planner.hide();
  if (name === 'biblioteca') trackManager.show();
  else trackManager.hide();
  if (name === 'rotacions') rotationsView.show();
  else rotationsView.hide();
}
$('nav-emissio').addEventListener('click', () => showView('emissio'));
$('nav-planificador').addEventListener('click', () => showView('planificador'));
$('nav-biblioteca').addEventListener('click', () => showView('biblioteca'));
$('nav-rotacions').addEventListener('click', () => showView('rotacions'));

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
  const list = [problems.output, problems.autodj, ...problems.folders].filter(Boolean);
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
  // Una importació en curs (fase 3, pas 7b): la fa el motor, no la finestra
  const importing = connected ? importView.runningText() : null;
  $('close-import').classList.toggle('d-none', !importing);
  $('close-import-text').textContent = importing
    ? `${importing}. Si només tanques la finestra, la importació continua. Si atures l'emissió, s'aturarà a mig fer: ` +
      'el que ja s\'ha importat es queda, i la pots tornar a fer després (les pistes que ja hi són se salten).'
    : '';

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
      auxPlayers.refresh(); // sense connexió: es buida el que sonava
      processor.render(null); // no se sap com està: s'amaga fins que es torni a connectar
      processor.setReduction(0);
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
  client.addEventListener('ev:scheduler.paused', (e) => eventsPause.show(detail(e).paused));
  // Reproductors auxiliars i notes (fase 3, pas 4b)
  client.addEventListener('ev:aux.started', (e) => {
    auxPlayers.started(detail(e).player);
    auxPlayers.refresh();
  });
  client.addEventListener('ev:aux.ended', () => auxPlayers.refresh());
  client.addEventListener('ev:aux.loaded', () => auxPlayers.refresh());
  client.addEventListener('ev:aux.failed', (e) => auxPlayers.failed(detail(e).player, detail(e).message));
  client.addEventListener('ev:notes.changed', (e) => notes.received(detail(e).text));
  client.addEventListener('ev:processor.changed', (e) => processor.render(detail(e).processor));
  client.addEventListener('ev:library.importProgress', (e) => importView.show(detail(e).import));
  client.addEventListener('ev:folders.changed', (e) => folders.update(detail(e).folders));
  // Rotacions (fase 3, pas 8a): l'editor, el desplegable de l'AutoDJ i el planificador
  client.addEventListener('ev:rotations.changed', () => {
    rotationsView.refresh();
    send('rotations.list').then((r) => autoDjView.setRotations(r.rotations)).catch(() => {});
    planner.refresh();
  });
  // Gestor de pistes (fase 3, pas 7a): una pista o les categories han canviat
  client.addEventListener('ev:library.changed', (e) => {
    const trackId = detail(e).trackId ?? null;
    trackManager.libraryChanged(trackId);
    if (trackId == null) library.refresh(); // els filtres de la cerca de l'Emissió (noms i comptes)
  });
  client.addEventListener('ev:meters', (e) => {
    const ev = detail(e);
    meters.set(ev.output);
    processor.setReduction(ev.reductionDb);
    nowPlaying.setPosition(ev.position);
  });

  client.connect();
}

/** En connectar-se (o tornar-s'hi a connectar): tot l'estat actual. */
async function loadState() {
  loadUiSettings();
  try {
    const [queue, autodj, rotations, output] = await Promise.all([
      send('queue.get'), send('autodj.get'), send('rotations.list'), send('output.get'),
    ]);
    showQueue(queue.queue);
    autoDjView.setRotations(rotations.rotations);
    // Les subcategories, per triar la categoria de sweepers (fase 3, pas 5b)
    send('library.filters').then((f) => autoDjView.setCategories(f.categories)).catch(() => {});
    showAutoDj(autodj.autodj);
    showOutput(output.output);
    await Promise.all([historyView.refresh(), refreshNowPlayingStatus()]);
    planner.refresh(); // només si es mostra
    upcoming.refresh();
    eventsPause.refresh();
    library.refresh();
    auxPlayers.refresh();
    notes.refresh();
    processor.refresh();
    trackManager.refresh(); // només si es mostra
    importView.refresh();
    folders.refresh();
    rotationsView.refresh(); // només si es mostra
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
