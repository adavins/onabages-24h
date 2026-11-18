// @ts-check
// Pas 2: obre Ona Bages 24h amb una carpeta de dades de mostra (ONABAGES_DATA_DIR), hi crea les dades de mostra amb les
// ordres del protocol (les mateixes que fa servir la interfície), la deixa sonar un minut perquè hi hagi historial i fa
// les captures de les pantalles (JPEG). En acabar, atura el motor de mostra i tanca la finestra.
//
//   node captures.mjs
//
// Cal que l'aplicació de sempre estigui tancada (el motor fa servir el port 47824) i que s'hagin generat els àudios
// (node genera-audios.mjs). La carpeta «dades» de sempre no es toca. Els àudios són tons fluixos (uns −30 dB) i, a
// més, el volum de sortida es posa a −20 dB: no se sent gairebé res (si es vol, es pot silenciar l'altaveu).
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { APP, AUDIO, DATA, ENGINE_PORT, SHOTS, sleep } from './comu.mjs';

const DEBUG_PORT = 9482;
const PLAY_SECONDS = 60; // quant sona abans de les captures (historial, cua, pròxims esdeveniments)

if (!fs.existsSync(AUDIO)) {
  console.log('Primer: node genera-audios.mjs');
  process.exit(2);
}
// Si ja hi ha un motor al port (l'aplicació de sempre), no es fa res: mai dos motors alhora
try {
  await fetch(`http://127.0.0.1:${ENGINE_PORT}/health`, { signal: AbortSignal.timeout(1000) });
  console.log('Hi ha un motor en marxa: tanca Ona Bages 24h (i atura el motor) i torna-ho a provar.');
  process.exit(2);
} catch { /* lliure */ }

fs.rmSync(DATA, { recursive: true, force: true }); // sempre des de zero
fs.mkdirSync(DATA, { recursive: true });
fs.mkdirSync(SHOTS, { recursive: true });

// Electron amb la carpeta de dades de mostra i el depurador remot (per clicar i fer captures).
// VS Code (que també és Electron) deixa ELECTRON_RUN_AS_NODE posada: s'ha de treure.
const env = { ...process.env, ONABAGES_DATA_DIR: DATA };
delete env.ELECTRON_RUN_AS_NODE;
const electron = spawn(path.join(APP, 'node_modules', 'electron', 'dist', 'electron.exe'),
  [`--remote-debugging-port=${DEBUG_PORT}`, '.'], { cwd: APP, stdio: 'ignore', env });

/** @type {WebSocket | null} */
let engine = null;
let finished = false;
async function finish(/** @type {number} */ code) {
  if (finished) return;
  finished = true;
  // El motor és un procés a part (continua si es tanca la finestra): s'atura amb l'ordre d'aturada
  try { if (engine) await send('engine.shutdown'); } catch { /* ja tancat */ }
  await sleep(1000);
  electron.kill();
  process.exit(code);
}
process.on('unhandledRejection', async (e) => {
  console.log('ERROR:', /** @type {any} */ (e)?.message ?? e);
  await finish(1);
});

// --- El motor de mostra ---
let runtime = null;
for (let i = 0; i < 120 && !runtime; i++) {
  await sleep(1000);
  try {
    const info = JSON.parse(fs.readFileSync(path.join(DATA, 'engine.json'), 'utf8'));
    await fetch(`http://127.0.0.1:${info.port}/health`, { signal: AbortSignal.timeout(1000) });
    runtime = info;
  } catch { /* encara no */ }
}
if (!runtime) { console.log('El motor de mostra no ha arrencat.'); await finish(1); }

engine = new WebSocket(`ws://127.0.0.1:${runtime.port}/ws?token=${encodeURIComponent(runtime.token)}`);
await new Promise((resolve, reject) => { engine?.addEventListener('open', resolve); engine?.addEventListener('error', reject); });
let nextId = 0;
const pending = new Map();
engine.addEventListener('message', (e) => {
  const msg = JSON.parse(String(e.data));
  if (msg.ev || !pending.has(msg.id)) return;
  const { resolve, reject, cmd } = pending.get(msg.id);
  pending.delete(msg.id);
  if (msg.ok) resolve(msg.result);
  else reject(new Error(`${cmd}: ${msg.error?.message ?? 'error'}`));
});
/** @returns {Promise<any>} */
function send(/** @type {string} */ cmd, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject, cmd });
    engine?.send(JSON.stringify({ v: 1, id, cmd, ...params }));
  });
}

