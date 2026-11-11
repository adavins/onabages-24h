// @ts-check
// Pròxims esdeveniments a la pantalla d'emissió (fase 3, pas 1): la franja amb els 4 següents, amb el color del seu mode,
// i el compte enrere fins al primer a la barra d'estat. Les dades són les del motor (scheduler.occurrences); el compte
// enrere és només per mostrar-lo: qui dispara els esdeveniments a l'hora és el motor (D-008).

import { $, el, icon, setText } from './dom.js';
import { modeInfo, shortTime } from '../schedule.js';
import { untilText, dayText } from '../forecast.js';

const SHOWN = 4;
const REFRESH_MS = 60_000;

export class UpcomingEvents {
  #send;
  #onChange;
  /** @type {import('../forecast.js').UpcomingEvent[]} */
  #events = [];
  #token = 0;
  /** L'hora del primer de la franja tal com està dibuixada (si canvia, es redibuixa). @type {string | null} */
  #shownFirst = null;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {() => void} onChange  els esdeveniments han canviat (la previsió de la cua s'ha de tornar a calcular)
   */
  constructor(send, onChange) {
    this.#send = send;
    this.#onChange = onChange;
    setInterval(() => this.#tick(), 1000);
    setInterval(() => this.refresh(), REFRESH_MS);
  }

  /** Els pròxims esdeveniments actius, per ordre (per a la previsió de la cua). */
  get events() {
    return this.#events;
  }

  /** Torna a demanar els pròxims esdeveniments (en connectar-se, quan canvien o quan se n'ha disparat algun). */
  async refresh() {
    const token = ++this.#token;
    try {
      const now = new Date();
      const result = await this.#send('scheduler.occurrences', {
        from: now.toISOString(), to: new Date(now.getTime() + 2 * 86_400_000).toISOString(), limit: 20,
      });
      if (token !== this.#token) return;
      this.#events = result.occurrences.filter((/** @type {any} */ o) => !o.skippedClockChange);
    } catch {
      if (token !== this.#token) return;
      this.#events = []; // sense connexió: ja ho diu la capçalera
    }
    this.#render();
    this.#onChange();
  }

  #render() {
    const strip = $('upcoming-list');
    strip.replaceChildren();
    const now = Date.now();
    const next = this.#events.filter((e) => Date.parse(e.at) > now).slice(0, SHOWN);
    this.#shownFirst = next[0]?.at ?? null;
    $('upcoming-empty').classList.toggle('d-none', next.length > 0);
    next.forEach((e, index) => {
      const info = modeInfo(e.mode);
      const chip = el('span', `upcoming-chip ${info.css}`);
      const day = dayText(e.date, new Date());
      chip.title = `${day} ${shortTime(e.time)} · ${e.name} · ${info.label}`;
      chip.append(icon(info.icon), ' ', el('span', 'upcoming-time', `${day === 'avui' ? '' : `${day} `}${shortTime(e.time)}`),
        ' ', el('span', 'upcoming-name', e.name), el('span', 'upcoming-mode', ` · ${info.label}`));
      if (index === 0) {
        const until = el('span', 'upcoming-until');
        until.id = 'upcoming-until';
        chip.append(' ', until);
      }
      strip.append(chip);
    });
    this.#tick();
  }

  /** Cada segon: el compte enrere de la franja i el de la barra d'estat. */
  #tick() {
    const now = Date.now();
    const first = this.#events.find((e) => Date.parse(e.at) > now);
    // Si el primer de la franja ja ha passat, es redibuixa (i al minut es torna a demanar al motor)
    if ((first?.at ?? null) !== this.#shownFirst) {
      this.#render();
      this.#onChange(); // la previsió de la cua es torna a calcular amb el TALLA següent
      return;
    }
    const status = $('status-next');
    if (!first) {
      setText(status, '');
      return;
    }
    const info = modeInfo(first.mode);
    const until = untilText(Date.parse(first.at) - now);
    const chip = document.getElementById('upcoming-until');
    if (chip) setText(chip, until);
    setText(status, `Pròxim esdeveniment: ${dayText(first.date, new Date())} ${shortTime(first.time)} · ${first.name} · ${info.label} (${until})`);
  }
}
