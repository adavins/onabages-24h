// @ts-check
// Pista que sona: títol, artista, tipus, barra de progrés, temps sonat, quan entra la mescla i quant falta.
// La pista surt de la cua (queue.get / queue.changed) i la posició, de l'esdeveniment «meters» (cada 50 ms).
// Pausa i salt (D-103): fent clic a la barra, la pista va a aquell punt; en pausa, un indicador vermell.
// Streams (fase 2, pas 7e): l'estat (en directe, tallat, en silenci, caigut i quan torna) i el temps des de l'inici previst.

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
  /** L'última posició (per saber on fer clic a la barra). @type {{ itemId: string, durationMs: number } | null} */
  #position = null;
  /** El stream que sona o que s'espera que torni (queue.stream, docs/protocol.md 4.2), o null. @type {any} */
  #stream = null;

  /**
   * @param {(itemId: string, positionMs: number) => void} onSeek  fent clic a la barra: anar a aquell punt de la pista (D-103)
   */
  constructor(onSeek) {
    const hit = $('np-progress');
    const hover = $('np-hover');
    /** El punt de la pista sota el ratolí, en ms, o null. @param {MouseEvent} e */
    const pointAt = (e) => {
      if (!this.#position || this.#position.durationMs <= 0) return null;
      const rect = $('np-bar').parentElement?.getBoundingClientRect();
      if (!rect || rect.width <= 0) return null;
      const fraction = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
      return { fraction, ms: Math.round(fraction * this.#position.durationMs) };
    };
    hit.addEventListener('mousemove', (e) => {
      const point = pointAt(e);
      hover.classList.toggle('d-none', !point);
      if (!point) return;
      hover.style.left = `${(point.fraction * 100).toFixed(1)}%`;
      hover.dataset.time = formatDuration(point.ms);
    });
    hit.addEventListener('mouseleave', () => hover.classList.add('d-none'));
    hit.addEventListener('click', (e) => {
      const point = pointAt(e);
      if (point && this.#position) onSeek(this.#position.itemId, point.ms);
    });
  }

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
   * La pausa (D-103): quan continuarà sola (hora ISO), o null si no hi ha pausa.
   * @param {{ autoResumeAt: string } | null} paused
   */
  setPaused(paused) {
    const label = $('np-paused');
    label.classList.toggle('d-none', !paused);
    $('np-bar').parentElement?.classList.toggle('is-paused', !!paused);
    if (!paused) return;
    const at = new Date(paused.autoResumeAt).toLocaleTimeString('ca-ES', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const icon = document.createElement('i');
    icon.className = 'bi bi-pause-circle-fill';
    icon.setAttribute('aria-hidden', 'true');
    label.replaceChildren(icon, ` EN PAUSA · continua sola a les ${at}`);
  }

  /**
   * El stream que sona o que s'espera que torni (queue.stream, docs/protocol.md 4.2), o null.
   * @param {any} stream
   */
  setStream(stream) {
    this.#stream = stream;
    this.#showStream();
  }

  /** L'etiqueta de l'estat del stream (es torna a escriure cada vegada que arriba la posició: hi ha temps que avancen). */
  #showStream() {
    const label = $('np-stream');
    const s = this.#stream;
    label.classList.toggle('d-none', !s);
    if (!s) return;
    const now = Date.now();
    const clock = (/** @type {string} */ iso) => new Date(iso).toLocaleTimeString('ca-ES', { hour: '2-digit', minute: '2-digit' });
    const cuts = s.cuts > 1 ? ` · ${s.cuts} talls` : '';
    /** @type {[string, string, string]} classe, icona, text */
    let look;
    switch (s.state) {
      case 'silent':
        look = ['is-warning', 'bi-volume-mute-fill', 'STREAM EN SILENCI · connectat, però no se sent res'];
        break;
      case 'cut':
        look = ['is-warning', 'bi-exclamation-triangle-fill', 'STREAM TALLAT · es reconnecta'];
        break;
      case 'down': {
        const since = s.downSince ? formatDuration(Math.max(0, now - Date.parse(s.downSince))) : '';
        const probe = s.probe === 'ready'
          ? (s.returnAfterSong ? 'ja funciona: tornarà en acabar la cançó' : 'ja funciona: torna ara')
          : s.probe === 'receiving' ? 'torna a rebre àudio, es comprova' : 'es prova de reconnectar';
        look = ['is-down', 'bi-wifi-off',
          `STREAM «${s.name}» CAIGUT fa ${since} · sona música · ${probe} · fins a les ${clock(s.endsAt)}${cuts}`];
        break;
      }
      case 'returning':
        look = ['is-returning', 'bi-arrow-repeat',
          `STREAM «${s.name}» A PUNT · ${s.returnAfterSong ? 'torna en acabar la cançó' : 'torna ara'}`];
        break;
      default:
        look = ['is-live', 'bi-broadcast', `EN DIRECTE${s.title ? ` · ${s.title}` : ''}${s.reconnects ? ' · si cau, es reconnecta' : ''}${cuts}`];
    }
    const [kind, iconName, text] = look;
    if (label.dataset.text === text && label.dataset.kind === kind) return; // només es toca la pàgina si canvia (D-069)
    label.dataset.text = text;
    label.dataset.kind = kind;
    label.className = `now-stream ${kind}`;
    label.title = text;
    const icon = document.createElement('i');
    icon.className = `bi ${iconName}`;
    icon.setAttribute('aria-hidden', 'true');
    label.replaceChildren(icon, ` ${text}`);
  }

  /**
   * Posició de la pista que sona (esdeveniment meters, docs/protocol.md 5.1), o null.
   * @param {{ itemId: string, elapsedMs: number, remainingMs: number, mixInMs: number, durationMs: number, paused?: boolean } | null} position
   */
  setPosition(position) {
    // Just quan canvia de pista, la posició pot arribar abans que la cua nova: s'espera
    if (position && position.itemId !== this.#current?.id) return;
    if (this.#stream) this.#showStream(); // el temps que fa que ha caigut
    // Un stream (fase 2, pas 7e): el temps es compta des de l'inici previst (també si ha caigut i ha tornat) fins a
    // l'hora prevista de final; no es pot anar a un altre punt
    const isStream = !!position && this.#stream?.itemId === position.itemId;
    if (position && isStream) {
      const elapsedMs = Math.max(0, Date.now() - Date.parse(this.#stream.startedAt));
      const fadeMs = Math.max(0, position.remainingMs - position.mixInMs);
      position = { ...position, elapsedMs, durationMs: elapsedMs + position.remainingMs };
      if (fadeMs > 0) position.durationMs = Math.max(position.durationMs, elapsedMs + fadeMs);
    }
    this.#position = position && this.#current && !isStream ? { itemId: position.itemId, durationMs: position.durationMs } : null;
    $('np-progress').classList.toggle('is-disabled', !this.#position);

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
