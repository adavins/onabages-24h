// @ts-check
// Pantalla d'emissió (fase 1, passos 10b i 10c). Només mostra l'estat del motor i hi envia ordres: l'àudio,
// la cua i l'AutoDJ són al motor, que continua sonant encara que aquesta finestra es tanqui.

import { EngineClient } from './engine-client.js';
import { formatClock, formatLongDate } from './format.js';
import { $, el, setText } from './ui/dom.js';
import { confirmAction } from './ui/confirm.js';
import { OutputMeters } from './ui/meters.js';
import { NowPlaying } from './ui/now-playing.js';
import { QueueView, HistoryView } from './ui/queue.js';
import { AutoDjView } from './ui/autodj.js';
import { renderOutput, renderNowPlaying } from './ui/statusbar.js';
import { openFixedTitle } from './ui/fixed-title.js';
import { WarningsView } from './ui/warnings.js';
import { LogView } from './ui/log-view.js';
import { todayIso } from './event-form.js';
import { CampaignsView } from './ui/campaigns.js';
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
import { CalendarView } from './ui/calendar.js';
import { setupZoom, applyZoom } from './ui/zoom.js';
import { setupWindowFrame } from './ui/window-frame.js';
import { CollapsibleCards } from './ui/collapsible.js';
import { openSettingsDialog } from './ui/settings-dialog.js';
import { SessionController } from './session.js';
import { openReport } from './ui/report-dialog.js';
import { HistorySearchView } from './ui/history-search.js';

/**
 * @typedef {{
 *   startEngine: () => Promise<{ port: number, token: string }>,
 *   findEngine: () => Promise<{ port: number, token: string } | null>,
 *   onCloseRequested: (callback: () => void) => void,
 *   isDevelopment: () => Promise<boolean>,
 *   closeWindow: () => Promise<void>,
 *   chooseAudioFile: (defaultPath?: string) => Promise<string | null>,
 *   chooseAudioFiles: (defaultPath?: string) => Promise<string[] | null>,
 *   chooseFolder:(defaultPath?: string) => Promise<string | null>,
 *   showFile: (filePath: string) => Promise<'file' | 'folder' | 'missing'>,
 *   chooseImage: () => Promise<{ dataUrl?: string, error?: string } | null>,
 *   saveReportPdf: (html: string, name: string) => Promise<{ path?: string, error?: string } | null>,
 *   saveCsv: (text: string, name: string) => Promise<{ path?: string, error?: string } | null>,
 *   choosePackageSave: (suggestedName: string) => Promise<string | null>,
 *   choosePackageOpen: () => Promise<string | null>,
 *   chooseBackup: () => Promise<string | null>,
 * }} Bridge
 */
const bridge = /** @type {Bridge} */ (/** @type {any} */ (window).onabages);

// ⚙ Configuració (botó de la capçalera; també des d'un informe, per posar les dades de l'emissora): una finestra amb
// pestanyes (dades generals, avisos per correu, còpies de seguretat i eines)
/** @param {import('./ui/settings-dialog.js').SettingsTab} [tab] */
const openStation = (tab) => openSettingsDialog(send, {
  chooseImage: () => bridge.chooseImage(),
  chooseFolder: (path) => bridge.chooseFolder(path),
  chooseBackup: () => bridge.chooseBackup(),
  showFile: (path) => bridge.showFile(path),
  processor,
}, session, tab);
/**
 * @param {'programming' | 'certificate'} kind
 * @param {{ campaignId: number } | { trackId: number }} target
 * @param {{ name: string, startDate?: string, endDate?: string, validFrom?: string | null, validUntil?: string | null }} subject
 */
const showReport = (kind, target, subject) => openReport({
  send, savePdf: (html, name) => bridge.saveReportPdf(html, name), openStation, kind, target, subject,
});

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
  el.title = s.text; // només la icona (D-168): el text, en passar-hi el ratolí i per als lectors de pantalla
  el.innerHTML = '';
  const icon = document.createElement('i');
  icon.className = `bi ${s.icon}`;
  icon.setAttribute('aria-hidden', 'true');
  const label = document.createElement('span');
  label.className = 'visually-hidden';
  label.textContent = s.text;
  el.append(icon, label);
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

