// @ts-check
// Importar una carpeta des de la pantalla «Biblioteca» (fase 3, pas 7b; D-088): un diàleg per triar la carpeta, la
// subcategoria i com queden les pistes noves; el motor ho fa en segon pla i n'avisa del progrés
// (library.importProgress), que es mostra en un requadre a sobre de la llista de pistes, amb l'informe en acabar.

import { $, el, icon, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { typeName } from '../format.js';
import { TRACK_TYPES, parseTags, guessType } from '../track-form.js';

export class ImportView {
  #send;
  #chooseFolder;
  #context;
  /** @type {any} */
  #status = null;
  #showReport = false;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(defaultPath?: string) => Promise<string | null>} chooseFolder  diàleg de Windows per triar una carpeta
   * @param {() => { categories: any[], subcategoryId: number | null }} context  les categories i la subcategoria triada a l'arbre
   */
  constructor(send, chooseFolder, context) {
    this.#send = send;
    this.#chooseFolder = chooseFolder;
    this.#context = context;
    $('tm-import').addEventListener('click', () => this.#open());
    $('import-choose').addEventListener('click', async () => {
      const input = /** @type {HTMLInputElement} */ ($('import-folder'));
      const folder = await this.#chooseFolder(input.value || undefined);
      if (folder) input.value = folder;
      this.#validate();
    });
    $('import-subcategory').addEventListener('change', () => {
      this.#proposeType();
      this.#validate();
    });
  }

  /** En connectar-se: la importació en curs, si n'hi ha (una d'acabada no torna a sortir si ja s'ha tancat). */
  async refresh() {
    try {
      const status = (await this.#send('library.importStatus')).import;
      if (status && (status.state === 'listing' || status.state === 'running')) this.show(status);
    } catch { /* sense connexió */ }
  }

  /** «S'està important BOSSA 9 (120 de 289)» si hi ha una importació en curs, o null (per avisar en tancar). */
  runningText() {
    const s = this.#status;
    if (!s || (s.state !== 'listing' && s.state !== 'running')) return null;
    return `S'està important «${folderName(s.folder)}»${s.state === 'running' ? ` (${s.done} de ${s.total})` : ''}`;
  }

  /** @param {any} status  de library.importProgress o library.importStatus (docs/protocol.md, 4.10), o null */
  show(status) {
    this.#status = status;
    const panel = $('tm-import-panel');
    if (!status) {
      panel.classList.add('d-none');
      return;
    }
    panel.classList.remove('d-none');
    const running = status.state === 'listing' || status.state === 'running';
    /** @type {Node[]} */
    const parts = [];

    const head = el('div', 'tm-import-head');
    const what = `${folderName(status.folder)} → ${status.subcategory}`;
    if (status.state === 'listing') head.append(statusLabel('text-info', 'bi-hourglass-split', `Buscant els fitxers de ${what}…`));
    else if (status.state === 'running') head.append(statusLabel('text-info', 'bi-arrow-repeat', `Important ${what}: ${status.done} de ${status.total}`));
    else if (status.state === 'done') head.append(statusLabel('text-success', 'bi-check-circle-fill', `Importació acabada: ${what}`));
    else if (status.state === 'cancelled') head.append(statusLabel('text-warning', 'bi-stop-circle-fill', `Importació aturada: ${what} (${status.done} de ${status.total})`));
    else head.append(statusLabel('text-danger', 'bi-x-octagon-fill', `No s'ha pogut importar ${what}: ${status.error ?? ''}`));
    head.title = status.folder;

    const tools = el('span', 'tm-import-tools');
    if (running) {
      const stop = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-warning'));
      stop.type = 'button';
      stop.append(icon('bi-stop-fill'), ' Atura');
      stop.title = 'Atura la importació (el que ja s\'ha importat es queda)';
      stop.addEventListener('click', () => this.#send('library.importCancel').catch(() => {}));
      tools.append(stop);
    } else {
      if (status.unreadableCount > 0) {
        const report = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
        report.type = 'button';
        report.textContent = this.#showReport ? 'Amaga els errors' : 'Mostra els errors';
        report.addEventListener('click', () => {
          this.#showReport = !this.#showReport;
          this.show(this.#status);
        });
        tools.append(report);
      }
      const close = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      close.type = 'button';
      close.textContent = 'Tanca';
      close.addEventListener('click', () => {
        this.#showReport = false;
        panel.classList.add('d-none');
      });
      tools.append(close);
    }
    head.append(tools);
    parts.push(head);

    if (status.state === 'running' && status.total > 0) {
      const bar = el('div', 'tm-import-bar');
      const fill = el('span', 'tm-import-fill');
      fill.style.width = `${Math.round((status.done / status.total) * 100)}%`;
      bar.append(fill);
      parts.push(bar);
    }

    // Resum: noves, ja hi eren, recuperades, sense so i errors
    const counts = [`${status.added} ${status.added === 1 ? 'nova' : 'noves'}`, `${status.alreadyThere} ja hi eren`];
    if (status.reactivated) counts.push(`${status.reactivated} recuperades (el fitxer faltava o s'havia mogut)`);
    if (status.withoutSound) counts.push(`${status.withoutSound} sense so`);
    if (status.removed) counts.push(`${status.removed} esborrades en buidar la subcategoria`);
    if (status.retiredInstead) counts.push(`${status.retiredInstead} retirades (les fan servir esdeveniments)`);
    const summary = el('div', 'tm-import-summary', counts.join(' · '));
    if (status.unreadableCount > 0)
      summary.append(' · ', statusLabel('text-warning', 'bi-exclamation-triangle-fill', `${status.unreadableCount} no es poden llegir`));
    parts.push(summary);
    if (status.warning) parts.push(statusLabel('text-warning', 'bi-exclamation-triangle-fill', status.warning));

    if (this.#showReport && !running && status.unreadableCount > 0) {
      const list = el('ul', 'tm-import-errors');
      list.append(...status.unreadable.map((/** @type {string} */ u) => el('li', '', u)));
      if (status.unreadableCount > status.unreadable.length)
        list.append(el('li', '', `… i ${status.unreadableCount - status.unreadable.length} més (són al registre del motor).`));
      parts.push(list);
    }
    panel.replaceChildren(...parts);
  }

  // --- Diàleg ---

  async #open() {
    if (this.#status && (this.#status.state === 'listing' || this.#status.state === 'running')) {
      await confirmAction({ title: 'Ja s\'està important', message: 'Espera que acabi la importació en curs (o atura-la).', confirm: 'D\'acord', info: true });
      return;
    }
    const { categories, subcategoryId } = this.#context();
    const select = /** @type {HTMLSelectElement} */ ($('import-subcategory'));
    select.replaceChildren(option('', 'Tria la subcategoria…'));
    for (const c of categories) {
      if (c.subcategories.length === 0) continue;
      const group = document.createElement('optgroup');
      group.label = c.name;
      group.append(...c.subcategories.map((/** @type {any} */ s) => option(String(s.id), s.name)));
      select.append(group);
    }
    select.value = subcategoryId == null ? '' : String(subcategoryId);
    const type = /** @type {HTMLSelectElement} */ ($('import-type'));
    type.replaceChildren(...TRACK_TYPES.map((id) => option(id, typeName(id))));
    this.#proposeType();
    /** @type {HTMLInputElement} */ ($('import-tags')).value = '';
    /** @type {HTMLInputElement} */ ($('import-variable')).checked = false;
    /** @type {HTMLInputElement} */ ($('import-empty')).checked = false;
    this.#validate();

    const dialog = /** @type {HTMLDialogElement} */ ($('import-dialog'));
    dialog.returnValue = '';
    dialog.showModal();
    const ok = await new Promise((resolve) => dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true }));
    if (!ok) return;

    const params = {
      folder: /** @type {HTMLInputElement} */ ($('import-folder')).value,
      subcategoryId: Number(select.value),
      type: type.value,
      tags: parseTags(/** @type {HTMLInputElement} */ ($('import-tags')).value),
      variableDuration: /** @type {HTMLInputElement} */ ($('import-variable')).checked,
      emptySubcategory: /** @type {HTMLInputElement} */ ($('import-empty')).checked,
    };
    // Buidar esborra pistes: es confirma, dient de quina subcategoria i quantes n'hi ha ara
    if (params.emptySubcategory) {
      const { categories } = this.#context();
      const category = categories.find((c) => c.subcategories.some((/** @type {any} */ s) => s.id === params.subcategoryId));
      const sub = category?.subcategories.find((/** @type {any} */ s) => s.id === params.subcategoryId);
      const confirmed = await confirmAction({
        title: 'Buidar la subcategoria',
        message: `«${category?.name ?? ''} / ${sub?.name ?? ''}» té ara ${sub?.count ?? 0} pistes. En acabar la importació, les que no ` +
          'siguin a la carpeta s\'esborraran de la biblioteca (les que fan servir esdeveniments quedaran retirades). ' +
          'L\'historial les conserva i els fitxers no es toquen, però no es pot desfer.',
        confirm: 'Importa i buida',
      });
      if (!confirmed) return;
    }
    try {
      this.#showReport = false;
      this.show((await this.#send('library.import', params)).import);
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut començar la importació', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** Proposa el tipus segons la categoria de la subcategoria triada (es pot canviar). */
  #proposeType() {
    const id = Number(/** @type {HTMLSelectElement} */ ($('import-subcategory')).value);
    const category = this.#context().categories.find((c) => c.subcategories.some((/** @type {any} */ s) => s.id === id));
    /** @type {HTMLSelectElement} */ ($('import-type')).value = guessType(category?.name);
  }

  #validate() {
    const folder = /** @type {HTMLInputElement} */ ($('import-folder')).value;
    const sub = /** @type {HTMLSelectElement} */ ($('import-subcategory')).value;
    const problem = !folder ? 'Tria la carpeta.' : !sub ? 'Tria la subcategoria.' : '';
    $('import-problem').textContent = problem;
    /** @type {HTMLButtonElement} */ ($('import-ok')).disabled = !!problem;
  }
}

/** L'últim tros de la ruta («O:\musica\BOSSA 9» → «BOSSA 9»). @param {string} path */
function folderName(path) {
  const parts = path.replace(/[\\/]+$/, '').split(/[\\/]/);
  return parts[parts.length - 1] || path;
}

/** @param {string} value @param {string} text */
function option(value, text) {
  const o = document.createElement('option');
  o.value = value;
  o.textContent = text;
  return o;
}
