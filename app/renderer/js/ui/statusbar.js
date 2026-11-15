// @ts-check
// Barra d'estat de sota: per on s'emet (avís si és la sortida d'emergència) i el títol per a BUTT. Els avisos són a warnings.js.
// Cada estat amb color + icona + text.

import { $, statusLabel } from './dom.js';

/** @param {any} output  de output.get o output.changed (docs/protocol.md, 4.3) */
export function renderOutput(output) {
  const target = $('status-output');
  target.title = 'Clica per triar la sortida d\'àudio';
  if (!output?.current) {
    target.replaceChildren(statusLabel('text-danger', 'bi-volume-mute-fill', `Sense sortida d'àudio${output?.problem ? `: ${output.problem}` : ''}`));
  } else if (output.emergency) {
    target.replaceChildren(statusLabel('text-danger', 'bi-exclamation-triangle-fill', `Sortida d'emergència: ${output.current.name}`));
    if (output.problem) target.title = `${output.problem}\nClica per triar la sortida d'àudio`;
  } else {
    target.replaceChildren(statusLabel('', 'bi-speaker-fill', `Sortida: ${output.current.name}`));
    if (output.problem) target.title = `${output.problem}\nClica per triar la sortida d'àudio`;
  }
}

/** @param {any} nowPlaying  de nowplaying.get (docs/protocol.md, 4.6) */
export function renderNowPlaying(nowPlaying) {
  const target = $('status-butt');
  target.title = nowPlaying?.path ?? '';
  if (!nowPlaying) {
    target.replaceChildren();
  } else if (!nowPlaying.enabled) {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-slash-circle', 'BUTT: desactivat'));
    target.title = 'Títol per a BUTT: desactivat';
  } else if (nowPlaying.problem) {
    target.replaceChildren(statusLabel('text-danger', 'bi-exclamation-triangle-fill', 'Títol per a BUTT: no es pot escriure'));
    target.title = nowPlaying.problem;
  } else if (nowPlaying.text) {
    // El que sona, amb una icona de play (el que rep BUTT); la ruta del fitxer, en passar-hi el ratolí
    target.replaceChildren(statusLabel('', 'bi-play-fill', nowPlaying.text));
    target.title = `Títol per a BUTT: ${nowPlaying.text}${nowPlaying.path ? `\n${nowPlaying.path}` : ''}`;
  } else {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-play', 'Encara no hi ha cap títol'));
    target.title = 'Títol per a BUTT: encara no n\'hi ha cap';
  }
}