/**
 * Envia una ordre al motor, sense res més (la sessió la fa servir per a les seves ordres).
 * @param {string} cmd
 * @param {object} [params]
 * @param {number} [timeoutMs]  per a les ordres que poden tardar (p. ex. el correu de prova); si no, el temps normal
 */
function request(cmd, params = {}, timeoutMs = undefined) {
  if (!client) return Promise.reject(new Error('El motor no està connectat'));
  return client.request(cmd, params, timeoutMs);
}

// Usuaris i sessió (fase 5, pas 8b; D-164): si una ordre necessita sessió just després d'un clic, es demana i es torna a enviar
const session = new SessionController(request);

/**
 * Envia una ordre al motor (totes les de la interfície passen per aquí).
 * @param {string} cmd
 * @param {object} [params]
 * @param {number} [timeoutMs]
 */
function send(cmd, params = {}, timeoutMs = undefined) {
  return session.send(cmd, params, timeoutMs);
}

// El botó de la sessió, a la capçalera (sempre color + icona + text)
session.onChange((state) => {
  const button = $('btn-session');
  let iconName, text, className, title;
  if (!state.usersExist) {
    [iconName, text, className] = ['bi-unlock', 'Sense usuaris', 'text-warning'];
    title = 'No hi ha cap usuari: tothom pot canviar-ho tot. Crea un administrador a ⚙ Configuració → Usuaris.';
  } else if (!state.session) {
    [iconName, text, className] = ['bi-lock-fill', 'Consulta', 'text-info'];
    title = 'Mode de consulta: es veu tot, però per canviar res cal iniciar sessió. L\'emissió continua igualment.';
  } else {
    [iconName, text, className] = ['bi-person-check-fill', `${state.session.displayName} · ${state.session.roleName}`, 'text-success'];
    title = 'Sessió oberta: canviar la contrasenya o tancar-la';
  }
  const i = document.createElement('i');
  i.className = `bi ${iconName}`;
  i.setAttribute('aria-hidden', 'true');
  const label = document.createElement('span');
  label.className = 'visually-hidden'; // només la icona (D-168): el text, en passar-hi el ratolí
  label.textContent = text;
  button.replaceChildren(i, label);
  button.className = `btn btn-sm btn-link topbar-session ${className}`;
  button.title = `${text}\n${title}`;
  // El registre: amb un altre rol hi surten (o no) els canvis i les sessions
  logView.refresh();
  // Les notes es desen soles mentre s'escriu: sense permís, només es poden llegir
  /** @type {HTMLTextAreaElement} */ ($('notes-text')).readOnly = !session.can('operator');
});
$('btn-session').addEventListener('click', () => {
  if (!session.state.usersExist) openStation('users');
  else if (!session.state.session) session.login();
  else session.menu();
});

