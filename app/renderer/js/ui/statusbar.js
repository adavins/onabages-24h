// @ts-check
// Barra d'estat de sota: per on s'emet (avís si és la sortida d'emergència), el títol per a BUTT i els avisos.
// Cada estat amb color + icona + text.

import { $, statusLabel } from './dom.js';

/** @param {any} output  de output.get o output.changed (docs/protocol.md, 4.3) */
export function renderOutput(output) {
  const target = $('status-output');
  if (!output?.current) {
    target.replaceChildren(statusLabel('text-danger', 'bi-volume-mute-fill', `Sense sortida d'àudio${output?.problem ? `: ${output.problem}` : ''}`));
  } else if (output.emergency) {
    target.replaceChildren(statusLabel('text-danger', 'bi-exclamation-triangle-fill', `Sortida d'emergència: ${output.current.name}`));
    target.title = output.problem ?? '';
  } else {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-speaker-fill', `Sortida: ${output.current.name}`));
    target.title = output.problem ?? '';
  }
}

/** @param {any} nowPlaying  de nowplaying.get (docs/protocol.md, 4.6) */
export function renderNowPlaying(nowPlaying) {
  const target = $('status-butt');
  target.title = nowPlaying?.path ?? '';
  if (!nowPlaying) {
    target.replaceChildren();
  } else if (!nowPlaying.enabled) {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-slash-circle', 'Títol per a BUTT: desactivat'));
  } else if (nowPlaying.problem) {
    target.replaceChildren(statusLabel('text-danger', 'bi-exclamation-triangle-fill', 'Títol per a BUTT: no es pot escriure'));
    target.title = nowPlaying.problem;
  } else if (nowPlaying.text) {
    target.replaceChildren(statusLabel('text-success', 'bi-check-lg', `Títol per a BUTT: ${nowPlaying.text}`));
  } else {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-hourglass', 'Títol per a BUTT: encara no n\'hi ha cap'));
  }
}

/**
 * Avisos de l'AutoDJ i de la sortida, junts.
 * @param {{ autodj?: string | null, output?: string | null }} problems
 */
export function renderWarnings(problems) {
  const list = [problems.output, problems.autodj].filter(Boolean);
  const target = $('status-warnings');
  if (list.length === 0) {
    target.replaceChildren(statusLabel('text-success', 'bi-check-circle', 'Cap avís'));
    target.title = '';
  } else {
    target.replaceChildren(statusLabel('text-warning', 'bi-exclamation-triangle-fill', list.length === 1 ? '1 avís' : `${list.length} avisos`));
    target.title = list.join('\n');
  }
}
