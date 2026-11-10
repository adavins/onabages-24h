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
//   node tools/prova-motor.mjs "{\"cmd\":\"queue.insert\",\"items\":[{\"trackId\":12}]}"
//
// Amb --escolta SEGONS, després de les respostes es queda escoltant i mostra tots els esdeveniments (excepte
// engine.status) durant aquests segons. P. ex., per veure els mesuradors: node tools/prova-motor.mjs --escolta 2 engine.ping

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const args = process.argv.slice(2);
// Per defecte, la carpeta «dades» del repositori (D-044): aquest fitxer és a tools\
let dataDir = join(import.meta.dirname, '..', 'dades');
const commands = [];
let listenSeconds = 0;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--dades') dataDir = args[++i];
  // Drecera: --carpeta "O:\musica\BOSSA 9…" [--quantes 10] → afegeix pistes barrejades d'aquesta carpeta a la cua
  else if (args[i] === '--carpeta') commands.push(JSON.stringify({ cmd: 'queue.insert', folder: args[++i], limit: 10 }));
  else if (args[i] === '--escolta') listenSeconds = Number(args[++i]);
  else if (args[i] === '--quantes') {
    const last = JSON.parse(commands.pop());
    commands.push(JSON.stringify({ ...last, limit: Number(args[++i]) }));
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
    } else if (listening && msg.ev !== 'engine.status') {
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
