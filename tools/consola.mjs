// Consola de desenvolupament d'Ona Bages 24h: un menú per arrencar, aturar, compilar, provar, empaquetar i diagnosticar
// el motor. S'obre amb consola.cmd (a l'arrel). Només JavaScript de Node 22 o posterior, sense paquets.
//
// Les opcions de diagnòstic només fan consultes al motor, excepte «Enviar una ordre», que envia el que s'hi escriu.

import { createInterface } from 'node:readline/promises';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const ROOT = join(import.meta.dirname, '..');
const DATA = join(ROOT, 'dades');
const ENGINE_EXE = join(ROOT, 'engine', 'OnaBages24.Engine', 'bin', 'Debug', 'net10.0-windows', 'OnaBages24.Engine.exe');

// --- Colors de la consola (sempre amb text: el color només ajuda) ---

const color = {
  green: (s) => `\x1b[32m${s}\x1b[0m`,
  red: (s) => `\x1b[31m${s}\x1b[0m`,
  yellow: (s) => `\x1b[33m${s}\x1b[0m`,
  cyan: (s) => `\x1b[36m${s}\x1b[0m`,
  dim: (s) => `\x1b[2m${s}\x1b[0m`,
  bold: (s) => `\x1b[1m${s}\x1b[0m`,
};

// --- Preguntes ---

let pipedReader = null;
const pipedLines = [];

/** Fa una pregunta i retorna la resposta. Cada pregunta té el seu lector: així els programes que s'executen entremig
 *  (dotnet, npm) tenen el teclat i Ctrl+C sencers. */
async function ask(question) {
  // Si l'entrada no és el teclat (p. ex. respostes per un tub, per provar la consola), un sol lector per a tot
  if (!process.stdin.isTTY) {
    if (!pipedReader) {
      pipedReader = createInterface({ input: process.stdin });
      pipedReader.on('line', (line) => pipedLines.push(line));
      pipedReader.on('close', () => pipedLines.push(null));
    }
    process.stdout.write(question);
    while (pipedLines.length === 0) await new Promise((resolve) => setTimeout(resolve, 10));
    const line = pipedLines.shift();
    if (line === null) process.exit(0); // s'han acabat les respostes
    console.log(line);
    return line.trim();
  }
  const reader = createInterface({ input: process.stdin, output: process.stdout });
  try {
    return (await reader.question(question)).trim();
  } finally {
    reader.close();
  }
}

/** Demana confirmació: només «s» (o «si», «sí») és que sí. */
async function confirm(question) {
  const answer = (await ask(`${question} (s/N) `)).toLowerCase();
  return answer === 's' || answer === 'si' || answer === 'sí';
}

async function pause() {
  await ask(color.dim('\nPrem Retorn per tornar al menú...'));
}

// --- El motor: si funciona i com s'hi parla ---

/** El fitxer d'execució del motor (port, testimoni i PID), o null si no n'hi ha o el procés ja no existeix. */
function runtimeInfo() {
  let info;
  try {
    info = JSON.parse(readFileSync(join(DATA, 'engine.json'), 'utf8'));
  } catch {
    return null;
  }
  try {
    process.kill(info.pid, 0); // no fa res: només comprova que el procés existeix
    return info;
  } catch {
    return null; // fitxer d'una execució anterior que no va acabar bé
  }
}

/**
 * Connexió amb el motor. Retorna { send(cmd, params), onEvent(fn), close() }. send retorna el resultat o llança un
 * error amb el missatge del motor. Llança un error si el motor no funciona o no respon en 3 segons.
 */
