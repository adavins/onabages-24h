// Prova ràpida del motor des de la línia d'ordres (sense paquets: Node 22 o posterior ja porta WebSocket).
//
// Ús:
//   node tools/prova-motor.mjs [--dades CARPETA] [ordre ...]
//
// Llegeix el port i el testimoni del fitxer d'execució (engine.json) de la carpeta de dades
// (per defecte, %ProgramData%\OnaBages24), envia les ordres (per defecte: engine.hello, state.get
// i devices.list) i mostra les respostes i el primer esdeveniment engine.status.
//
// Exemple:  node tools/prova-motor.mjs engine.hello devices.list

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const args = process.argv.slice(2);
let dataDir = join(process.env.ProgramData ?? 'C:\\ProgramData', 'OnaBages24');
const commands = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--dades') dataDir = args[++i];
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
// Amb engine.shutdown no arribarà cap engine.status: no cal esperar-lo.
const waitForStatus = !commands.includes('engine.shutdown');

socket.addEventListener('open', () => {
  opened = true;
  commands.forEach((cmd, i) => {
    pending.set(i + 1, cmd);
    socket.send(JSON.stringify({ v: 1, id: i + 1, cmd }));
  });
});

socket.addEventListener('message', (event) => {
  const msg = JSON.parse(event.data);
  if (msg.ev) {
    if (msg.ev === 'engine.status' && !statusSeen) {
      statusSeen = true;
      console.log('← esdeveniment', JSON.stringify(msg));
    }
  } else {
    console.log(`← ${pending.get(msg.id)}`, JSON.stringify(msg, null, 2));
    pending.delete(msg.id);
  }
  if (pending.size === 0 && (statusSeen || !waitForStatus)) socket.close();
});

socket.addEventListener('error', () => {
  if (opened) return; // la connexió s'ha tancat (p. ex. el motor s'ha aturat): no és un error de connexió
  console.error('No s\'ha pogut connectar amb el motor.');
  process.exit(1);
});
