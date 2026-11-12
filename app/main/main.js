'use strict';

// Procés principal d'Electron (fase 1, pas 10b).
// Responsabilitats: obrir la finestra, trobar el motor (o arrencar-lo si no n'hi ha cap) i passar-ne
// el port i el testimoni a la interfície. Aquí NO hi ha àudio ni base de dades: tot és al motor.
// Tancar la finestra no atura el motor si l'operador no ho tria al diàleg de tancar (D-035, D-054).

const { app, BrowserWindow, ipcMain, screen, dialog } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

const DEFAULT_PORT = 47824;
const ENGINE_START_TIMEOUT_MS = 60_000; // «dotnet run» pot trigar la primera vegada
const REPO_ROOT = path.resolve(__dirname, '..', '..');

// Carpeta de dades (D-044): la carpeta «dades» de l'aplicació. En desenvolupament, la del repositori;
// empaquetada, la del costat de l'executable. Res a %APPDATA%: també la memòria cau d'Electron va aquí.
const DATA_DIR = app.isPackaged
  ? path.join(path.dirname(process.execPath), 'dades')
  : path.join(REPO_ROOT, 'dades');
app.setPath('userData', path.join(DATA_DIR, 'electron')); // abans de res més
const RUNTIME_FILE = path.join(DATA_DIR, 'engine.json');

// Ha de funcionar bé per escriptori remot: res que depengui de la targeta gràfica.
app.disableHardwareAcceleration();

/** @type {BrowserWindow | null} */
let mainWindow = null;
/** La interfície ja ha dit que es pot tancar (després del diàleg de tancar). */
let allowClose = false;
let rendererUnresponsive = false;

// Només una finestra de l'aplicació alhora.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });
  app.whenReady().then(createWindow);
}

app.on('window-all-closed', () => app.quit());

function createWindow() {
  // On era la finestra l'última vegada (o la mida per defecte).
  const { bounds, maximized } = savedWindowBounds({ width: 1600, height: 900, minWidth: 800, minHeight: 500 });
  mainWindow = new BrowserWindow({
    ...bounds,
    show: false,
    title: 'Ona Bages 24h',
    // Icona de la finestra i de la barra de tasques: el mateix logotip de la capçalera. Si se substitueix el fitxer,
    // es veu en tornar a obrir l'aplicació. La de l'executable (.ico) la crea empaquetar.cmd a partir d'aquest mateix PNG.
    icon: path.join(__dirname, '..', 'renderer', 'img', 'logo.png'),
    backgroundColor: '#121417',
    autoHideMenuBar: true,
    // Sense la barra de títol de Windows (com a Ona Bages Play): la capçalera de l'aplicació en fa la feina
    // (s'hi pot arrossegar la finestra) i els botons de minimitzar, maximitzar i tancar es dibuixen a sobre, a la dreta.
    titleBarStyle: 'hidden',
    titleBarOverlay: { ...TITLE_BAR_COLORS, height: titleBarHeight() },
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  if (maximized) mainWindow.maximize();
  mainWindow.show();
  // Sense el menú d'Electron: les seves dreceres de zoom passarien per davant de les de l'aplicació (js/ui/zoom.js).
  mainWindow.removeMenu();
  mainWindow.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));

  // F12 obre les eines de desenvolupament (només per desenvolupar), i F5 torna a carregar la interfície
  // (el motor no se n'assabenta: l'emissió continua).
  mainWindow.webContents.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown') return;
    if (input.key === 'F12' && !app.isPackaged) {
      mainWindow?.webContents.toggleDevTools();
      event.preventDefault();
    } else if (input.key === 'F5') {
      mainWindow?.webContents.reload();
      event.preventDefault();
    }
  });

  // En clicar la X, la interfície pregunta si es tanca només la finestra o també s'atura l'emissió (D-054).
  // Si la interfície està penjada o s'ha tancat, no pot preguntar: es tanca només la finestra.
  mainWindow.on('close', (event) => {
    if (allowClose || rendererUnresponsive || mainWindow?.webContents.isCrashed()) {
      if (mainWindow) saveWindowBounds(mainWindow);
      return;
    }
    event.preventDefault();
    mainWindow?.webContents.send('app:close-requested');
  });
  mainWindow.on('unresponsive', () => { rendererUnresponsive = true; });
  mainWindow.on('responsive', () => { rendererUnresponsive = false; });

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

// ---------------------------------------------------------------------------
// Motor
// ---------------------------------------------------------------------------

