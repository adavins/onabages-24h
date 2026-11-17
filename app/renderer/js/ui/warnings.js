// @ts-check
// Avisos de la barra de sota (fase 4, pas 6b; D-146): un sol botó amb tots els avisos, amb el color del més greu; en
// clicar-lo, una finestra amb la llista per gravetat i l'accés a l'historial d'incidències. Hi ha les incidències obertes
// del motor (sortida, AutoDJ, títol per a BUTT, carpetes vigilades; fase 5, pas 1b, D-156: incidents.list) i els avisos de
// vigència (pistes i campanyes dels esdeveniments acabades, que no sonaran, amb les dates malament o que comencen aviat;
// validity.list), que surten també a la fitxa de la pista i a l'editor de la campanya.

import { $, el, icon, statusLabel } from './dom.js';
import { formDialog } from './form-dialog.js';
import { kindInfo, levelInfo, subjectText, allWarnings, barState, warningsFor } from '../warnings-form.js';
import { removeFromEvents } from './remove-from-events.js';

/**
 * @typedef {import('../warnings-form.js').ValidityWarning} ValidityWarning
 * @typedef {import('../warnings-form.js').GeneralWarning} GeneralWarning
 */

/** Quants esdeveniments es diuen a la llista abans de «i N més». */
const MAX_EVENTS = 8;

export class WarningsView {
  /** @type {(cmd: string, params?: object) => Promise<any>} */
  #send;
  /** @type {{ openTrack: (id: number) => void, openCampaign: (id: number) => void, openEvent: (id: number) => void }} */
  #open;
  /** @type {GeneralWarning[]} */
  #general = [];
  /** @type {ValidityWarning[]} */
  #validity = [];
  /** Els requadres de la fitxa i de l'editor que hi ha a la pàgina. @type {Set<{ node: HTMLElement, subject: 'track' | 'campaign', id: number }>} */
  #boxes = new Set();
  /** La llista de la finestra, si és oberta. @type {HTMLElement | null} */
  #dialogList = null;
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  #timer;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {{ openLog: () => void, openTrack: (id: number) => void, openCampaign: (id: number) => void, openEvent: (id: number) => void }} open
   */
  constructor(send, open) {
    this.#send = send;
    this.#open = open;
    $('status-warnings').addEventListener('click', () => this.openDialog());
    this.#render();
  }

  /**
   * Torna a demanar les incidències obertes i els avisos de vigència. Diversos canvis seguits (p. ex. desar un esdeveniment
   * avisa de diverses coses) en fan una sola petició.
   */
  refresh() {
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.#load(), 300);
  }

  async #load() {
    // Si una de les dues no es pot llegir (sense connexió), aquella es queda com estava
    const [incidents, validity] = await Promise.allSettled([this.#send('incidents.list', { open: true }), this.#send('validity.list')]);
    if (incidents.status === 'fulfilled') {
      this.#general = incidents.value.incidents.map((/** @type {{ severity: GeneralWarning['level'], text: string }} */ i) =>
        ({ level: i.severity, text: i.text }));
    }
    if (validity.status === 'fulfilled') this.#validity = validity.value.warnings;
    this.#render();
  }

  /** Sense connexió amb el motor: no se sap res (ja ho diu la capçalera). */
  clear() {
    this.#general = [];
    this.#validity = [];
    this.#render();
  }

  #render() {
    const items = allWarnings(this.#general, this.#validity);
    const bar = barState(items);
    const target = $('status-warnings');
    target.replaceChildren(statusLabel(bar.className, bar.icon, bar.text));
    target.title = items.length > 0 ? 'Clica per veure els avisos' : '';
    this.#renderBoxes();
    if (this.#dialogList?.isConnected) this.#renderList(this.#dialogList);
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
    const list = warningsFor(this.#validity, subject, id);
    node.classList.toggle('d-none', list.length === 0);
    node.replaceChildren(...list.map((w) => {
      const kind = kindInfo(w.kind);
      const row = el('div', 'vw-box-row');
      row.append(statusLabel(kind.className, kind.icon, `${kind.label}: ${w.text}`));
      if (w.canRemove) row.append(this.#removeButton(w));
      return row;
    }));
  }

  /** La finestra amb tots els avisos, per gravetat. */
  async openDialog() {
    const list = el('div', 'vw-list');
    this.#dialogList = list;
    this.#renderList(list);
    this.refresh(); // per si de cas ha canviat alguna cosa (p. ex. ha passat la mitjanit)
    // El registre (D-167): també les incidències que ja s'han resolt, les d'un moment, els canvis i les sessions
    const history = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
    history.type = 'button';
    history.append(icon('bi-journal-text'), ' Obre el registre…');
    history.title = 'La pestanya «Registre»: tot el que ha passat, també el que ja s\'ha resolt';
    history.addEventListener('click', () => this.#leave(list, () => this.#open.openLog()));
    const tools = el('div', 'vw-tools');
    tools.append(history);
    await formDialog({ title: 'Avisos', icon: 'bi-exclamation-triangle', body: [tools, list], wide: true });
    this.#dialogList = null;
  }

  /** @param {HTMLElement} list */
  #renderList(list) {
    const items = allWarnings(this.#general, this.#validity);
    if (items.length === 0) {
      list.replaceChildren(statusLabel('text-success', 'bi-check-circle', 'Cap avís.'));
      return;
    }
    list.replaceChildren(...items.map((item) => {
      if ('general' in item) {
        const level = levelInfo(item.level);
        const row = el('div', 'vw-item');
        row.append(statusLabel(level.className, level.icon, item.general.text));
        return row;
      }
      return this.#validityItem(list, item.validity);
    }));
  }

  /** Un avís de vigència a la finestra. @param {HTMLElement} list @param {ValidityWarning} w */
  #validityItem(list, w) {
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

  /** Treu-la dels esdeveniments actius (D-146) i torna a demanar els avisos. @param {ValidityWarning} w */
  async #remove(w) {
    const where = w.events.length === 1 ? `l'esdeveniment «${w.events[0].name}»` : `${w.events.length} esdeveniments actius`;
    await removeFromEvents(this.#send, { subject: w.subject, id: w.id, name: w.name, where });
    this.refresh();
  }
}