// --- Dades de mostra (noms neutres: res de l'emissora ni dels anunciants de debò) ---
console.log('Creant les dades de mostra…');
await send('station.set', { name: 'Ràdio de Mostra', frequency: '100,0 FM' });
await send('processor.set', { volumeDb: -20 });

const category = async (/** @type {string} */ name) => (await send('category.save', { name })).id;
const subcategory = async (/** @type {string} */ name, /** @type {number} */ categoryId) => (await send('subcategory.save', { name, categoryId })).id;
const music = await category('Música');
const pop = await subcategory('Pop', music);
const catala = await subcategory('Català', music);
const falques = await subcategory('Falques', await category('Publicitat'));
const sweepers = await subcategory('Indicatius', await category('Sweepers'));
const programes = await subcategory('Programes', await category('Programes'));

async function importFolder(/** @type {string} */ folder, /** @type {number} */ subcategoryId, /** @type {string} */ type) {
  await send('library.import', { folder: path.join(AUDIO, folder), subcategoryId, type });
  for (let i = 0; i < 300; i++) {
    await sleep(500);
    const status = (await send('library.importStatus')).import;
    if (status && status.state !== 'listing' && status.state !== 'running') {
      if (status.state !== 'done') throw new Error(`Importació de ${folder}: ${status.state}`);
      return;
    }
  }
  throw new Error(`Importació de ${folder}: no s'acaba`);
}
await importFolder('Musica pop', pop, 'music');
await importFolder('Musica catala', catala, 'music');
await importFolder('Falques', falques, 'commercial');
await importFolder('Sweepers', sweepers, 'sweeper');
await importFolder('Programes', programes, 'program');
const track = async (/** @type {string} */ text) => {
  const found = (await send('library.search', { text, limit: 5 })).tracks;
  if (!found?.length) throw new Error(`No es troba la pista «${text}»`);
  return found[0].id;
};

const bagA = (await send('bags.save', { name: 'Generalista A', subcategoryId: pop })).bag.id;
const bagB = (await send('bags.save', { name: 'Generalista B', subcategoryId: catala })).bag.id;
const rotation = (await send('rotations.save', {
  name: 'Mostra',
  steps: [{ bagId: bagA }, { bagId: bagB }, { subcategoryId: pop }, { subcategoryId: catala }],
})).rotation;
await send('autodj.set', { rotationId: rotation.id });

