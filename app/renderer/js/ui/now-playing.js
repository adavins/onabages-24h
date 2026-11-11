// @ts-check
// Pista que sona: títol, artista, tipus, barra de progrés, temps sonat, quan entra la mescla i quant falta.
// La pista surt de la cua (queue.get / queue.changed) i la posició, de l'esdeveniment «meters» (cada 50 ms).

import { formatDuration, formatRemaining, typeName, originName } from '../format.js';
import { setText, setStyle } from './dom.js';

const ENDING_MS = 10_000; // els últims 10 s, el temps que falta es marca en vermell (amb icona)

/** @param {string} id */
function $(id) {
  return /** @type {HTMLElement} */ (document.getElementById(id));
}

export class NowPlaying {
  /** @type {{ id: string, title: string, artist: string, type: string, origin: string } | null} */
  #current = null;

  /**
   * La pista que sona ara (element de la cua, docs/protocol.md 4.2), o null.
   * @param {any} item
   */
  setCurrent(item) {
    if (item?.id === this.#current?.id) return;
    this.#current = item ?? null;

    if (!item) {
      $('np-title').textContent = 'Res no sona';
      $('np-artist').textContent = '';
      $('np-type').classList.add('d-none');
      $('np-origin').textContent = '';
      this.setPosition(null);
      return;
    }

    $('np-title').textContent = item.title || '(sense títol)';
    $('np-artist').textContent = item.artist || '';
    $('np-type').textContent = typeName(item.type);
    $('np-type').classList.remove('d-none');
    $('np-origin').textContent = item.eventName ? `Esdeveniment «${item.eventName}»` : originName(item.origin);
    document.title = `${item.artist ? `${item.artist} - ` : ''}${item.title} · Ona Bages 24h`;
  }

  /**
   * Posició de la pista que sona (esdeveniment meters, docs/protocol.md 5.1), o null.
   * @param {{ itemId: string, elapsedMs: number, remainingMs: number, mixInMs: number, durationMs: number } | null} position
   */
  setPosition(position) {
    // Just quan canvia de pista, la posició pot arribar abans que la cua nova: s'espera
    if (position && position.itemId !== this.#current?.id) return;

    if (!position || !this.#current) {
      $('np-bar').style.width = '0';
      $('np-mix-marker').classList.add('d-none');
      $('np-elapsed').textContent = '–';
      $('np-mix-note').textContent = '';
      $('np-mix-in').textContent = '–';
      $('np-remaining').textContent = '–';
      $('np-remaining').classList.remove('ending');
      if (!this.#current) document.title = 'Ona Bages 24h';
      return;
    }

    const { elapsedMs, remainingMs, mixInMs, durationMs } = position;
    const mixAtMs = elapsedMs + mixInMs;           // on és el punt de mescla, des de l'inici
    const overlapMs = Math.max(0, remainingMs - mixInMs);
    const fraction = durationMs > 0 ? Math.min(1, elapsedMs / durationMs) : 0;
    const mixFraction = durationMs > 0 ? Math.min(1, mixAtMs / durationMs) : 1;

    // Només es toca la pàgina quan el que es veu canvia (la barra, a dècimes de percentatge): D-069
    setStyle($('np-bar'), 'width', `${(fraction * 100).toFixed(1)}%`);
    const marker = $('np-mix-marker');
    marker.classList.toggle('d-none', mixFraction >= 1);
    setStyle(marker, 'left', `${(mixFraction * 100).toFixed(1)}%`);

    setText($('np-elapsed'), `${formatDuration(elapsedMs)} sonats de ${formatDuration(durationMs)}`);
    setText($('np-mix-note'), overlapMs > 0
      ? `Mescla amb la següent a ${formatDuration(mixAtMs)} · solapament ${(overlapMs / 1000).toLocaleString('ca', { maximumFractionDigits: 1 })} s`
      : '');
    setText($('np-mix-in'), formatRemaining(mixInMs));
    setText($('np-remaining'), formatRemaining(remainingMs));
    $('np-remaining').classList.toggle('ending', remainingMs <= ENDING_MS);
  }
}
