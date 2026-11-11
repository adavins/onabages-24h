// Prova ràpida del motor des de la línia d'ordres (sense paquets: Node 22 o posterior ja porta WebSocket).
//
// Ús:
//   node tools/prova-motor.mjs [--dades CARPETA] [--carpeta CARPETA_DE_MUSICA [--quantes N]] [--escolta SEGONS] [ordre ...]
//
// Llegeix el port i el testimoni del fitxer d'execució (engine.json) de la carpeta de dades
// (per defecte, la carpeta «dades» del repositori), envia les ordres (per defecte: engine.hello, state.get
// i devices.list) i mostra les respostes i el primer esdeveniment engine.status.
//
// Una ordre pot ser només el nom o un objecte JSON amb els paràmetres (sense "v" ni "id"):
//   node tools/prova-motor.mjs engine.hello devices.list
//   node tools/prova-motor.mjs "{\"cmd\":\"queue.insert\",\"items\":[{\"trackId\":12}]}"   (consola de Windows, cmd)
//
// A PowerShell, el JSON amb cometes es trenca: millor els paràmetres com a «nom=valor» darrere de l'ordre (el valor
// es llegeix com a JSON si es pot: números, null, true, [1,2]…; si no, és un text):
//   node tools/prova-motor.mjs sweepers.set subcategoryId=16 autodj.set rotationId=2 queue.get
//   node tools/prova-motor.mjs ducking.set sweepers=-6
//
// Amb --escolta SEGONS, mostra els esdeveniments des del principi (també els que arriben abans de les respostes) i,
// després de les respostes, es queda escoltant aquests segons. No mostra els engine.status (excepte el primer) ni
// els mesuradors; amb --mesuradors, també els mesuradors (meters, 20 per segon):
//   node tools/prova-motor.mjs --escolta 2 --mesuradors engine.ping
//
// Esdeveniments de prova (fase 2, passos 3 a 5): es creen per a avui, d'aquí a SEGONS segons, i només sonen una vegada.
//   --encua SEGONS ID[,ID…]      ENCUA amb aquestes pistes (els id són els trackId de queue.get o history.list)
//   --talla SEGONS ID[,ID…]      TALLA amb aquestes pistes: talla el que soni a l'hora exacta i esborra la cua
//   --fos MS                     (després de --talla) fos de la pista tallada; per defecte, 1000 ms
//   --rotacio SEGONS ROTACIO_ID  SISTEMA que canvia la rotació de l'AutoDJ (els id, a rotations.list)
//   --superposa SEGONS ID        SUPERPOSA (pas 5b): la pista sona pel reproductor auxiliar 1, per sobre de la música
//   --atenua DB                  (després de --superposa) atenuació de la música mentre sona, p. ex. -12
//   p. ex.: node tools/prova-motor.mjs --escolta 90 --encua 60 1234,5678
//           node tools/prova-motor.mjs --escolta 60 --talla 30 1234 --fos 3000
//           node tools/prova-motor.mjs --escolta 50 --superposa 40 31 --atenua -12

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const args = process.argv.slice(2);
// Per defecte, la carpeta «dades» del repositori (D-044): aquest fitxer és a tools\
let dataDir = join(import.meta.dirname, '..', 'dades');
const commands = [];
let listenSeconds = 0;
let showMeters = false;

/** Hora local d'aquí a uns segons (HH:MM:SS) i la data d'avui (AAAA-MM-DD). */
function inSeconds(seconds) {
  const at = new Date(Date.now() + seconds * 1000);
  const pad = (n) => String(n).padStart(2, '0');
  return {
    time: `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}`,
    date: `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`,
  };
}