/** Llegeix el fitxer d'execució que escriu el motor quan arrenca (port i testimoni). */
function readRuntimeFile() {
  try {
    const data = JSON.parse(fs.readFileSync(RUNTIME_FILE, 'utf8'));
    if (data && typeof data.port === 'number' && typeof data.token === 'string') {
      return { port: data.port, token: data.token };
    }
  } catch {
    // No existeix o no es pot llegir: el motor no està en marxa.
  }
  return null;
}

/**
 * Hi ha un motor responent en aquest port? Qualsevol resposta de /health compta: un 503 vol dir que el motor
 * funciona però no envia àudio (p. ex. sense cap sortida), i la interfície s'hi ha de connectar per mostrar-ho.
 */
async function isEngineAlive(port) {
  try {
    await fetch(`http://127.0.0.1:${port}/health`, { signal: AbortSignal.timeout(1000) });
    return true;
  } catch {
    return false;
  }
}

/** Com s'arrenca el motor: l'executable compilat o, si no n'hi ha, «dotnet run». */
function engineCommand(port, token) {
  const engineArgs = ['--port', String(port), '--token', token, '--data-dir', DATA_DIR];
  const exeName = 'OnaBages24.Engine.exe';
  const candidates = [
    // Aplicació empaquetada (fase 6): el motor publicat, al costat.
    path.join(process.resourcesPath || '', 'engine', exeName),
    // Desenvolupament: després de «dotnet build».
    path.join(REPO_ROOT, 'engine', 'OnaBages24.Engine', 'bin', 'Debug', 'net10.0-windows', exeName),
  ];
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return { command: candidate, args: engineArgs };
  }
  return {
    command: 'dotnet',
    args: ['run', '--project', path.join(REPO_ROOT, 'engine', 'OnaBages24.Engine'), '--', ...engineArgs],
  };
}

/** El motor que ja funciona (segons engine.json), o null. No n'arrenca cap. */
async function findEngine() {
  const existing = readRuntimeFile();
  if (existing && (await isEngineAlive(existing.port))) return existing;
  return null;
}

/**
 * Retorna el port i el testimoni del motor. Si no n'hi ha cap en marxa, l'arrenca com a procés independent:
 * continua funcionant encara que la interfície es tanqui o es pengi.
 */
async function startEngine() {
  const existing = await findEngine();
  if (existing) return existing;

  const port = DEFAULT_PORT;
  const token = crypto.randomBytes(24).toString('hex');
  const { command, args } = engineCommand(port, token);

  let spawnError = null;
  const child = spawn(command, args, {
    cwd: app.isPackaged ? path.dirname(command) : REPO_ROOT,
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
  });
  child.on('error', (err) => {
    spawnError = err;
  });
  child.unref();

  const deadline = Date.now() + ENGINE_START_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (spawnError) throw new Error(`No s'ha pogut arrencar el motor (${command}): ${spawnError.message}`);
    if (await isEngineAlive(port)) return { port, token };
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`El motor no ha respost en ${ENGINE_START_TIMEOUT_MS / 1000} s. Prova d'executar «dotnet build» a la carpeta engine.`);
}

// ---------------------------------------------------------------------------
// Comunicació amb la interfície (via preload.js)
// ---------------------------------------------------------------------------

// En obrir: el motor en marxa o, si no n'hi ha cap, un de nou (l'emissió ha de començar sola).
ipcMain.handle('engine:start', () => startEngine());

// Després de perdre la connexió: només es busca (el motor pot haver tornat a arrencar amb un altre testimoni).
// No se n'arrenca cap sense que l'operador ho demani.
ipcMain.handle('engine:find', () => findEngine());

// La interfície ja ha preguntat a l'operador (i, si calia, ha aturat el motor): es tanca la finestra.
ipcMain.handle('app:close', () => {
  allowClose = true;
  mainWindow?.close();
});

// Gestor de pistes (fase 3, pas 7a): triar un fitxer d'àudio d'aquest ordinador (el motor comprova que existeix i es pot llegir).
// Les extensions són les que accepta el motor (FolderImporter.AudioExtensions).
ipcMain.handle('dialog:choose-audio-file', async (_event, defaultPath) => {
  if (!mainWindow) return null;
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Tria el fitxer d\'àudio',
    defaultPath: typeof defaultPath === 'string' && defaultPath ? defaultPath : undefined,
    properties: ['openFile'],
    filters: [
      { name: 'Àudio', extensions: ['mp3', 'wav', 'flac', 'm4a', 'mp4', 'aac', 'ogg', 'wma'] },
      { name: 'Tots els fitxers', extensions: ['*'] },
    ],
  });
  return result.canceled || result.filePaths.length === 0 ? null : result.filePaths[0];
});

