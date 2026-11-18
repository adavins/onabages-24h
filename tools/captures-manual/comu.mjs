// @ts-check
// Rutes comunes de les eines de captures del manual (com a Ona Bages Play).
// Tot el que es genera (àudios, dades de mostra, captures) va a una carpeta temporal de Windows, fora del repositori i
// fora de les dades de l'usuari (la carpeta «dades» de sempre no es toca mai).
import os from 'node:os';
import path from 'node:path';

export const HERE = import.meta.dirname;
export const REPO = path.resolve(HERE, '..', '..');
export const APP = path.join(REPO, 'app');
export const MANUAL = path.join(REPO, 'docs', 'manual-usuari.html');

export const WORK = path.join(os.tmpdir(), 'OnaBages24Captures');
export const AUDIO = path.join(WORK, 'audios');
export const DATA = path.join(WORK, 'dades');
export const SHOTS = path.join(WORK, 'captures');

/** El port del motor (el mateix que l'aplicació de sempre: per això ha d'estar tancada). */
export const ENGINE_PORT = 47824;

export const sleep = (/** @type {number} */ ms) => new Promise((r) => setTimeout(r, ms));
