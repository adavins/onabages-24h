// @ts-check
// Títol fix per a BUTT (D-184): un text que surt a NowPlaying.txt fins a una hora, sigui quina sigui la cua (p. ex. durant
// un directe que no surt de la cua). Es posa i es treu des de la barra d'estat; el motor el treu sol a l'hora de final.

import { $ } from './dom.js';

/** «21:05» d'una data. @param {Date} date */
function clock(date) {
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
}

/**
 * L'hora de final triada: avui a aquesta hora o, si ja ha passat, demà. Null si no hi ha hora.
 * @param {string} value  «HH:MM» del camp d'hora
 */
function endFrom(value) {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const now = new Date();
  const end = new Date(now.getFullYear(), now.getMonth(), now.getDate(), Number(match[1]), Number(match[2]));
  if (end <= now) end.setDate(end.getDate() + 1);
  return end;
}

/**
 * Obre el diàleg del títol fix. Retorna el que diu el motor després del canvi (nowPlaying, docs/protocol.md 4.6), o null si
 * no s'ha canviat res.
 * @param {any} nowPlaying  l'estat actual (nowplaying.get)
 * @param {(cmd: string, params?: any) => Promise<any>} send
 * @returns {Promise<any | null>}
 */
export function openFixedTitle(nowPlaying, send) {
  const dialog = /** @type {HTMLDialogElement} */ ($('fixed-title-dialog'));
  const text = /** @type {HTMLInputElement} */ ($('fixed-title-text'));
  const time = /** @type {HTMLInputElement} */ ($('fixed-title-time'));
  const until = $('fixed-title-until');
  const error = $('fixed-title-error');
  const clear = /** @type {HTMLButtonElement} */ ($('fixed-title-clear'));
  const current = nowPlaying?.fixed ?? null;

  // Si ja n'hi ha un, es pot canviar o treure; si no, es proposa fins a la propera hora en punt
  if (current) {
    text.value = current.text;
    time.value = clock(new Date(current.until));
  } else {
    text.value = '';
    const next = new Date();
    next.setHours(next.getHours() + 1, 0, 0, 0);
    time.value = clock(next);
  }
  clear.classList.toggle('d-none', !current);
  error.textContent = '';
  error.classList.add('d-none');

  const showUntil = () => {
    const end = endFrom(time.value);
    if (!end) {
      until.textContent = 'Tria l\'hora de final.';
      return;
    }
    const tomorrow = end.getDate() !== new Date().getDate();
    until.textContent = `Fins ${tomorrow ? 'demà ' : ''}a les ${clock(end)}. Després torna sol el títol del que sona.`;
  };
  time.oninput = showUntil;
  showUntil();

  return new Promise((resolve) => {
    /** @param {string} cmd @param {any} [params] */
    const run = async (cmd, params) => {
      try {
        const result = await send(cmd, params);
        dialog.close();
        resolve(result.nowPlaying);
      } catch (err) {
        // El motor diu què no va (text buit, hora passada…): el diàleg es queda obert
        error.textContent = /** @type {Error} */ (err).message;
        error.classList.remove('d-none');
      }
    };
    $('fixed-title-ok').onclick = (e) => {
      e.preventDefault();
      const end = endFrom(time.value);
      if (!text.value.trim() || !end) {
        error.textContent = !text.value.trim() ? 'Escriu el text del títol.' : 'Tria l\'hora de final.';
        error.classList.remove('d-none');
        return;
      }
      run('nowplaying.fixed.set', { text: text.value.trim(), until: end.toISOString() });
    };
    // Retorn = posar-lo (sense això, el formulari faria servir el primer botó)
    text.onkeydown = time.onkeydown = (e) => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      $('fixed-title-ok').click();
    };
    clear.onclick = (e) => {
      e.preventDefault();
      run('nowplaying.fixed.clear');
    };
    dialog.addEventListener('close', () => resolve(null), { once: true }); // Cancel·la o Esc (si ja s'ha resolt, no fa res)
    dialog.returnValue = '';
    dialog.showModal();
    text.select();
  });
}