const meters = new OutputMeters($('meter-left'), $('meter-right'));
// Fent clic a la barra de progrés, la pista va a aquell punt (D-103). No interromp l'emissió: no demana confirmació
const nowPlaying = new NowPlaying(async (itemId, positionMs) => {
  try {
    await send('player.seek', { itemId, positionMs });
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut anar a aquell punt', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
  }
});
const queueView = new QueueView(send);
const historyView = new HistoryView(send);
const autoDjView = new AutoDjView(send);
// Des de l'acció «Campanya» de l'editor d'esdeveniments: la campanya a la pestanya Campanyes (D-137)
const planner = new PlannerView(send, (campaignId) => {
  showView('campanyes');
  campaignsView.openById(campaignId);
});
/** L'última cua rebuda: la previsió es torna a calcular quan canvien els pròxims esdeveniments. @type {any} */
let lastQueue = { current: null, currentEndsAt: null, upcoming: [] };
// Un clic a un dels pròxims esdeveniments l'obre al Planificador (D-137)
const upcoming = new UpcomingEvents(send, () => queueView.render(lastQueue, upcoming.events), (eventId, date) => {
  showView('planificador');
  planner.openEvent(eventId, date);
});
const eventsPause = new EventsPause(send);
const library = new LibraryView(send);
queueView.acceptDrops(TRACK_DRAG_TYPE, (track, position) => library.add(track, position));
const auxPlayers = new AuxPlayersView(send, TRACK_DRAG_TYPE);
const notes = new NotesView(send);
const processor = new ProcessorView(send, () => openStation('processor'));
const folders = new FoldersView(send, (path) => bridge.chooseFolder(path));
const rotationsView = new RotationsView(send);
// El calendari d'una campanya (fase 4, pas 3d) és part de la pestanya Campanyes: «Calendari…» a l'editor l'obre i
// «Torna a la campanya» hi torna, tal com era (amb el que diu on sona al dia)
const campaignCalendar = new CalendarView(send, () => {
  showView('campanyes');
  campaignsView.refresh();
}, { boxId: 'cpcal-editor', backText: 'Torna a la campanya' });
const campaignsView = new CampaignsView(send, (campaign) => {
  showView('calendari-campanya');
  campaignCalendar.openCampaign(campaign);
}, (kind, c) => showReport(kind, { campaignId: c.id }, { name: c.name, startDate: c.startDate, endDate: c.endDate }));
// El calendari d'una pista és part de la pestanya Pistes (D-113): «Torna a les pistes» hi torna, tal com era
const calendarView = new CalendarView(send, () => showView('biblioteca'));
// «Calendari…» a la fitxa d'una pista (D-111): obre la pestanya Calendari amb aquella pista
const trackManager = new TrackManagerView(send, (path) => bridge.chooseAudioFile(path), folders, (track) => {
  showView('calendari');
  calendarView.openTrack(track);
}, (path) => bridge.showFile(path), (kind, t) => showReport(kind, { trackId: t.id },
  { name: t.artist ? `${t.artist} - ${t.title}` : t.title, validFrom: t.validFrom, validUntil: t.validUntil }), (t) => {
  // «Historial…» a la fitxa: tot el que ha sonat d'aquesta pista (D-143)
  showView('historial');
  historySearch.openTrack({ id: t.id, name: t.artist ? `${t.artist} - ${t.title}` : t.title });
});
new RerootView(send, (path) => bridge.chooseFolder(path));
// La pestanya Historial (fase 4, pas 5b; D-143): un clic a una fila obre la fitxa de la pista a la pestanya Pistes
const historySearch = new HistorySearchView(send, (trackId) => {
  showView('biblioteca');
  trackManager.openTrack(trackId);
}, { savePdf: (html, name) => bridge.saveReportPdf(html, name), saveCsv: (text, name) => bridge.saveCsv(text, name) });
// Avisos (fase 4, pas 6b; D-146): tots junts a la barra de sota, amb una finestra per gravetat; els de vigència, també
// a la fitxa de la pista i a la campanya
// Registre (D-167): tot el que ha passat, en una pestanya
const logView = new LogView(send);
const warnings = new WarningsView(send, {
  openLog: () => {
    showView('registre');
    logView.showOpenIncidents();
  },
  openTrack: (id) => {
    showView('biblioteca');
    trackManager.openTrack(id);
  },
  openCampaign: (id) => {
    showView('campanyes');
    campaignsView.openById(id);
  },
  openEvent: (id) => {
    showView('planificador');
    planner.openEvent(id, todayIso());
  },
});
trackManager.validityBox = (subject, id) => warnings.box(subject, id);
campaignsView.validityBox = (subject, id) => warnings.box(subject, id);
// Paquet de campanya (D-150): els diàlegs de Windows per triar on es desa i quin s'obre
campaignsView.packageFiles = {
  chooseSave: (name) => bridge.choosePackageSave(name),
  chooseOpen: () => bridge.choosePackageOpen(),
};
// El requadre «Programació» de la fitxa (D-148): un clic a un esdeveniment l'obre al Planificador
trackManager.openEvent = (id) => {
  showView('planificador');
  planner.openEvent(id, todayIso());
};
const importView = new ImportView(send, (path) => bridge.chooseFolder(path), (path) => bridge.chooseAudioFiles(path),
  () => trackManager.importContext(), (id) => trackManager.openTrack(id));

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

