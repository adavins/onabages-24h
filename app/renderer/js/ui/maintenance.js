// @ts-check
// Pantalla «Manteniment» (D-194), al menú «Més»: el que s'ha anat acumulant a la programació i ja no sonarà, amb què es
// proposa fer-ne. «Analitza» ho demana al motor (maintenance.scan, només llegeix); les propostes surten agrupades per cas,
// amb una casella cadascuna (totes desmarcades), el motiu i l'acció. «Aplica les marcades…» demana confirmació i ho fa
// el motor (maintenance.apply), que abans fa una còpia de seguretat. Mai no es fa res sol.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { groupFindings, actionInfo, applySummary, resultText } from '../maintenance-form.js';

/**
 * @typedef {import('../maintenance-form.js').Finding} Finding
 * @typedef {{ openEvent: (id: number) => void, openTrack: (id: number) => void, openCampaign: (id: number) => void }} MaintenanceLinks
 */

export class MaintenanceView {
  /** @type {(cmd: string, params?: object) => Promise<any>} */
  #send;
  #links;
  /** @type {Finding[] | null} null = encara no s'ha analitzat */
  #findings = null;
  /** Les claus marcades. @type {Set<string>} */
  #checked = new Set();
  #busy = false;
  #status = el('span', 'mt-status');
  #list = el('div', 'mt-list');
  #count = el('span', 'mt-count');
  #apply = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-danger'));
  #analyze = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-primary'));