async function connect() {
  const info = runtimeInfo();
  if (!info) throw new Error('El motor no està en marxa (no hi ha cap dades\\engine.json d\'un procés viu).');
  const socket = new WebSocket(`ws://127.0.0.1:${info.port}/ws?token=${encodeURIComponent(info.token)}`);
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('El motor no respon (3 s sense poder-s\'hi connectar).')), 3000);
    socket.addEventListener('open', () => { clearTimeout(timer); resolve(); });
    socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('No s\'ha pogut connectar amb el motor.')); });
  });

  let nextId = 0;
  const pending = new Map();
  const listeners = [];
  socket.addEventListener('message', (event) => {
    const msg = JSON.parse(event.data);
    if (msg.ev) {
      for (const listener of listeners) listener(msg);
      return;
    }
    const waiting = pending.get(msg.id);
    if (!waiting) return;
    pending.delete(msg.id);
    if (msg.ok) waiting.resolve(msg.result);
    else waiting.reject(new Error(`${msg.error?.message ?? 'Error del motor'} (${msg.error?.code ?? '?'})`));
  });
  socket.addEventListener('close', () => {
    for (const waiting of pending.values()) waiting.reject(new Error('S\'ha tancat la connexió amb el motor.'));
    pending.clear();
  });

  return {
    info,
    send(cmd, params = {}) {
      return new Promise((resolve, reject) => {
        const id = ++nextId;
        pending.set(id, { resolve, reject });
        socket.send(JSON.stringify({ v: 1, id, cmd, ...params }));
      });
    },
    onEvent(listener) { listeners.push(listener); },
    close() { socket.close(); },
  };
}

/** Obre una connexió, hi fa la feina i la tanca. Si el motor no funciona, ho diu i no fa res. */
async function withEngine(work) {
  let engine;
  try {
    engine = await connect();
  } catch (error) {
    console.log(color.yellow(`\n${error.message}`));
    return;
  }
  try {
    await work(engine);
  } catch (error) {
    console.log(color.red(`\nError: ${error.message}`));
  } finally {
    engine.close();
  }
}

// --- Programes externs ---

/** Executa un programa en aquesta finestra (es veu el que escriu) i retorna si ha acabat bé. */
function run(command, args, options = {}) {
  console.log(color.dim(`\n> ${[command, ...args].join(' ')}\n`));
  const result = spawnSync(command, args, { cwd: ROOT, stdio: 'inherit', ...options });
  if (result.error) {
    console.log(color.red(`No s'ha pogut executar «${command}»: ${result.error.message}`));
    return false;
  }
  return result.status === 0;
}

/** Obre un script .cmd de tools\ en una finestra nova, que es queda oberta (la consola continua). */
function openWindow(title, script) {
  const path = join(ROOT, 'tools', script);
  // «start» és una ordre de cmd: amb /s, cmd treu les cometes de fora i la resta queda tal com s'escriu aquí
  spawn('cmd.exe', ['/d', '/s', '/c', `"start "${title}" /D "${ROOT}" cmd /k "${path}""`], {
    windowsVerbatimArguments: true, detached: true, stdio: 'ignore',
  }).unref();
}

// --- Capçalera ---

async function header() {
  console.clear();
  console.log(color.bold('Ona Bages 24h — consola de desenvolupament'));
  const info = runtimeInfo();
  if (!info) {
    console.log(`Motor: ${color.yellow('■ aturat')}`);
    return;
  }
  try {
    const engine = await connect();
    try {
      const hello = await engine.send('engine.hello');
      const { output } = await engine.send('output.get');
      const device = output.current?.name ?? 'cap';
      console.log(`Motor: ${color.green('▶ en marxa')} · versió ${hello.version} · PID ${info.pid} · port ${info.port}`);
      console.log(`Sortida: ${output.emergency ? color.red(`⚠ emergència: ${device}`) : device}`);
      if (output.problem) console.log(color.yellow(`⚠ ${output.problem}`));
    } finally {
      engine.close();
    }
  } catch (error) {
    console.log(`Motor: ${color.red(`⚠ el procés hi és (PID ${info.pid}) però no respon`)} · ${error.message}`);
  }
}

/** Mostra un menú i retorna la tecla triada. */
async function menu(title, options) {
  console.log(`\n${color.cyan(title)}`);
  for (const [key, text] of options) console.log(`  [${key}] ${text}`);
  return (await ask('\nTria una opció: ')).toLowerCase();
}

// --- Menú principal ---