const today = new Date();
const day = (/** @type {number} */ offset) => {
  const d = new Date(today.getFullYear(), today.getMonth(), today.getDate() + offset);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
const ferreteria = (await send('campaigns.save', {
  name: 'Ferreteria Exemple', advertiser: 'Ferreteria Exemple', startDate: day(-5), endDate: day(25), state: 'ACTIVA',
  contractedPerDay: 6, spots: [{ trackId: await track('Falca de tardor') }, { trackId: await track('Falca de Nadal') }],
})).campaign;
const forn = (await send('campaigns.save', {
  name: 'Forn de Mostra', advertiser: 'Forn de Mostra', startDate: day(-2), endDate: day(12), state: 'ACTIVA',
  contractedPerDay: 4, spots: [{ trackId: await track('Forn de Mostra') }],
})).campaign;

const weekdays = 31; // de dilluns a divendres
const save = (/** @type {any} */ event) => send('scheduler.save', { event });
await save({ name: 'El Magazín de Mostra', mode: 'TALLA', startTime: '10:00:00', recurrence: 'SETMANAL', daysMask: weekdays, clearQueue: true,
  actions: [{ type: 'PISTA', params: { songId: await track('Magazín de Mostra') } },
    { type: 'TITOL', params: { text: 'El Magazín de Mostra', durada_seg: 3600 } }] });
await save({ name: 'Informatiu', mode: 'TALLA', startTime: '13:00:00', recurrence: 'SETMANAL', daysMask: weekdays, clearQueue: true,
  actions: [{ type: 'PISTA', params: { songId: await track('Informatiu') } }] });
await save({ name: 'Bloc de falques', mode: 'ENCUA', startTime: '00:30:00', recurrence: 'SETMANAL', daysMask: 127, repeat: 'CADA_HORA',
  commercialBlock: true, actions: [{ type: 'CAMPANYA', params: { campanyaId: ferreteria.id } }, { type: 'CAMPANYA', params: { campanyaId: forn.id } }] });
await save({ name: 'Indicatiu de les hores', mode: 'ENCUA', startTime: '00:00:00', recurrence: 'SETMANAL', daysMask: 127, repeat: 'CADA_HORA',
  actions: [{ type: 'PISTA', params: { songId: await track('Indicatiu 1') } }] });
await save({ name: 'Programa de fora', mode: 'TALLA', startTime: '12:00:00', recurrence: 'SETMANAL', daysMask: 32, clearQueue: true,
  actions: [{ type: 'ENTRADA', params: { dispositiu: 'Línia 2', durada_seg: 3600, titol_butt: 'Programa de fora', reconnecta: true } }] });
await save({ name: 'Canvi de bossa', mode: 'SISTEMA', startTime: '00:00:00', recurrence: 'DIES_MES', monthDays: [1],
  actions: [{ type: 'BOSSA', params: { bossaId: bagA, subcategoriaId: catala } }] });
await send('notes.set', { text: 'Recordatori: divendres hi ha retransmissió especial a les 19 h.' });

console.log(`Sonant ${PLAY_SECONDS} s perquè hi hagi historial…`);
await sleep(PLAY_SECONDS * 1000);

// --- La finestra (Chrome DevTools Protocol) ---
let target;
for (let i = 0; i < 30 && !target; i++) {
  try {
    const list = await (await fetch(`http://127.0.0.1:${DEBUG_PORT}/json`)).json();
    target = list.find((/** @type {any} */ t) => t.type === 'page' && t.url.includes('index.html'));
  } catch { /* encara no */ }
  if (!target) await sleep(1000);
}
if (!target) { console.log('No s\'ha trobat la finestra.'); await finish(1); }

const page = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => page.addEventListener('open', r));
let cdpId = 0;
const cdpWaiting = new Map();
/** @type {string[]} */
const consoleErrors = [];
page.addEventListener('message', (e) => {
  const m = JSON.parse(String(e.data));
  if (m.id && cdpWaiting.has(m.id)) { cdpWaiting.get(m.id)(m); cdpWaiting.delete(m.id); }
  if (m.method === 'Runtime.exceptionThrown') consoleErrors.push(m.params.exceptionDetails?.exception?.description);
});
const cdp = (/** @type {string} */ method, params = {}) =>
  new Promise((r) => { const n = ++cdpId; cdpWaiting.set(n, r); page.send(JSON.stringify({ id: n, method, params })); });
const run = async (/** @type {string} */ expr) =>
  (/** @type {any} */ (await cdp('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }))).result?.result?.value;
await cdp('Runtime.enable');
await cdp('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });

async function shot(/** @type {string} */ name) {
  await sleep(1500); // que s'acabi de pintar (i arribin les dades del motor)
  const r = /** @type {any} */ (await cdp('Page.captureScreenshot', { format: 'jpeg', quality: 82 }));
  fs.writeFileSync(path.join(SHOTS, name + '.jpg'), Buffer.from(r.result.data, 'base64'));
  console.log(`Captura: ${name}`);
}
const click = (/** @type {string} */ selector) => run(`document.querySelector(${JSON.stringify(selector)})?.click(), true`);

await click('#nav-emissio');
await shot('emissio');
await click('#nav-planificador');
await shot('planificador');
await click('#nav-biblioteca');
await click('#sub-biblioteca');
await shot('biblioteca');
await click('#sub-historial');
await shot('historial');
for (const view of ['rotacions', 'campanyes', 'registre']) {
  await click(`[data-view="${view}"]`);
  await shot(view);
}
await click('#nav-emissio');

if (consoleErrors.length) console.log(`Errors a la consola de la interfície:\n  ${consoleErrors.join('\n  ')}`);
console.log(`Fet. Captures a ${SHOTS}: mira-les abans d'incrustar-les.`);
await finish(0);