/** Un esdeveniment de prova que sona una sola vegada (recurrència DATES amb la data d'avui). */
function testEvent(name, mode, seconds, actions, extra = {}) {
  const { time, date } = inSeconds(seconds);
  console.log(`Esdeveniment de prova «${name}» a les ${time}`);
  return JSON.stringify({
    cmd: 'scheduler.save',
    event: { name, mode, startTime: time, recurrence: 'DATES', daysMask: 0, includedDates: [date], actions, ...extra },
  });
}
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--dades') dataDir = args[++i];
  // Drecera: --carpeta "O:\musica\BOSSA 9…" [--quantes 10] → afegeix pistes barrejades d'aquesta carpeta a la cua
  else if (args[i] === '--carpeta') commands.push(JSON.stringify({ cmd: 'queue.insert', folder: args[++i], limit: 10 }));
  else if (args[i] === '--escolta') listenSeconds = Number(args[++i]);
  else if (args[i] === '--mesuradors') showMeters = true;
  else if (args[i] === '--encua') {
    const seconds = Number(args[++i]);
    const ids = String(args[++i]).split(',').map(Number);
    commands.push(testEvent('Prova ENCUA', 'ENCUA', seconds, ids.map((songId) => ({ type: 'PISTA', params: { songId } }))));
  }
  else if (args[i] === '--talla') {
    const seconds = Number(args[++i]);
    const ids = String(args[++i]).split(',').map(Number);
    commands.push(testEvent('Prova TALLA', 'TALLA', seconds, ids.map((songId) => ({ type: 'PISTA', params: { songId } })),
      { clearQueue: true }));
  }
  else if (args[i] === '--fos') {
    const last = JSON.parse(commands.pop());
    last.event.fadeMs = Number(args[++i]);
    commands.push(JSON.stringify(last));
  }
  else if (args[i] === '--superposa') {
    const seconds = Number(args[++i]);
    const songId = Number(args[++i]);
    commands.push(testEvent('Prova SUPERPOSA', 'SUPERPOSA', seconds, [{ type: 'AUXILIAR', params: { songId, player: 1 } }]));
  }
  else if (args[i] === '--atenua') {
    const last = JSON.parse(commands.pop());
    last.event.duckingDb = Number(args[++i]);
    commands.push(JSON.stringify(last));
  }
  else if (args[i] === '--rotacio') {
    const seconds = Number(args[++i]);
    const rotacioId = Number(args[++i]);
    commands.push(testEvent('Prova canvi de rotació', 'SISTEMA', seconds, [{ type: 'ROTACIO', params: { rotacioId } }]));
  }
  else if (args[i] === '--quantes') {
    const last = JSON.parse(commands.pop());
    commands.push(JSON.stringify({ ...last, limit: Number(args[++i]) }));
  }
  // Paràmetre «nom=valor» de l'ordre anterior (sense JSON ni cometes, que PowerShell trenca). El valor es llegeix com a
  // JSON si es pot (16, -8, null, true, [1,2]); si no, és un text.
  else if (/^[A-Za-z]\w*=/.test(args[i]) && commands.length > 0) {
    const [name, ...rest] = args[i].split('=');
    const raw = rest.join('=');
    let value;
    try { value = JSON.parse(raw); } catch { value = raw; }
    const last = commands.pop();
    const params = last.trim().startsWith('{') ? JSON.parse(last) : { cmd: last };
    commands.push(JSON.stringify({ ...params, [name]: value }));
  }
  else commands.push(args[i]);
}
if (commands.length === 0) commands.push('engine.hello', 'state.get', 'devices.list');

let runtime;
try {
  runtime = JSON.parse(readFileSync(join(dataDir, 'engine.json'), 'utf8'));
} catch {
  console.error(`No es troba ${join(dataDir, 'engine.json')}: el motor no funciona o fa servir una altra carpeta de dades.`);
  process.exit(1);
}

const socket = new WebSocket(`ws://127.0.0.1:${runtime.port}/ws?token=${encodeURIComponent(runtime.token)}`);
const pending = new Map();
let statusSeen = false;
let opened = false;
let listening = false;
let listenTimer = null;
// Cada ordre: un nom («engine.hello») o un objecte JSON amb els paràmetres
const messages = commands.map((text, i) => {
  const params = text.trim().startsWith('{') ? JSON.parse(text) : { cmd: text };
  return { v: 1, id: i + 1, ...params };
});
// Amb engine.shutdown no arribarà cap engine.status: no cal esperar-lo.
const waitForStatus = !messages.some((m) => m.cmd === 'engine.shutdown');

socket.addEventListener('open', () => {
  opened = true;
  for (const message of messages) {
    pending.set(message.id, message.cmd);
    socket.send(JSON.stringify(message));
  }
});

socket.addEventListener('message', (event) => {
  const msg = JSON.parse(event.data);
  if (msg.ev) {
    if (msg.ev === 'engine.status' && !statusSeen) {
      statusSeen = true;
      console.log('← esdeveniment', JSON.stringify(msg));
    } else if ((listening || listenSeconds > 0) && msg.ev !== 'engine.status' && (showMeters || msg.ev !== 'meters')) {
      console.log('← esdeveniment', JSON.stringify(msg));
    }
  } else {
    console.log(`← ${pending.get(msg.id)}`, JSON.stringify(msg, null, 2));
    pending.delete(msg.id);
  }
  if (pending.size === 0 && (statusSeen || !waitForStatus) && !listening && !listenTimer) {
    if (listenSeconds > 0) {
      // Es queda escoltant els esdeveniments uns segons més
      listening = true;
      listenTimer = setTimeout(() => socket.close(), listenSeconds * 1000);
    } else {
      socket.close();
    }
  }
});

socket.addEventListener('error', () => {
  if (opened) return; // la connexió s'ha tancat (p. ex. el motor s'ha aturat): no és un error de connexió
  console.error('No s\'ha pogut connectar amb el motor.');
  process.exit(1);
});