async function main() {
  for (;;) {
    await header();
    const choice = await menu('Menú principal', [
      ['1', 'Arrencar tot (compila el motor i obre la interfície)'],
      ['2', 'Arrencar només el motor (amb el registre a la vista)'],
      ['3', 'Aturar el motor'],
      ['4', 'Compilar el motor'],
      ['5', 'Proves automàtiques…'],
      ['6', 'Diagnòstic del motor…'],
      ['7', 'Empaquetar la versió portable'],
      ['8', 'Obrir la carpeta de dades'],
      ['0', 'Sortir'],
    ]);
    switch (choice) {
      case '1': await startAll(); break;
      case '2': await startEngine(); break;
      case '3': await stopEngine(); break;
      case '4': await buildEngine(); break;
      case '5': await testsMenu(); break;
      case '6': await diagnosticsMenu(); break;
      case '7': await packageApp(); break;
      case '8': spawn('explorer.exe', [DATA], { detached: true, stdio: 'ignore' }).unref(); break;
      case '0': case 'q': return;
      default: break;
    }
  }
}

async function startAll() {
  if (runtimeInfo()) {
    console.log(color.yellow('\nEl motor ja està en marxa: no es podrà recompilar i la interfície es connectarà al que funciona.'));
    console.log('Si vols l\'última versió del motor, primer atura\'l (opció 3).');
    if (!(await confirm('Obro la interfície igualment?'))) return;
  }
  openWindow('Ona Bages 24h - interficie', 'arrencar-tot.cmd');
  console.log(color.green('\nS\'ha obert una finestra nova que compila el motor i obre la interfície.'));
  await pause();
}

async function startEngine() {
  if (runtimeInfo()) {
    console.log(color.yellow('\nJa hi ha un motor en marxa: no se n\'arrenca cap altre (mai dos motors alhora).'));
    await pause();
    return;
  }
  openWindow('Ona Bages 24h - motor', 'arrencar-motor.cmd');
  console.log(color.green('\nS\'ha obert una finestra nova amb el motor i el seu registre. Ctrl+C en aquella finestra l\'atura.'));
  await pause();
}

async function stopEngine() {
  const info = runtimeInfo();
  if (!info) {
    console.log(color.yellow('\nEl motor no està en marxa.'));
    await pause();
    return;
  }
  if (!(await confirm(color.yellow('\nAturar el motor atura l\'emissió. Segur que el vols aturar?')))) return;
  await withEngine(async (engine) => {
    // El motor pot tancar la connexió abans de respondre: no és un error
    await engine.send('engine.shutdown').catch(() => {});
  });
  // Espera que el procés acabi (com a molt 10 s)
  for (let i = 0; i < 50 && runtimeInfo(); i++) await new Promise((resolve) => setTimeout(resolve, 200));
  console.log(runtimeInfo() ? color.red('\nEl motor encara no s\'ha aturat. Mira la seva finestra.') : color.green('\nMotor aturat.'));
  await pause();
}

/** Abans de compilar: si el motor funciona, els seus fitxers estan en ús i la compilació fallarà. */
async function engineFreeToBuild() {
  if (!runtimeInfo()) return true;
  console.log(color.yellow('\nEl motor està en marxa: els seus fitxers estan en ús i no es poden substituir.'));
  return confirm('Vols provar-ho igualment?');
}

async function buildEngine() {
  if (await engineFreeToBuild()) {
    const ok = run('dotnet', ['build', 'engine\\OnaBages24.Engine', '-v', 'q', '-nologo']);
    console.log(ok ? color.green('\nCompilat.') : color.red('\nNo s\'ha pogut compilar. Mira els missatges de sobre.'));
  }
  await pause();
}

async function testsMenu() {
  const choice = await menu('Proves automàtiques', [
    ['1', 'Motor (xUnit; triga uns 30 s)'],
    ['2', 'Interfície (node --test)'],
    ['3', 'Totes'],
    ['0', 'Tornar'],
  ]);
  if (!['1', '2', '3'].includes(choice)) return;
  const results = [];
  if (choice === '1' || choice === '3') {
    if (await engineFreeToBuild()) results.push(['Motor', run('dotnet', ['test', 'engine\\OnaBages24.Tests', '-nologo'])]);
  }
  if (choice === '2' || choice === '3') {
    // npm és un .cmd: cal la consola de Windows per executar-lo
    results.push(['Interfície', run('npm test', [], { cwd: join(ROOT, 'app'), shell: true })]);
  }
  console.log('');
  for (const [name, ok] of results) console.log(`${name}: ${ok ? color.green('✔ passen') : color.red('✖ n\'hi ha que fallen')}`);
  await pause();
}