/** @param {any} queue  docs/protocol.md, 4.2 */
function showQueue(queue) {
  nowPlaying.setCurrent(queue.current);
  nowPlaying.setPaused(queue.paused ?? null);
  nowPlaying.setStream(queue.stream ?? null);
  lastQueue = queue;
  queueView.render(queue, upcoming.events);
  /** @type {HTMLButtonElement} */ ($('btn-skip')).disabled = !queue.current;
  // Pausa (D-103): el botó passa a ser «Continua» mentre dura
  const pause = /** @type {HTMLButtonElement} */ ($('btn-pause'));
  pause.disabled = !queue.current;
  const paused = !!queue.paused;
  pause.classList.toggle('btn-danger', paused);
  pause.classList.toggle('btn-outline-light', !paused);
  pause.title = paused ? 'Continua la pista des d\'on s\'havia aturat' : 'Atura la pista que sona (la ràdio queda en silenci fins que continuï)';
  const icon = document.createElement('i');
  icon.className = paused ? 'bi bi-play-fill' : 'bi bi-pause-fill';
  icon.setAttribute('aria-hidden', 'true');
  pause.replaceChildren(icon, el('span', '', paused ? 'Continua' : 'Pausa'));
}

/** @param {any} state */
function showAutoDj(state) {
  autoDjView.render(state);
  rotationsView.setActive(state.rotation?.id ?? null);
}

/** @param {any} output */
function showOutput(output) {
  renderOutput(output);
}

/** L'últim estat del títol per a BUTT (per obrir el diàleg del títol fix amb el que hi ha). @type {any} */
let lastNowPlaying = null;

async function refreshNowPlayingStatus() {
  try {
    const nowPlaying = (await send('nowplaying.get')).nowPlaying;
    lastNowPlaying = nowPlaying;
    renderNowPlaying(nowPlaying);
  } catch { /* sense connexió: ja ho diu la capçalera */ }
}

// Títol fix per a BUTT (D-184): des de la barra d'estat
$('status-butt').addEventListener('click', async () => {
  const changed = await openFixedTitle(lastNowPlaying, send);
  if (!changed) return;
  lastNowPlaying = changed;
  renderNowPlaying(changed);
});

// --- Pantalles: Emissió i Planificador (fase 2, pas 8a). L'emissió continua igual, es mostri la que es mostri. ---

/** A quina pestanya de dalt pertany cada pantalla (D-168): Pistes té la biblioteca i l'historial; «Més», la resta. */
const TOP_TAB = /** @type {const} */ ({
  emissio: 'emissio', planificador: 'planificador', biblioteca: 'biblioteca', historial: 'biblioteca', calendari: 'biblioteca',
  rotacions: 'mes', campanyes: 'mes', 'calendari-campanya': 'mes', registre: 'mes',
});
/** El nom i la icona del botó «Més» mentre es veu una de les seves pantalles. */
const MORE_LABELS = /** @type {Record<string, [string, string]>} */ ({
  rotacions: ['Rotacions', 'bi-arrow-repeat'], campanyes: ['Campanyes', 'bi-megaphone'],
  'calendari-campanya': ['Campanyes', 'bi-megaphone'], registre: ['Registre', 'bi-journal-text'],
});

