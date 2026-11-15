// @ts-check
// Avisos de vigència (fase 4, pas 6b; D-146): les pistes i les campanyes dels esdeveniments que s'han acabat, no sonaran,
// tenen les dates malament o comencen aviat. Una línia a la barra de sota («N avisos de vigència»), una finestra amb la
// llista i un requadre a la fitxa de la pista i a l'editor de la campanya. Ho calcula el motor (validity.list).

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { formDialog } from './form-dialog.js';
import { kindInfo, subjectText, barText, warningsFor, removedText } from '../validity-form.js';

/** @typedef {import('../validity-form.js').ValidityWarning} ValidityWarning */

/** Quants esdeveniments es diuen a la llista abans de «i N més». */
const MAX_EVENTS = 8;

export class ValidityView {
  /** @type {(cmd: string, params?: object) => Promise<any>} */
  #send;
  /** @type {{ openTrack: (id: number) => void, openCampaign: (id: number) => void, openEvent: (id: number) => void }} */
  #open;
  /** @type {ValidityWarning[]} */
  #warnings = [];
  /** Els requadres de la fitxa i de l'editor que hi ha a la pàgina. @type {Set<{ node: HTMLElement, subject: 'track' | 'campaign', id: number }>} */
  #boxes = new Set();
  /** La llista de la finestra, si és oberta. @type {HTMLElement | null} */
  #dialogList = null;
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  #timer;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {{ openTrack: (id: number) => void, openCampaign: (id: number) => void, openEvent: (id: number) => void }} open
   */
  constructor(send, open) {
    this.#send = send;
    this.#open = open;
    $('status-validity').addEventListener('click', () => this.openDialog());
    this.#renderBar();
  }

  /**
   * Torna a demanar els avisos. Diversos canvis seguits (p. ex. desar un esdeveniment avisa de diverses coses) en fan una
   * sola petició.
   */
  refresh() {
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.#load(), 300);
  }

  async #load() {
    try {
      this.#warnings = (await this.#send('validity.list')).warnings;
    } catch {
      return; // sense connexió o motor antic: es queda com estava
    }
    this.#renderBar();
    this.#renderBoxes();
    if (this.#dialogList?.isConnected) this.#renderList(this.#dialogList);
  }

  /** Sense connexió amb el motor: no se sap res. */
  clear() {
    this.#warnings = [];
    this.#renderBar();
    this.#renderBoxes();
  }

  #renderBar() {
    const target = $('status-validity');
    const bar = barText(this.#warnings);
    target.classList.toggle('d-none', !bar);
    if (!bar) {
      target.replaceChildren();
      return;
    }
    target.replaceChildren(statusLabel(bar.serious ? 'text-danger' : 'text-warning', 'bi-calendar-x', bar.text));
    target.title = 'Pistes i campanyes dels esdeveniments que s\'han acabat, no sonaran, tenen les dates malament o comencen aviat. Clica per veure-les.';
  }

  /**
   * El requadre amb els avisos d'una pista o d'una campanya, per a la fitxa i l'editor. S'actualitza sol mentre és a la pàgina.
   * @param {'track' | 'campaign'} subject @param {number} id
   */
  box(subject, id) {
    const node = el('div', 'vw-box');
    const entry = { node, subject, id };
    this.#boxes.add(entry);
    this.#fillBox(entry);
    return node;
  }