async function packageApp() {
  console.log('\nGenera build\\OnaBages24\\ i build\\OnaBages24-<versió>.zip. La carpeta build\\ s\'esborra abans.');
  if (await confirm('Empaqueto?')) {
    const ok = run('cmd.exe', ['/d', '/c', join(ROOT, 'tools', 'empaquetar.cmd')]);
    console.log(ok ? color.green('\nEmpaquetat.') : color.red('\nNo s\'ha pogut empaquetar.'));
  }
  await pause();
}

// --- Diagnòstic ---

async function diagnosticsMenu() {
  for (;;) {
    await header();
    const choice = await menu('Diagnòstic del motor', [
      ['1', 'Estat general: què sona, AutoDJ i avisos'],
      ['2', 'Cua i rotació: cada pista amb la subcategoria i el pas d\'on surt'],
      ['3', 'Dispositius d\'àudio'],
      ['4', 'Planificador: estat, pròximes execucions i registre d\'avui'],
      ['5', 'Historial: últimes pistes que han sonat'],
      ['6', 'Escoltar els esdeveniments del motor uns segons'],
      ['7', 'Comprovar l\'emissió: forats a l\'historial (comprova-emissio)'],
      ['8', 'Enviar una ordre lliure'],
      ['9', 'Últimes línies del registre del motor'],
      ['0', 'Tornar'],
    ]);
    switch (choice) {
      case '1': await withEngine(showStatus); break;
      case '2': await withEngine(showQueue); break;
      case '3': await withEngine(showDevices); break;
      case '4': await withEngine(showScheduler); break;
      case '5': await withEngine(showHistory); break;
      case '6': await listenEvents(); break;
      case '7': checkBroadcast(); break;
      case '8': await sendCommand(); break;
      case '9': showLog(); break;
      case '0': case '': return;
      default: continue;
    }
    await pause();
  }
}

/** Hora local curta (HH:MM:SS) d'una hora ISO. */
function time(iso) {
  return iso ? new Date(iso).toLocaleTimeString('ca-ES') : '—';
}