/** @param {keyof typeof TOP_TAB} name */
function showView(name) {
  for (const view of /** @type {(keyof typeof TOP_TAB)[]} */ (Object.keys(TOP_TAB))) {
    $(`view-${view}`).classList.toggle('d-none', view !== name);
  }
  // Les pestanyes de dalt (els calendaris no en tenen: són part de Pistes, D-113, i de Campanyes, pas 3d)
  for (const tab of ['emissio', 'planificador', 'biblioteca', 'mes']) {
    const current = TOP_TAB[name] === tab;
    $(`nav-${tab}`).classList.toggle('active', current);
    if (current) $(`nav-${tab}`).setAttribute('aria-current', 'page');
    else $(`nav-${tab}`).removeAttribute('aria-current');
  }
  const [moreLabel, moreIcon] = MORE_LABELS[name] ?? ['Més', 'bi-three-dots'];
  $('nav-mes-label').textContent = moreLabel;
  $('nav-mes-icon').className = `bi ${moreIcon}`;
  // Pistes: les subpestanyes Biblioteca i Historial
  $('subnav-pistes').classList.toggle('d-none', TOP_TAB[name] !== 'biblioteca');
  $('sub-biblioteca').classList.toggle('active', name !== 'historial');
  $('sub-historial').classList.toggle('active', name === 'historial');
  if (name === 'planificador') planner.show();
  else planner.hide();
  if (name === 'biblioteca') trackManager.show();
  else trackManager.hide();
  if (name === 'rotacions') rotationsView.show();
  else rotationsView.hide();
  if (name === 'campanyes') campaignsView.show();
  else campaignsView.hide();
  if (name === 'calendari') calendarView.show();
  else calendarView.hide();
  if (name === 'calendari-campanya') campaignCalendar.show();
  else campaignCalendar.hide();
  if (name === 'historial') historySearch.show();
  else historySearch.hide();
  if (name === 'registre') logView.show();
  else logView.hide();
}
$('nav-emissio').addEventListener('click', () => showView('emissio'));
$('nav-planificador').addEventListener('click', () => showView('planificador'));
$('nav-biblioteca').addEventListener('click', () => showView('biblioteca'));
$('sub-biblioteca').addEventListener('click', () => showView('biblioteca'));
$('sub-historial').addEventListener('click', () => showView('historial'));

// El menú «Més» (D-168): s'obre i es tanca amb el botó; es tanca en triar, en fer clic a fora o amb Esc
const moreMenu = $('menu-mes');
/** @param {boolean} open */
const setMoreOpen = (open) => {
  moreMenu.classList.toggle('d-none', !open);
  $('nav-mes').setAttribute('aria-expanded', String(open));
  if (open) /** @type {HTMLElement | null} */ (moreMenu.querySelector('.more-item'))?.focus();
};
$('nav-mes').addEventListener('click', () => setMoreOpen(moreMenu.classList.contains('d-none')));
moreMenu.addEventListener('click', (e) => {
  const item = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('.more-item'));
  if (!item) return;
  setMoreOpen(false);
  if (item.dataset.view) showView(/** @type {keyof typeof TOP_TAB} */ (item.dataset.view));
});
$('menu-settings').addEventListener('click', () => openStation());
document.addEventListener('pointerdown', (e) => {
  if (!moreMenu.classList.contains('d-none') && !/** @type {HTMLElement} */ (e.target).closest('.more-wrap')) setMoreOpen(false);
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !moreMenu.classList.contains('d-none')) {
    setMoreOpen(false);
    $('nav-mes').focus();
  }
});
$('btn-history-all').addEventListener('click', (ev) => {
  ev.stopPropagation(); // no plega el requadre
  showView('historial');
});
// ⚙ Configuració és al menú «Més» (D-168): l'administrador hi veu totes les pestanyes; l'operador i el programador, només
// la sortida d'àudio. Sense sessió, el clic la demana (session.js, data-needs)

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