// ---------------------------------------------------------------------------
// Zoom i capçalera com a barra de títol
// ---------------------------------------------------------------------------

/** Zoom d'Electron (de 0,7 a 1,5). Per sota de 0,7 la interfície no fa més petita la lletra (js/ui/zoom.js). */
ipcMain.handle('ui:set-zoom', (_event, factor) => {
  if (typeof factor === 'number' && factor >= 0.7 && factor <= 1.5) {
    mainWindow?.webContents.setZoomFactor(factor);
    updateTitleBarOverlay(); // la capçalera ha canviat d'alçada en píxels de pantalla
  }
});

/** Colors dels botons de la finestra: el fons de la capçalera (--ob-bg) i el text. */
const TITLE_BAR_COLORS = { color: '#121417', symbolColor: '#dee2e6' };

/** Alçada de la capçalera en píxels CSS (la diu la interfície; fins llavors, una aproximació). */
let topbarCssHeight = 48;

/** Alçada dels botons de la finestra: la de la capçalera, en píxels de pantalla (amb el zoom). */
function titleBarHeight() {
  const zoom = mainWindow?.webContents.getZoomFactor() ?? 1;
  // Arrodonit avall: mai més alts que la capçalera, perquè no tapin la vora de sota.
  return Math.max(24, Math.floor(topbarCssHeight * zoom));
}

function updateTitleBarOverlay() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.setTitleBarOverlay({ ...TITLE_BAR_COLORS, height: titleBarHeight() });
}

/** La interfície diu l'alçada de la capçalera (canvia amb el zoom i amb l'amplada de la finestra). */
ipcMain.on('ui:topbar-height', (_event, cssHeight) => {
  if (typeof cssHeight !== 'number' || !(cssHeight > 0 && cssHeight < 400)) return;
  topbarCssHeight = cssHeight;
  updateTitleBarOverlay();
});

// ---------------------------------------------------------------------------
// Posició i mida de la finestra
// ---------------------------------------------------------------------------
// Es desen a «finestra.json», a la carpeta d'Electron (dades\electron). No és configuració de l'aplicació
// (que és al motor): és només on era la finestra en aquest ordinador. Si el fitxer falta o està malmès,
// la finestra surt a la mida per defecte.

function windowFile() {
  return path.join(app.getPath('userData'), 'finestra.json');
}

/**
 * Mida i posició desades, si encara es veuen en alguna pantalla (potser s'ha desconnectat un monitor);
 * si no, les per defecte.
 * @param {{ width: number, height: number, minWidth: number, minHeight: number }} defaults
 */
function savedWindowBounds(defaults) {
  try {
    const saved = JSON.parse(fs.readFileSync(windowFile(), 'utf8'));
    const valid = saved && ['x', 'y', 'width', 'height'].every((k) => Number.isFinite(saved[k]));
    if (valid && isOnSomeDisplay(saved)) {
      return {
        bounds: {
          ...defaults,
          x: Math.round(saved.x),
          y: Math.round(saved.y),
          width: Math.max(Math.round(saved.width), defaults.minWidth),
          height: Math.max(Math.round(saved.height), defaults.minHeight),
        },
        maximized: saved.maximized === true,
      };
    }
  } catch { /* encara no n'hi ha, o està malmès */ }
  return { bounds: { ...defaults }, maximized: false };
}

/** La part de dalt de la finestra (almenys 100 × 30 píxels) es veu en alguna pantalla: així sempre es pot moure. */
function isOnSomeDisplay(/** @type {{ x: number, y: number, width: number }} */ b) {
  return screen.getAllDisplays().some(({ workArea: a }) => {
    const width = Math.min(b.x + b.width, a.x + a.width) - Math.max(b.x, a.x);
    const height = Math.min(b.y + 30, a.y + a.height) - Math.max(b.y, a.y);
    return width >= 100 && height >= 30;
  });
}

/** Desa on és la finestra (si està maximitzada, la mida que tenia abans, i que ho estava). */
function saveWindowBounds(/** @type {BrowserWindow} */ win) {
  try {
    const state = { ...win.getNormalBounds(), maximized: win.isMaximized() };
    const file = windowFile();
    fs.writeFileSync(`${file}.tmp`, JSON.stringify(state, null, 2), 'utf8');
    fs.renameSync(`${file}.tmp`, file); // escriptura atòmica
  } catch (err) {
    console.warn('No s\'ha pogut desar la posició de la finestra', err);
  }
}