  #renderBoxes() {
    for (const entry of this.#boxes) {
      // Els que ja no són a la pàgina (s'ha tornat a pintar la fitxa o l'editor) es deixen
      if (!entry.node.isConnected) this.#boxes.delete(entry);
      else this.#fillBox(entry);
    }
  }

  /** @param {{ node: HTMLElement, subject: 'track' | 'campaign', id: number }} entry */
  #fillBox({ node, subject, id }) {
    const list = warningsFor(this.#warnings, subject, id);
    node.classList.toggle('d-none', list.length === 0);
    node.replaceChildren(...list.map((w) => {
      const kind = kindInfo(w.kind);
      const row = el('div', 'vw-box-row');
      row.append(statusLabel(kind.className, kind.icon, `${kind.label}: ${w.text}`));
      if (w.canRemove) row.append(this.#removeButton(w));
      return row;
    }));
  }

  /** La finestra amb tots els avisos. */
  async openDialog() {
    const list = el('div', 'vw-list');
    this.#dialogList = list;
    this.#renderList(list);
    this.refresh(); // per si de cas ha canviat alguna cosa (p. ex. ha passat la mitjanit)
    await formDialog({
      title: 'Avisos de vigència',
      icon: 'bi-calendar-x',
      message: 'Les pistes i les campanyes dels esdeveniments actius que s\'han acabat, no sonaran, tenen les dates malament o comencen d\'aquí a 7 dies o menys.',
      body: [list],
      wide: true,
    });
    this.#dialogList = null;
  }

  /** @param {HTMLElement} list */
  #renderList(list) {
    if (this.#warnings.length === 0) {
      list.replaceChildren(statusLabel('text-success', 'bi-check-circle', 'Cap avís de vigència.'));
      return;
    }
    list.replaceChildren(...this.#warnings.map((w) => {
      const kind = kindInfo(w.kind);
      const item = el('div', 'vw-item');
      const head = el('div', 'vw-head');
      head.append(statusLabel(kind.className, kind.icon, kind.label), el('strong', '', `${subjectText(w)}: ${w.name}`));
      item.append(head, el('div', 'vw-text', w.text));

      // On és: un clic obre l'esdeveniment al Planificador
      if (w.events.length > 0) {
        const events = el('div', 'vw-events');
        events.append(el('span', 'text-body-secondary', w.events.length === 1 ? 'Esdeveniment:' : 'Esdeveniments:'));
        for (const ev of w.events.slice(0, MAX_EVENTS)) {
          const link = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-link btn-sm p-0 vw-event', ev.name));
          link.type = 'button';
          link.title = 'Obre l\'esdeveniment al Planificador';
          link.addEventListener('click', () => this.#leave(list, () => this.#open.openEvent(ev.id)));
          events.append(link);
        }
        if (w.events.length > MAX_EVENTS) events.append(el('span', 'text-body-secondary', `i ${w.events.length - MAX_EVENTS} més`));
        item.append(events);
      }

      const actions = el('div', 'vw-actions');
      const open = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      open.type = 'button';
      open.append(icon(w.subject === 'campaign' ? 'bi-megaphone' : 'bi-collection-play'), w.subject === 'campaign' ? ' Obre la campanya' : ' Obre la pista');
      open.addEventListener('click', () => this.#leave(list, () => (w.subject === 'campaign' ? this.#open.openCampaign(w.id) : this.#open.openTrack(w.id))));
      actions.append(open);
      if (w.canRemove) actions.append(this.#removeButton(w));
      item.append(actions);
      return item;
    }));
  }

  /** Tanca la finestra i fa una cosa (obrir una pista, una campanya, un esdeveniment). @param {HTMLElement} list @param {() => void} then */
  #leave(list, then) {
    /** @type {HTMLDialogElement | null} */ (list.closest('dialog'))?.close();
    then();
  }

  /** «Treu-la dels esdeveniments», amb confirmació. @param {ValidityWarning} w */
  #removeButton(w) {
    const button = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-danger'));
    button.type = 'button';
    button.append(icon('bi-calendar-minus'), ' Treu-la dels esdeveniments');
    button.title = 'La treu dels esdeveniments actius. L\'historial i la fitxa no es toquen.';
    button.addEventListener('click', () => this.#remove(w));
    return button;
  }

  /** @param {ValidityWarning} w */
  async #remove(w) {
    const what = w.subject === 'campaign' ? `la campanya «${w.name}»` : `la pista «${w.name}»`;
    const where = w.events.length === 1 ? `l'esdeveniment «${w.events[0].name}»` : `${w.events.length} esdeveniments actius`;
    const ok = await confirmAction({
      title: 'Treure-la dels esdeveniments?',
      message: `Es traurà ${what} de ${where}. Els blocs de falques que es quedin buits s'esborraran. ` +
        'L\'historial i la fitxa no es toquen, i no es pot desfer.',
      confirm: 'Treu-la',
    });
    if (!ok) return;
    let message;
    try {
      const result = await this.#send('validity.removeFromEvents', w.subject === 'campaign' ? { campaignId: w.id } : { trackId: w.id });
      message = removedText(result);
    } catch (err) {
      message = `No s'ha pogut treure: ${/** @type {Error} */ (err).message}`;
    }
    await confirmAction({ title: 'Treure-la dels esdeveniments', message, confirm: 'D\'acord', info: true });
    this.refresh();
  }
}