// Pausa (D-103): deixa la ràdio en silenci, per tant demana confirmació; continuar, no
$('btn-pause').addEventListener('click', async () => {
  if (lastQueue?.paused) {
    try {
      await send('player.resume');
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut continuar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
    return;
  }
  const title = $('np-title').textContent;
  const ok = await confirmAction({
    title: 'Fer pausa',
    message: `«${title}» s'aturarà i la ràdio quedarà en silenci fins que premis «Continua». ` +
      'Si en 5 minuts ningú no ho fa, continuarà sola. Un esdeveniment TALLA sonarà igualment a la seva hora.',
    confirm: 'Fes pausa',
  });
  if (!ok) return;
  try {
    await send('player.pause');
  } catch (err) {
    await confirmAction({ title: 'No s\'ha pogut fer pausa', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
  }
});

// Triar la sortida d'àudio, en clicar-la a la barra de sota (D-120)
$('status-output').addEventListener('click', () => openStation('output'));

// En clicar la X (D-054, D-162): es tanca la finestra i l'emissió continua. El motor no s'atura mai des de la interfície
// de l'emissora; només en desenvolupament hi ha «Atura el motor i tanca» (i, sense connexió amb el motor, no es pot triar).
bridge.onCloseRequested(async () => {
  const dialog = /** @type {HTMLDialogElement} */ ($('close-dialog'));
  if (dialog.open) return;
  const development = await bridge.isDevelopment().catch(() => false);
  const connected = client?.connected ?? false;
  $('close-dev').classList.toggle('d-none', !development);
  $('close-stop').classList.toggle('d-none', !development);
  /** @type {HTMLButtonElement} */ ($('close-stop')).disabled = !connected;
  $('close-offline').classList.toggle('d-none', !development || connected);
  // Una importació en curs (fase 3, pas 7b): la fa el motor, no la finestra
  const importing = connected ? importView.runningText() : null;
  $('close-import').classList.toggle('d-none', !importing);
  $('close-import-text').textContent = importing
    ? `${importing}. Si tanques la finestra, la importació continua.` + (development
      ? ' Si atures el motor, s\'aturarà a mig fer: el que ja s\'ha importat es queda, i la pots tornar a fer després (les pistes que ja hi són se salten).'
      : '')
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
      await session.connected(); // abans de res: la sessió d'abans, si n'hi havia
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
      warnings.clear();
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
    historySearch.refresh();
    setTimeout(refreshNowPlayingStatus, 500); // el fitxer s'escriu en un fil a part
  });
  client.addEventListener('ev:player.trackEnded', () => {
    historyView.refresh();
    historySearch.refresh();
  });
  client.addEventListener('ev:scheduler.changed', () => {
    warnings.refresh();
    trackManager.eventsChanged(); // el requadre «Programació» de la fitxa oberta (D-148)
    planner.refresh();
    upcoming.refresh();
    calendarView.refresh(); // només si es mostra (p. ex. s'ha canviat un esdeveniment al Planificador)
    campaignCalendar.refresh();
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
  // Incidències (fase 5, pas 1b; D-156): la barra d'avisos i, si és oberta, la finestra «Incidències»
  // La sessió s'ha tancat per inactivitat (D-164)
  client.addEventListener('ev:session.changed', (e) => session.changed(detail(e)));
  client.addEventListener('ev:incidents.changed', () => {
    warnings.refresh();
    logView.refresh();
  });
  // Rotacions (fase 3, pas 8a): l'editor, el desplegable de l'AutoDJ i el planificador
  client.addEventListener('ev:rotations.changed', () => {
    rotationsView.refresh();
    send('rotations.list').then((r) => autoDjView.setRotations(r.rotations)).catch(() => {});
    planner.refresh();
  });
  // Agenda de bosses (D-170): les bosses de Rotacions en marquen l'agenda (el canvi de les bosses arriba amb rotations.changed)
  client.addEventListener('ev:agendas.changed', () => rotationsView.refresh());
  // Campanyes (fase 4, pas 3c): la pantalla i el planificador (l'acció «Campanya» en diu el nom i l'estat)
  client.addEventListener('ev:campaigns.changed', () => {
    warnings.refresh();
    campaignsView.refresh();
    campaignCalendar.refresh(); // el nom, l'estat o les dates de la campanya oberta
    planner.refresh();
  });
  // Gestor de pistes (fase 3, pas 7a): una pista o les categories han canviat
  client.addEventListener('ev:library.changed', (e) => {
    const trackId = detail(e).trackId ?? null;
    warnings.refresh();
    trackManager.libraryChanged(trackId);
    calendarView.refresh(); // el títol o les dates de la pista triada poden haver canviat
    campaignCalendar.refresh(); // la durada de les falques (hores previstes del contingut)
    campaignsView.refresh(); // els avisos de les falques (fitxer, dates…)
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
    campaignsView.refresh(); // només si es mostra
    warnings.refresh();
  } catch (err) {
    console.error('No s\'ha pogut llegir l\'estat del motor:', err);
  }
}

// Els avisos de vigència no tenen esdeveniment propi (D-146): cada hora, per al canvi de dia
setInterval(() => {
  if (client?.connected) warnings.refresh();
}, 3_600_000);

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
