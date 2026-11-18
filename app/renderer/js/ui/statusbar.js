// @ts-check
// Barra d'estat de sota: per on s'emet (avís si és la sortida d'emergència) i el títol per a BUTT. Els avisos són a warnings.js.
// Cada estat amb color + icona + text.

import { $, statusLabel } from './dom.js';

/** @param {any} output  de output.get o output.changed (docs/protocol.md, 4.3) */
export function renderOutput(output) {
  // Només la icona amb color (D-168): el text, en passar-hi el ratolí i per als lectors de pantalla. Una sortida
  // d'emergència o cap sortida també surt a la barra d'avisos, amb el text
  const target = $('status-output');
  /** @type {[string, string, string]} */
  let [className, iconName, text] = ['text-success', 'bi-speaker-fill', `Sortida: ${output?.current?.name ?? ''}`];
  if (!output?.current) [className, iconName, text] = ['text-danger', 'bi-volume-mute-fill', `Sense sortida d'àudio${output?.problem ? `: ${output.problem}` : ''}`];
  else if (output.emergency) [className, iconName, text] = ['text-danger', 'bi-exclamation-triangle-fill', `Sortida d'emergència: ${output.current.name}`];
  const i = document.createElement('i');
  i.className = `bi ${iconName} ${className}`;
  i.setAttribute('aria-hidden', 'true');
  const hidden = document.createElement('span');
  hidden.className = 'visually-hidden';
  hidden.textContent = text;
  target.replaceChildren(i, hidden);
  target.title = `${text}${output?.problem && output?.current ? `\n${output.problem}` : ''}\nClica per canviar-la (⚙ Configuració → Sortida)`;
}

/** @param {any} nowPlaying  de nowplaying.get (docs/protocol.md, 4.6) */
export function renderNowPlaying(nowPlaying) {
  const target = $('status-butt');
  target.title = nowPlaying?.path ?? '';
  const click = '\nClica per posar o treure un títol fix.';
  if (!nowPlaying) {
    target.replaceChildren();
  } else if (nowPlaying.fixed && nowPlaying.enabled && !nowPlaying.problem) {
    // Títol fix (D-184): es veu diferent, amb l'hora de final
    const until = new Date(nowPlaying.fixed.until).toLocaleTimeString('ca-ES', { hour: '2-digit', minute: '2-digit' });
    target.replaceChildren(statusLabel('text-warning', 'bi-pin-angle-fill', `Títol fix: «${nowPlaying.fixed.text}» fins a les ${until}`));
    target.title = `Títol per a BUTT fix fins a les ${until}${nowPlaying.fixed.eventName ? ` (esdeveniment «${nowPlaying.fixed.eventName}»)` : ' (posat a mà)'}. Després torna sol el títol del que sona.${click}`;
  } else if (!nowPlaying.enabled) {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-slash-circle', 'BUTT: desactivat'));
    target.title = 'Títol per a BUTT: desactivat';
  } else if (nowPlaying.problem) {
    target.replaceChildren(statusLabel('text-danger', 'bi-exclamation-triangle-fill', 'Títol per a BUTT: no es pot escriure'));
    target.title = nowPlaying.problem;
  } else if (nowPlaying.text) {
    // El que sona, amb una icona de play (el que rep BUTT); la ruta del fitxer, en passar-hi el ratolí
    target.replaceChildren(statusLabel('', 'bi-play-fill', nowPlaying.text));
    target.title = `Títol per a BUTT: ${nowPlaying.text}${nowPlaying.path ? `\n${nowPlaying.path}` : ''}${click}`;
  } else {
    target.replaceChildren(statusLabel('text-body-secondary', 'bi-play', 'Encara no hi ha cap títol'));
    target.title = 'Títol per a BUTT: encara no n\'hi ha cap';
  }
}