/** Durada en m:ss. */
function duration(ms) {
  if (ms == null) return '?:??';
  const seconds = Math.round(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function describe(item) {
  if (!item) return '—';
  const text = item.artist ? `${item.artist} - ${item.title}` : item.title;
  return `${text} (${duration(item.durationMs)}, ${item.origin})`;
}

async function showStatus(engine) {
  const state = await engine.send('state.get');
  const { queue } = await engine.send('queue.get');
  const { autodj } = await engine.send('autodj.get');
  const { paused } = await engine.send('scheduler.status');
  const uptimeHours = (state.engine.uptime / 3600).toFixed(1);
  console.log(`\nMotor: versió ${state.engine.version}, en marxa fa ${uptimeHours} h, ${state.engine.clients} client(s) connectat(s)`);
  console.log(`Biblioteca: ${state.library.tracks} pistes`);
  console.log(`\nSona: ${describe(queue.current)}${queue.currentEndsAt ? `, s'acaba a les ${time(queue.currentEndsAt)}` : ''}`);
  console.log(`A la cua: ${queue.upcoming.length} pistes`);
  const rotation = autodj.rotation ? `«${autodj.rotation.name}», proper pas ${autodj.nextStep + 1}` : 'cap';
  console.log(`\nAutoDJ: ${autodj.enabled ? color.green('▶ actiu') : color.yellow(`■ aturat fins a les ${time(autodj.disabledUntil)}`)} · rotació ${rotation}`);
  if (autodj.sweepers) console.log(`Sweepers: ${autodj.sweepers.name}`);
  if (autodj.problem) console.log(color.yellow(`⚠ ${autodj.problem}`));
  if (autodj.notice) console.log(color.yellow(`⚠ ${autodj.notice}`));
  console.log(`\nPlanificador: ${paused ? color.yellow(`■ aturat fins a les ${time(paused.until)} (${paused.by ?? '?'})`) : color.green('▶ funciona')}`);
}

async function showQueue(engine) {
  const { autodj } = await engine.send('autodj.get');
  const { rotations } = await engine.send('rotations.list');
  const rotation = rotations.find((r) => r.id === autodj.rotation?.id);
  const stepName = (s) => `${s.bag ? `bossa «${s.bag}» → ` : ''}${s.category} / ${s.subcategory ?? '(tota la categoria)'}${s.tag ? ` [${s.tag}]` : ''}`;
  if (rotation) {
    console.log(`\nRotació activa: «${rotation.name}», proper pas ${autodj.nextStep + 1}`);
    rotation.steps.forEach((s, i) => console.log(`  pas ${String(i + 1).padStart(2)}: ${stepName(s)}`));
  } else {
    console.log(color.yellow('\nNo hi ha cap rotació activa.'));
  }
  if (autodj.problem) console.log(color.yellow(`⚠ ${autodj.problem}`));

  const { queue } = await engine.send('queue.get');
  const subcategories = new Map(); // trackId → «Categoria / Subcategoria»
  async function subcategory(item) {
    if (!item?.trackId) return '(no és a la biblioteca)';
    if (!subcategories.has(item.trackId)) {
      try {
        const { track } = await engine.send('library.get', { trackId: item.trackId });
        subcategories.set(item.trackId, track.subcategory ? `${track.subcategory.categoryName} / ${track.subcategory.name}` : '(sense subcategoria)');
      } catch {
        subcategories.set(item.trackId, '(no es troba)');
      }
    }
    return subcategories.get(item.trackId);
  }
  const step = (item) => item?.rotationStep != null
    ? (item.rotationId === rotation?.id ? `pas ${item.rotationStep + 1}` : `pas ${item.rotationStep + 1} d'una altra rotació`)
    : item?.origin;

  console.log(`\nSona:   ${describe(queue.current)} · ${await subcategory(queue.current)} · ${step(queue.current) ?? ''}`);
  for (const [i, item] of queue.upcoming.entries()) {
    const at = item.plannedAt ? time(item.plannedAt) : '  ?   ';
    console.log(`  ${String(i + 1).padStart(2)}. ${at}  ${describe(item)} · ${await subcategory(item)} · ${step(item)}`);
  }
}

async function showDevices(engine) {
  const { devices } = await engine.send('devices.list', { kind: 'output', includeInactive: true });
  const { output } = await engine.send('output.get');
  console.log(`\nSortida configurada: ${output.configured?.name ?? 'cap'}`);
  console.log(`Sortida d'ara: ${output.current?.name ?? 'cap'}${output.emergency ? color.red(' (⚠ emergència)') : ''}`);
  console.log(`Latència: ${output.latencyMs} ms${output.latencyMeasured ? '' : ' (suposada: encara no s\'ha pogut mesurar)'}`);
  if (output.problem) console.log(color.yellow(`⚠ ${output.problem}`));
  console.log('\nSortides de Windows:');
  for (const device of devices) {
    const marks = [];
    if (device.id === output.current?.id) marks.push(color.green('◀ s\'emet per aquí'));
    if (device.id === output.configured?.id) marks.push('(configurada)');
    const status = device.status === 'active' ? 'activa' : device.status === 'disabled' ? 'desactivada' : 'desconnectada';
    console.log(`  - ${device.name} · ${status} · ${device.connector} ${marks.join(' ')}`);
  }
}

async function showScheduler(engine) {
  const { paused } = await engine.send('scheduler.status');
  console.log(`\nPlanificador: ${paused ? color.yellow(`■ aturat fins a les ${time(paused.until)} (${paused.by ?? '?'})`) : color.green('▶ funciona')}`);
  const { occurrences } = await engine.send('scheduler.occurrences', { limit: 15 });
  console.log('\nPròximes execucions (24 h):');
  if (occurrences.length === 0) console.log('  (cap)');
  for (const o of occurrences) {
    const warning = o.skippedClockChange ? color.yellow(' ⚠ no sonarà (canvi d\'hora)') : '';
    console.log(`  ${o.date} ${o.time}  ${o.mode.padEnd(9)} ${o.name}${warning}`);
  }
  const { entries } = await engine.send('scheduler.log', { limit: 500 });
  console.log('\nRegistre d\'avui (les 15 últimes):');
  if (entries.length === 0) console.log('  (res)');
  for (const e of entries.slice(-15)) {
    const result = e.result === 'OK' ? color.green(e.result.padEnd(14)) : color.yellow(e.result.padEnd(14));
    console.log(`  ${time(e.scheduledFor)}  ${result} ${e.name}${e.detail ? color.dim(` · ${e.detail}`) : ''}`);
  }
}

async function showHistory(engine) {
  const { entries } = await engine.send('history.list', { limit: 20 });
  console.log('\nÚltimes 20 pistes (la més recent primer):');
  for (const e of entries) {
    const played = e.playedMs == null ? color.green('sona ara') : `${duration(e.playedMs)}${e.complete ? '' : color.yellow(' ⚠ tallada')}`;
    console.log(`  ${time(e.startedAt)}  ${e.artist ? `${e.artist} - ` : ''}${e.title} · ${e.origin} · ${played}`);
  }
}

async function listenEvents() {
  const answer = await ask('Quants segons? (per defecte, 15) ');
  const seconds = Number(answer) > 0 ? Number(answer) : 15;
  const meters = await confirm('Mostro també els mesuradors (20 per segon)?');
  await withEngine(async (engine) => {
    console.log(color.dim(`\nEscoltant ${seconds} s (sense engine.status${meters ? '' : ' ni mesuradors'})...\n`));
    engine.onEvent((msg) => {
      if (msg.ev === 'engine.status' || (!meters && msg.ev === 'meters')) return;
      const { v, ev, ...rest } = msg;
      console.log(`${color.dim(new Date().toLocaleTimeString('ca-ES'))} ${color.cyan(ev)} ${JSON.stringify(rest)}`);
    });
    await new Promise((resolve) => setTimeout(resolve, seconds * 1000));
  });
}

function checkBroadcast() {
  if (!existsSync(ENGINE_EXE)) {
    console.log(color.yellow('\nEl motor encara no està compilat (opció 4 del menú principal).'));
    return;
  }
  // Només llegeix: es pot fer amb el motor en marxa (les últimes 72 hores)
  run(ENGINE_EXE, ['comprova-emissio']);
}

/** «ordre nom=valor nom=valor»: el valor es llegeix com a JSON si es pot (16, null, true, [1,2]); si no, és un text. */
function parseCommand(line) {
  const [cmd, ...pairs] = line.split(/\s+/).filter(Boolean);
  const params = {};
  for (const pair of pairs) {
    const at = pair.indexOf('=');
    if (at < 1) throw new Error(`«${pair}» no és nom=valor`);
    const raw = pair.slice(at + 1);
    try { params[pair.slice(0, at)] = JSON.parse(raw); } catch { params[pair.slice(0, at)] = raw; }
  }
  return { cmd, params };
}

async function sendCommand() {
  console.log('\nEscriu l\'ordre i els paràmetres com a nom=valor (les ordres són a docs\\protocol.md).');
  console.log(color.dim('Exemples: queue.get · library.get trackId=123 · autodj.set enabled=true rotationId=2'));
  console.log(color.yellow('Atenció: les ordres que canvien coses (aturar, treure de la cua…) s\'apliquen de debò a l\'emissió.'));
  const line = await ask('> ');
  if (!line) return;
  let command;
  try {
    command = parseCommand(line);
  } catch (error) {
    console.log(color.red(error.message));
    return;
  }
  await withEngine(async (engine) => {
    const result = await engine.send(command.cmd, command.params);
    console.log(JSON.stringify(result, null, 2));
  });
}

function showLog() {
  const folder = join(DATA, 'logs');
  let files = [];
  try {
    files = readdirSync(folder).filter((f) => /^motor-.*\.log$/.test(f)).sort();
  } catch { /* encara no hi ha registres */ }
  if (files.length === 0) {
    console.log(color.yellow(`\nNo hi ha cap registre del motor a ${folder}.`));
    return;
  }
  const last = files[files.length - 1];
  const lines = readFileSync(join(folder, last), 'utf8').split(/\r?\n/).filter(Boolean);
  console.log(color.dim(`\n${join(folder, last)} (les 40 últimes línies):\n`));
  for (const line of lines.slice(-40)) {
    if (/\b(ERROR|FAIL|CRIT)\b/.test(line)) console.log(color.red(line));
    else if (/\bWARN\b/.test(line)) console.log(color.yellow(line));
    else console.log(line);
  }
}

await main();
pipedReader?.close();
