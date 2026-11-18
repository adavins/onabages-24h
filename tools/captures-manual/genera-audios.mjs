// @ts-check
// Pas 1: àudios sintètics per a les captures del manual, amb noms neutres (res de la música ni dels anunciants de debò).
// WAV de 22,05 kHz, 16 bits, mono, fluixos (uns −30 dB: prou per no comptar com a silenci, prou fluixos per no
// molestar). Uns 130 MB en total, a la carpeta temporal. Es poden tornar a generar quan es vulgui.
import fs from 'node:fs';
import path from 'node:path';
import { AUDIO } from './comu.mjs';

const RATE = 22050;

/** [carpeta, nom, segons] */
const FILES = [
  ['Musica pop', 'Artista A - Cançó del matí', 186],
  ['Musica pop', 'Artista B - Carretera', 204],
  ['Musica pop', 'Artista C - Sol de tarda', 172],
  ['Musica pop', "Grup D - Nit d'estiu", 158],
  ['Musica pop', 'Artista E - Ciutat', 193],
  ['Musica pop', 'Grup F - Platja', 167],
  ['Musica catala', 'Grup G - La plaça', 181],
  ['Musica catala', 'Artista H - Muntanya', 214],
  ['Musica catala', 'Grup I - Festa major', 176],
  ['Musica catala', 'Artista J - Riu avall', 162],
  ['Musica catala', 'Grup K - Diumenge', 199],
  ['Falques', 'Ferreteria Exemple - Falca de tardor', 20],
  ['Falques', 'Ferreteria Exemple - Falca de Nadal', 20],
  ['Falques', 'Forn de Mostra - Falca', 30],
  ['Sweepers', 'Indicatiu 1', 4],
  ['Sweepers', 'Indicatiu 2', 5],
  ['Sweepers', 'Cortina de notícies', 8],
  ['Programes', 'El Magazín de Mostra', 600],
  ['Programes', 'Informatiu', 300],
];

for (const [folder, name, seconds] of /** @type {[string, string, number][]} */ (FILES)) {
  const dir = path.join(AUDIO, folder);
  fs.mkdirSync(dir, { recursive: true });
  const frames = seconds * RATE;
  const data = Buffer.alloc(frames * 2);
  const freq = 220 + (name.length * 37) % 400;
  const fade = RATE / 2; // mig segon d'entrada i de sortida: l'analitzador hi troba els punts
  for (let i = 0; i < frames; i++) {
    // Un to amb el volum que va pujant i baixant (perquè la forma d'ona no sigui plana)
    const envelope = (0.6 + 0.4 * Math.sin(2 * Math.PI * i / (RATE * 1.7))) * Math.min(1, i / fade, (frames - i) / fade);
    data.writeInt16LE(Math.round(1000 * envelope * Math.sin(2 * Math.PI * freq * i / RATE)), i * 2);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0); header.writeUInt32LE(36 + data.length, 4); header.write('WAVE', 8);
  header.write('fmt ', 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(RATE, 24); header.writeUInt32LE(RATE * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34);
  header.write('data', 36); header.writeUInt32LE(data.length, 40);
  fs.writeFileSync(path.join(dir, name + '.wav'), Buffer.concat([header, data]));
}
console.log(`Fet: ${FILES.length} àudios a ${AUDIO}`);
