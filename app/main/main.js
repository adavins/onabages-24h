'use strict';

// Procés principal d'Electron (fase 1, pas 10b).
// Responsabilitats: obrir la finestra, trobar el motor (o arrencar-lo si no n'hi ha cap) i passar-ne
// el port i el testimoni a la interfície. Aquí NO hi ha àudio ni base de dades: tot és al motor.
// Tancar la finestra no atura el motor: l'emissió continua (D-035).

const { app, BrowserWindow, ipcMain } = require('electron');
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
  mainWindow = new BrowserWindow({
    width: 1600,
    height: 900,
    minWidth: 800,
    minHeight: 500,
    title: 'Ona Bages 24h',
    backgroundColor: '#121417',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
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
