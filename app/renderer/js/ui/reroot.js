// @ts-check
// Canvi d'arrel de les rutes (fase 3, pas 7d; D-025, D-092): canviar el principi de la ruta de moltes pistes alhora.
// Primer «Comprova» (el motor diu quantes pistes canviarien, quantes trobarien el fitxer i n'ensenya exemples) i després
// «Canvia les rutes» (tot en una transacció). Ajuda a triar l'arrel: la llista de carpetes on falten fitxers.

import { $, el, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';

export class RerootView {
  #send;
  #chooseFolder;
  /** La comprovació vàlida per als camps d'ara (si es canvien, s'ha de tornar a comprovar). @type {any} */
  #preview = null;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(defaultPath?: string) => Promise<string | null>} chooseFolder
   */
  constructor(send, chooseFolder) {
    this.#send = send;
    this.#chooseFolder = chooseFolder;
    $('tm-reroot').addEventListener('click', () => this.#open());
    // «Tria…» per a les dues arrels (la d'ara, si encara existeix: p. ex. una carpeta copiada a un altre lloc)
    for (const [button, field] of [['reroot-choose', 'reroot-to'], ['reroot-choose-from', 'reroot-from']]) {
      $(button).addEventListener('click', async () => {
        const input = /** @type {HTMLInputElement} */ ($(field));
        const folder = await this.#chooseFolder(input.value || undefined);
        if (folder) {
          input.value = folder;
          this.#invalidate();
        }
      });
    }
    for (const id of ['reroot-from', 'reroot-to']) $(id).addEventListener('input', () => this.#invalidate());
    $('reroot-only-found').addEventListener('change', () => this.#renderPreview());
    $('reroot-check').addEventListener('click', () => this.#check());
    $('reroot-apply').addEventListener('click', () => this.#apply());
  }

  async #open() {
    /** @type {HTMLInputElement} */ ($('reroot-from')).value = '';
    /** @type {HTMLInputElement} */ ($('reroot-to')).value = '';
    this.#invalidate();
    await this.#loadMissing();
    /** @type {HTMLDialogElement} */ ($('reroot-dialog')).showModal();
  }

  /** Les carpetes on falten fitxers: en fer-hi clic, omplen l'arrel d'ara. */
  async #loadMissing() {
    const list = $('reroot-missing');
    try {
      const { folders } = await this.#send('library.missingFolders');
      list.replaceChildren(...(folders.length === 0
        ? [el('li', 'text-body-secondary', 'Cap: no falta cap fitxer.')]
        : folders.map((/** @type {any} */ f) => {
          const li = el('li', '');
          const b = el('button', 'btn btn-link btn-sm p-0 text-start font-monospace');
          b.type = 'button';
          b.textContent = f.folder;
          b.title = 'Fes servir aquesta carpeta com a arrel d\'ara (es pot escurçar)';
          b.addEventListener('click', () => {
            /** @type {HTMLInputElement} */ ($('reroot-from')).value = f.folder;
            this.#invalidate();
          });
          li.append(b, el('span', 'text-body-secondary', ` · ${f.count} ${f.count === 1 ? 'pista' : 'pistes'}`));
          return li;
        })));
    } catch (err) {
      list.replaceChildren(el('li', 'text-warning', /** @type {Error} */ (err).message));
    }
  }

  /** Els camps han canviat: cal tornar a comprovar abans de canviar res. «Comprova», només amb les dues arrels escrites. */
  #invalidate() {
    this.#preview = null;
    $('reroot-preview').replaceChildren();
    $('reroot-problem').textContent = '';
    /** @type {HTMLButtonElement} */ ($('reroot-apply')).disabled = true;
    const { from, to } = this.#params();
    const missing = !from && !to ? 'Escriu o tria les dues arrels.'
      : !from ? 'Falta l\'arrel d\'ara: escriu-la, tria-la o fes clic en una carpeta de la llista.'
        : !to ? 'Falta l\'arrel nova.' : '';
    $('reroot-hint').textContent = missing;
    /** @type {HTMLButtonElement} */ ($('reroot-check')).disabled = !!missing;
  }

  #params() {
    return {
      from: /** @type {HTMLInputElement} */ ($('reroot-from')).value.trim(),
      to: /** @type {HTMLInputElement} */ ($('reroot-to')).value.trim(),
      onlyFound: /** @type {HTMLInputElement} */ ($('reroot-only-found')).checked,
    };
  }

  async #check() {
    this.#invalidate();
    $('reroot-preview').replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Comprovant els fitxers…'));
    try {
      this.#preview = await this.#send('library.rerootPreview', this.#params());
      this.#renderPreview();
    } catch (err) {
      $('reroot-preview').replaceChildren();
      $('reroot-problem').textContent = /** @type {Error} */ (err).message;
    }
  }

  #renderPreview() {
    const p = this.#preview;
    if (!p) return;
    const onlyFound = this.#params().onlyFound;
    const willChange = onlyFound ? p.found : p.found + p.notFound;
    const lines = [
      el('div', '', `${p.tracks} ${p.tracks === 1 ? 'pista comença' : 'pistes comencen'} per l'arrel d'ara.`),
      statusLabel('text-success', 'bi-check-circle-fill', `${p.found} trobaran el fitxer amb la ruta nova`),
      statusLabel('text-warning', 'bi-exclamation-triangle-fill',
        `${p.notFound} no el trobaran${onlyFound ? ' (no es canviaran)' : ' (es canviaran igualment i continuaran faltant)'}`),
    ];
    if (p.conflicts > 0) lines.push(statusLabel('text-body-secondary', 'bi-slash-circle', `${p.conflicts} xoquen amb una altra pista que ja té la ruta nova (no es tocaran)`));
    lines.push(el('div', 'fw-semibold mt-1', `Es canviaran: ${willChange}`));
    const examples = el('ul', 'reroot-examples');
    examples.append(...p.examples.map((/** @type {any} */ e) => {
      const li = el('li', e.conflict ? 'text-body-secondary' : e.exists ? '' : 'text-warning', e.to);
      li.title = `Ara: ${e.from}${e.conflict ? '\nXoca amb una altra pista' : e.exists ? '' : '\nEl fitxer no hi és'}`;
      return li;
    }));
    const details = /** @type {HTMLDetailsElement} */ (el('details', ''));
    details.append(el('summary', '', 'Exemples (rutes noves)'), examples);
    lines.push(details);
    $('reroot-preview').replaceChildren(...lines);
    /** @type {HTMLButtonElement} */ ($('reroot-apply')).disabled = willChange === 0;
  }

  async #apply() {
    const p = this.#preview;
    if (!p) return;
    const params = this.#params();
    const count = params.onlyFound ? p.found : p.found + p.notFound;
    const dialog = /** @type {HTMLDialogElement} */ ($('reroot-dialog'));
    dialog.close();
    const ok = await confirmAction({
      title: 'Canviar les rutes',
      message: `Es canviarà la ruta de ${count} ${count === 1 ? 'pista' : 'pistes'}: «${p.from}» passa a ser «${p.to}». ` +
        'La fitxa, els punts i l\'historial es conserven, i els fitxers no es toquen.',
      confirm: 'Canvia les rutes',
      danger: false,
    });
    if (!ok) {
      dialog.showModal();
      return;
    }
    try {
      const r = await this.#send('library.reroot', params);
      await confirmAction({
        title: 'Rutes canviades',
        message: `${r.changed} ${r.changed === 1 ? 'pista té' : 'pistes tenen'} la ruta nova (${r.changedFound} amb el fitxer).` +
          (r.foldersMoved ? ` ${r.foldersMoved} ${r.foldersMoved === 1 ? 'carpeta vigilada ha passat' : 'carpetes vigilades han passat'} a l'arrel nova.` : ''),
        confirm: 'D\'acord',
        info: true,
      });
    } catch (err) {
      await confirmAction({ title: 'No s\'han pogut canviar les rutes', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }
}
