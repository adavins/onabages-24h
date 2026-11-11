// @ts-check
// «Atura els esdeveniments» (fase 3, pas 2c; D-073), a la barra d'estat. Sempre per un temps (o fins a una hora), mai
// indefinidament: el motor els torna a activar sol. Mentre estan aturats es veu a la barra d'estat i a la franja de
// pròxims esdeveniments, amb color, icona i text, i amb un botó per reactivar-los abans.

import { $, el, icon } from './dom.js';
import { confirmAction } from './confirm.js';
import { askDuration } from './duration.js';

/** «18:30» d'una hora ISO. @param {string} iso */
function hhmm(iso) {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

export class EventsPause {
  #send;
  /** @type {{ until: string, by: string | null } | null} */
  #paused = null;

  /** @param {(cmd: string, params?: object) => Promise<any>} send */
  constructor(send) {
    this.#send = send;
    this.#render();
  }

  /** En connectar-se: com estan ara. */
  async refresh() {
    try {
      this.show((await this.#send('scheduler.status')).paused);
    } catch { /* sense connexió: ja ho diu la capçalera */ }
  }

  /** L'estat del motor (scheduler.status, scheduler.pause, scheduler.resume o l'esdeveniment scheduler.paused). @param {any} paused */
  show(paused) {
    this.#paused = paused ?? null;
    this.#render();
  }

  #render() {
    const box = $('status-events');
    box.replaceChildren();
    const banner = $('upcoming-paused');
    if (!this.#paused) {
      banner.classList.add('d-none');
      const stop = el('button', 'btn btn-link statusbar-button text-body-secondary');
      stop.type = 'button';
      stop.title = 'Atura els esdeveniments durant un temps (p. ex. durant una retransmissió especial)';
      stop.append(icon('bi-pause-circle'), ' ', el('span', '', 'Atura els esdeveniments'));
      stop.addEventListener('click', () => this.#pause());
      box.append(stop);
      return;
    }

    const text = `Esdeveniments aturats fins a les ${hhmm(this.#paused.until)}`;
    const label = el('span', 'text-warning');
    label.append(icon('bi-pause-circle-fill'), ' ', el('strong', '', text));
    const resume = el('button', 'btn btn-sm btn-warning ms-2');
    resume.type = 'button';
    resume.append(icon('bi-play-circle'), ' ', el('span', '', 'Reactiva\'ls'));
    resume.addEventListener('click', () => this.#resume());
    box.append(label, resume);

    // També a la franja de pròxims esdeveniments, on es mira què ha de sonar
    banner.classList.remove('d-none');
    banner.replaceChildren(icon('bi-pause-circle-fill'), ' ', el('span', '', `${text}: no sonarà cap esdeveniment (tampoc els senyals horaris).`));
  }

  async #pause() {
    const minutes = await askDuration({
      title: 'Aturar els esdeveniments',
      message: 'Mentre estiguin aturats no sonarà cap esdeveniment: ni els programes, ni les promos, ni els senyals horaris. ' +
        'L\'AutoDJ continua. Els que es perdin quedaran anotats al registre com a omesos.',
      confirm: 'Atura els esdeveniments',
      defaultMinutes: 60,
      allowUntil: true,
    });
    if (minutes == null) return;
    try {
      this.show((await this.#send('scheduler.pause', { minutes })).paused);
    } catch (err) {
      await confirmAction({ title: 'No s\'han pogut aturar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  async #resume() {
    try {
      this.show((await this.#send('scheduler.resume')).paused);
    } catch (err) {
      await confirmAction({ title: 'No s\'han pogut reactivar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}
