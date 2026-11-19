// @ts-check
// Pas 3: posa les captures noves dins de docs/manual-usuari.html (el manual és un sol fitxer, amb les imatges en
// base64). Cada captura substitueix la imatge que té aquest text alternatiu (alt): així no importa en quin ordre són.
// Les captures que no hi són (p. ex. si només se n'han fet algunes) es deixen com estaven. Al manual, una imatge que encara
// no té captura es deixa així: <img src="data:image/jpeg;base64," alt="…">.
import fs from 'node:fs';
import path from 'node:path';
import { MANUAL, SHOTS } from './comu.mjs';

/** Captura (a la carpeta de captures, sense .jpg) → text alternatiu de la imatge del manual que substitueix. */
const IMAGES = {
  emissio: "La pantalla d'emissió",
  planificador: 'El planificador amb la graella de la setmana',
  'planificador-editor': "L'editor d'un esdeveniment",
  biblioteca: 'La biblioteca de pistes',
  'biblioteca-fitxa': "La fitxa d'una pista",
  punts: "L'editor de punts",
  historial: "L'historial del que ha sonat",
  rotacions: 'La pantalla de rotacions',
  campanyes: 'La pantalla de campanyes',
  'calendari-campanya': "El calendari d'una campanya",
  certificat: "El certificat d'emissió d'una campanya",
  registre: 'El registre',
};

let html = fs.readFileSync(MANUAL, 'utf8');
let changed = 0;
for (const [name, alt] of Object.entries(IMAGES)) {
  const file = path.join(SHOTS, name + '.jpg');
  if (!fs.existsSync(file)) { console.log(`(sense captura) ${name}`); continue; }
  // <img src="data:image/jpeg;base64,…"  alt="…">  (entre els dos hi pot haver un salt de línia)
  const pattern = new RegExp(`(<img src="data:image/jpeg;base64,)[^"]*("\\s+alt="${alt.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}")`);
  if (!pattern.test(html)) { console.log(`(encara no és al manual) «${alt}»`); continue; }
  const base64 = fs.readFileSync(file).toString('base64');
  html = html.replace(pattern, (_, start, end) => start + base64 + end);
  changed++;
  console.log(`Substituïda: ${alt}`);
}
fs.writeFileSync(MANUAL, html);
console.log(`Fet: ${changed} imatges. Manual: ${(fs.statSync(MANUAL).size / 1048576).toFixed(2)} MB`);