  /** @param {(cmd: string, params?: object) => Promise<any>} send @param {MaintenanceLinks} links */
  constructor(send, links) {
    this.#send = send;
    this.#links = links;
    const page = el('div', 'mt-page');
    const head = el('div', 'mt-head');
    const title = el('h2', 'h5 mb-1');
    title.append(icon('bi-wrench-adjustable'), ' Manteniment de la programació');
    const intro = el('p', 'form-text mb-2',
      'Busca el que s\'ha anat acumulant i ja no sonarà (esdeveniments acabats, duplicats, pistes caducades…) i proposa què fer-ne. '
      + 'No es fa res fins que marques les propostes i les apliques; abans de canviar res, es fa una còpia de seguretat.');
    this.#analyze.type = 'button';
    this.#analyze.append(icon('bi-search'), ' Analitza');
    this.#analyze.addEventListener('click', () => this.#scan());
    const tools = el('div', 'mt-tools');
    tools.append(this.#analyze, this.#status);
    head.append(title, intro, tools);

    const foot = el('div', 'mt-foot');
    this.#apply.type = 'button';
    this.#apply.append(icon('bi-check2-square'), ' Aplica les marcades…');
    this.#apply.addEventListener('click', () => this.#applyChecked());
    foot.append(this.#count, this.#apply);

    page.append(head, this.#list, foot);
    $('view-manteniment').append(page);
    this.#render();
  }

  /** La pantalla es mostra: la primera vegada, s'analitza sola. */
  show() {
    if (this.#findings === null && !this.#busy) this.#scan();
  }

  hide() {}

  async #scan() {
    if (this.#busy) return;
    this.#busy = true;
    this.#status.replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Analitzant…'));
    this.#syncButtons();
    try {
      const result = await this.#send('maintenance.scan');
      this.#findings = result.findings;
      // Es conserven les marques de les que encara hi són
      const keys = new Set(this.#findings?.map((f) => f.key));
      for (const key of [...this.#checked]) if (!keys.has(key)) this.#checked.delete(key);
      this.#status.replaceChildren(statusLabel('text-body-secondary', 'bi-clock', `Analitzat a les ${new Date().toLocaleTimeString('ca-ES', { hour: '2-digit', minute: '2-digit' })}`));
    } catch (err) {
      this.#status.replaceChildren(statusLabel('text-danger', 'bi-x-octagon-fill', `No s'ha pogut analitzar: ${/** @type {Error} */ (err).message}`));
    } finally {
      this.#busy = false;
      this.#render();
    }
  }

  #render() {
    const findings = this.#findings;
    if (findings === null) {
      this.#list.replaceChildren();
    } else if (findings.length === 0) {
      this.#list.replaceChildren(el('div', 'empty-note', ''));
      /** @type {HTMLElement} */ (this.#list.firstElementChild).append(
        statusLabel('text-success', 'bi-check-circle-fill', 'Tot net: no hi ha res a proposar.'));
    } else {
      this.#list.replaceChildren(...groupFindings(findings).map((g) => this.#group(g)));
    }
    this.#syncButtons();
  }

  /** Un cas: el títol (amb una casella per marcar-los tots), què vol dir i les propostes. @param {ReturnType<typeof groupFindings>[number]} g */
  #group(g) {
    const box = el('section', 'mt-group');
    const head = el('div', 'mt-group-head');
    const all = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    all.type = 'checkbox';
    all.id = `mt-all-${g.code}`;
    const marked = g.findings.filter((f) => this.#checked.has(f.key)).length;
    all.checked = marked === g.findings.length;
    all.indeterminate = marked > 0 && marked < g.findings.length;
    all.addEventListener('change', () => {
      for (const f of g.findings) {
        if (all.checked) this.#checked.add(f.key);
        else this.#checked.delete(f.key);
      }
      this.#render();
    });
    const label = /** @type {HTMLLabelElement} */ (el('label', 'mt-group-title'));
    label.htmlFor = all.id;
    label.append(icon(g.icon), ' ', el('span', '', g.title), el('span', 'mt-group-count', ` (${g.findings.length})`));
    head.append(all, label);
    box.append(head, el('div', 'form-text mt-group-help', g.help));
    const list = el('ul', 'mt-items');
    for (const f of g.findings) list.append(this.#item(f));
    box.append(list);
    return box;
  }

  /** Una proposta: la casella, el nom (amb un enllaç per obrir-lo), el motiu i què es farà. @param {Finding} f */
  #item(f) {
    const li = el('li', 'mt-item');
    const check = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
    check.type = 'checkbox';
    check.id = `mt-${f.key}`;
    check.checked = this.#checked.has(f.key);
    check.addEventListener('change', () => {
      if (check.checked) this.#checked.add(f.key);
      else this.#checked.delete(f.key);
      this.#render();
    });
    const body = el('div', 'mt-item-body');
    const name = /** @type {HTMLLabelElement} */ (el('label', 'mt-item-name', f.name));
    name.htmlFor = check.id;
    const line = el('div', 'mt-item-line');
    line.append(name);
    const open = this.#openLink(f);
    if (open) line.append(open);
    const action = actionInfo(f.action);
    line.append(statusLabel(`mt-item-action ${action.css}`, action.icon, action.text));
    body.append(line, el('div', 'mt-item-reason', f.reason));
    li.append(check, body);
    return li;
  }

  /** Un botó per anar-hi (l'esdeveniment al planificador, la pista a la biblioteca, la campanya), o null. @param {Finding} f */
  #openLink(f) {
    /** @type {(() => void) | null} */
    const go = f.subject === 'event' ? () => this.#links.openEvent(f.id)
      : f.subject === 'track' ? () => this.#links.openTrack(f.id)
        : f.subject === 'campaign' ? () => this.#links.openCampaign(f.id) : null;
    if (!go) return null;
    const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-link p-0 mt-item-open'));
    b.type = 'button';
    b.title = f.subject === 'event' ? 'Obre l\'esdeveniment al planificador' : f.subject === 'track' ? 'Obre la fitxa de la pista' : 'Obre la campanya';
    b.append(icon('bi-box-arrow-up-right'), el('span', 'visually-hidden', b.title));
    b.addEventListener('click', go);
    return b;
  }

  #syncButtons() {
    const n = this.#checked.size;
    this.#count.textContent = this.#findings === null || this.#findings.length === 0 ? ''
      : n === 0 ? 'Cap proposta marcada' : `${n} ${n === 1 ? 'proposta marcada' : 'propostes marcades'}`;
    this.#apply.disabled = n === 0 || this.#busy;
    this.#analyze.disabled = this.#busy;
  }

  async #applyChecked() {
    const marked = (this.#findings ?? []).filter((f) => this.#checked.has(f.key));
    if (marked.length === 0 || this.#busy) return;
    const ok = await confirmAction({
      title: 'Aplicar el manteniment',
      message: [...applySummary(marked),
        'Abans es farà una còpia de seguretat («Abans del manteniment»): restaurant-la es desfà tot.'].join('\n'),
      confirm: `Aplica ${marked.length === 1 ? 'la proposta' : `les ${marked.length} propostes`}`,
    });
    if (!ok) return;
    this.#busy = true;
    this.#syncButtons();
    let result = null;
    try {
      result = await this.#send('maintenance.apply', { keys: marked.map((f) => f.key) });
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut aplicar', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    } finally {
      this.#busy = false;
    }
    if (result) {
      this.#checked.clear();
      await confirmAction({ title: result.error ? 'Manteniment a mitges' : 'Manteniment fet', message: resultText(result), confirm: 'D\'acord', info: true });
    }
    await this.#scan();
  }
}
