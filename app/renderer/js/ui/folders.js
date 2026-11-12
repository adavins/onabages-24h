// @ts-check
// Carpetes vigilades a la Biblioteca (fase 3, pas 7c-2; D-090): el diàleg de la carpeta d'una subcategoria (carpeta,
// bossa renovable i com queden les cançons noves), el seu estat i el registre. L'estat de totes les carpetes arriba amb
// folders.status / folders.changed i es fa servir a l'arbre (marques) i a la barra d'estat (avisos).

import { $, el, statusLabel } from './dom.js';
import { confirmAction } from './confirm.js';
import { typeName } from '../format.js';
import { TRACK_TYPES, parseTags } from '../track-form.js';
import { folderAction } from '../folders-text.js';

const RENEWABLE_ON = 'Les cançons que surtin de la carpeta s\'esborraran de la biblioteca (l\'historial les conserva); les que facin ' +
  'servir esdeveniments no es toquen. Per a bosses que es renoven, com la d\'actualitat.';
const RENEWABLE_OFF = 'Si una cançó desapareix de la carpeta, la pista es queda i surt com a «el fitxer falta», amb un avís.';

export class FoldersView {
  #send;
  #chooseFolder;
  /** @type {any[]} */
  #status = [];
  /** @type {((folders: any[]) => void)[]} */
  #listeners = [];
  /** La subcategoria del diàleg obert. @type {{ id: number, name: string } | null} */
  #open = null;
  /** S'ha demanat «Revisa ara» i encara no ha arribat el resultat (es mostra «Revisant…»). */
  #scanning = false;

  /**
   * @param {(cmd: string, params?: object) => Promise<any>} send
   * @param {(defaultPath?: string) => Promise<string | null>} chooseFolder
   */
  constructor(send, chooseFolder) {
    this.#send = send;
    this.#chooseFolder = chooseFolder;
    $('folder-choose').addEventListener('click', async () => {
      const input = /** @type {HTMLInputElement} */ ($('folder-path'));
      const folder = await this.#chooseFolder(input.value || undefined);
      if (folder) input.value = folder;
    });
    $('folder-clear').addEventListener('click', () => {
      /** @type {HTMLInputElement} */ ($('folder-path')).value = '';
    });
    $('folder-renewable').addEventListener('change', () => this.#renewableHelp());
    $('folder-scan').addEventListener('click', async () => {
      try {
        await this.#send('folders.scan');
        this.#scanning = true;
        $('folder-problem').replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Revisant…'));
      } catch (err) {
        $('folder-problem').textContent = /** @type {Error} */ (err).message;
      }
    });
    $('folder-type').replaceChildren(...TRACK_TYPES.map((id) => option(id, typeName(id))));
  }

  /** Avisa quan canvia l'estat de les carpetes (l'arbre i la barra d'estat). @param {(folders: any[]) => void} listener */
  onChange(listener) {
    this.#listeners.push(listener);
  }

  /** L'estat de la carpeta d'una subcategoria, si en té. @param {number} subcategoryId */
  statusOf(subcategoryId) {
    return this.#status.find((f) => f.subcategoryId === subcategoryId);
  }

  /** En connectar-se. */
  async refresh() {
    try {
      this.update((await this.#send('folders.status')).folders);
    } catch { /* sense connexió */ }
  }

  /** @param {any[]} folders  de folders.status o folders.changed */
  update(folders) {
    this.#status = folders ?? [];
    for (const listener of this.#listeners) listener(this.#status);
    if (this.#open) {
      this.#renderStatus();
      this.#loadLog();
      // Ha arribat el resultat de «Revisa ara»: es treu «Revisant…» (l'estat i el registre ja diuen què ha trobat)
      if (this.#scanning) {
        this.#scanning = false;
        $('folder-problem').replaceChildren(statusLabel('text-success', 'bi-check-lg', 'Revisada ara mateix.'));
      }
    }
  }

  /** Obre el diàleg de la carpeta d'una subcategoria. @param {{ id: number, name: string }} subcategory */
  async show(subcategory) {
    let settings;
    try {
      settings = (await this.#send('folders.get', { subcategoryId: subcategory.id })).folder;
    } catch (err) {
      await confirmAction({ title: 'Carpeta vigilada', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
      return;
    }
    this.#open = { id: subcategory.id, name: settings.name };
    this.#scanning = false;
    $('folder-name').textContent = settings.name;
    /** @type {HTMLInputElement} */ ($('folder-path')).value = settings.folder ?? '';
    /** @type {HTMLInputElement} */ ($('folder-renewable')).checked = settings.renewable;
    /** @type {HTMLSelectElement} */ ($('folder-type')).value = settings.type;
    /** @type {HTMLInputElement} */ ($('folder-tags')).value = (settings.tags ?? []).join(', ');
    /** @type {HTMLInputElement} */ ($('folder-variable')).checked = settings.variableDuration;
    $('folder-problem').textContent = '';
    this.#renewableHelp();
    this.#renderStatus();
    await this.#loadLog();

    const dialog = /** @type {HTMLDialogElement} */ ($('folder-dialog'));
    dialog.returnValue = '';
    dialog.showModal();
    for (;;) {
      const choice = await new Promise((resolve) => dialog.addEventListener('close', () => resolve(dialog.returnValue), { once: true }));
      if (choice === 'renew') {
        const open = /** @type {{ id: number, name: string }} */ (this.#open);
        this.#open = null;
        await this.#renew(open, settings);
        return;
      }
      if (choice !== 'save' || (await this.#save(settings))) break;
      dialog.returnValue = '';
      dialog.showModal(); // no s'ha pogut desar: el diàleg torna a sortir amb el motiu
    }
    this.#open = null;
  }

  /**
   * «Renova la bossa» (D-091): es tria la carpeta nova, el motor diu què es mantindrà, què entrarà i què sortirà, i en
   * confirmar-ho ho fa en segon pla (el progrés surt a la Biblioteca, com una importació).
   * @param {{ id: number, name: string }} sub @param {any} settings  la configuració de la carpeta vigilada
   */
  async #renew(sub, settings) {
    const input = /** @type {HTMLInputElement} */ ($('renew-folder'));
    const ok = /** @type {HTMLButtonElement} */ ($('renew-ok'));
    $('renew-name').textContent = sub.name;
    input.value = '';
    ok.disabled = true;
    $('renew-preview').replaceChildren();
    $('renew-problem').textContent = '';
    /** @type {HTMLInputElement} */ ($('renew-watch')).checked = !!settings.folder;
    /** @type {any} */
    let preview = null;

    $('renew-choose').onclick = async () => {
      const folder = await this.#chooseFolder(input.value || settings.folder || undefined);
      if (!folder) return;
      input.value = folder;
      ok.disabled = true;
      $('renew-problem').textContent = '';
      $('renew-preview').replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Comparant la carpeta nova amb la bossa d\'ara…'));
      try {
        preview = await this.#send('folders.renewPreview', { subcategoryId: sub.id, folder });
        renderPreview(preview);
        ok.disabled = preview.files === 0;
        if (preview.files === 0) $('renew-problem').textContent = 'La carpeta no té cap fitxer d\'àudio: no es pot renovar amb aquesta.';
      } catch (err) {
        preview = null;
        $('renew-preview').replaceChildren();
        $('renew-problem').textContent = /** @type {Error} */ (err).message;
      }
    };

    const dialog = /** @type {HTMLDialogElement} */ ($('renew-dialog'));
    dialog.returnValue = '';
    dialog.showModal();
    const choice = await new Promise((resolve) => dialog.addEventListener('close', () => resolve(dialog.returnValue), { once: true }));
    if (choice !== 'ok' || !preview) return;

    if (preview.leave > 0 && !(await confirmAction({
      title: 'Renovar la bossa',
      message: `${preview.leave} ${preview.leave === 1 ? 'cançó sortirà' : 'cançons sortiran'} de «${sub.name}» i s'esborraran de la biblioteca ` +
        '(l\'historial les conserva; els fitxers no es toquen). No es pot desfer.',
      confirm: 'Renova',
    }))) return;

    try {
      await this.#send('folders.renew', {
        subcategoryId: sub.id, folder: input.value, watch: /** @type {HTMLInputElement} */ ($('renew-watch')).checked,
      });
      // El progrés i l'informe surten al requadre d'importació de la Biblioteca (library.importProgress)
    } catch (err) {
      await confirmAction({ title: 'No s\'ha pogut renovar la bossa', message: /** @type {Error} */ (err).message, confirm: 'D\'acord', info: true });
    }
  }

  /** @param {any} before  la configuració d'abans (per confirmar els canvis delicats) */
  async #save(before) {
    const folder = /** @type {HTMLInputElement} */ ($('folder-path')).value.trim();
    const renewable = /** @type {HTMLInputElement} */ ($('folder-renewable')).checked;
    const open = /** @type {{ id: number, name: string }} */ (this.#open);
    // Fer-la renovable (o canviar-ne la carpeta si ja ho és) pot esborrar pistes: es confirma
    if (folder && renewable && (!before.renewable || folder !== before.folder)) {
      const ok = await confirmAction({
        title: 'Bossa renovable',
        message: `A partir d'ara, les pistes de «${open.name}» que no siguin a la carpeta (o que en surtin) s'esborraran de la ` +
          'biblioteca a la revisió següent. Les que fan servir esdeveniments no es toquen. Si la carpeta surt buida o no es ' +
          'troba, no s\'esborra res.',
        confirm: 'Desa-ho',
      });
      if (!ok) return true; // no es desa, però el diàleg es tanca
    }
    try {
      await this.#send('folders.set', {
        subcategoryId: open.id,
        folder: folder || null,
        renewable,
        type: /** @type {HTMLSelectElement} */ ($('folder-type')).value,
        tags: parseTags(/** @type {HTMLInputElement} */ ($('folder-tags')).value),
        variableDuration: /** @type {HTMLInputElement} */ ($('folder-variable')).checked,
      });
      return true;
    } catch (err) {
      $('folder-problem').textContent = /** @type {Error} */ (err).message;
      return false;
    }
  }

  #renewableHelp() {
    $('folder-renewable-help').textContent = /** @type {HTMLInputElement} */ ($('folder-renewable')).checked ? RENEWABLE_ON : RENEWABLE_OFF;
  }

  #renderStatus() {
    const box = $('folder-status');
    const s = this.#open ? this.statusOf(this.#open.id) : null;
    if (!s) {
      box.replaceChildren(statusLabel('text-body-secondary', 'bi-dash-circle', 'No es vigila cap carpeta (o encara no s\'ha revisat).'));
      return;
    }
    const when = s.lastScan ? new Date(s.lastScan).toLocaleTimeString('ca-ES', { hour: '2-digit', minute: '2-digit' }) : '—';
    const label = s.problem
      ? statusLabel('text-warning', 'bi-exclamation-triangle-fill', s.problem)
      : s.missing > 0
        ? statusLabel('text-warning', 'bi-exclamation-triangle-fill', `${s.missing} ${s.missing === 1 ? 'pista té' : 'pistes tenen'} el fitxer que falta`)
        : statusLabel('text-success', 'bi-check-circle-fill', 'Tot al dia');
    box.replaceChildren(label, el('div', 'form-text', `Última revisió: ${when} · ${s.files} fitxers a la carpeta · ${s.tracks} pistes`));
  }

  async #loadLog() {
    const list = $('folder-log');
    if (!this.#open) return;
    try {
      const { entries } = await this.#send('folders.log', { subcategoryId: this.#open.id, limit: 30 });
      list.replaceChildren(...(entries.length === 0
        ? [el('li', 'text-body-secondary', 'Encara no hi ha cap canvi.')]
        : entries.map((/** @type {any} */ e) => {
          const a = folderAction(e.action);
          const li = el('li', '');
          const time = new Date(e.time).toLocaleString('ca-ES', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
          li.append(el('span', 'folder-log-time', time), ' ', statusLabel(a.css, a.icon, a.text), ' ',
            el('span', 'folder-log-path', fileName(e.path)));
          li.title = `${e.path}${e.detail ? `\n${e.detail}` : ''}`;
          if (e.detail) li.append(el('span', 'folder-log-detail', ` · ${e.detail}`));
          return li;
        })));
    } catch { /* sense connexió */ }
  }
}

/** La previsualització de «Renova la bossa»: què es manté, què entra i què surt, amb les llistes plegades. @param {any} p */
function renderPreview(p) {
  /** @param {string} css @param {string} iconName @param {string} title @param {string[]} names @param {number} count */
  const block = (css, iconName, title, names, count) => {
    const details = /** @type {HTMLDetailsElement} */ (el('details', 'renew-block'));
    const summary = el('summary', '');
    summary.append(statusLabel(css, iconName, `${title}: ${count}`));
    const list = el('ul', 'renew-list');
    list.append(...names.map((n) => el('li', '', n)));
    if (count > names.length) list.append(el('li', 'text-body-secondary', `… i ${count - names.length} més`));
    details.append(summary, list);
    return details;
  };
  const parts = [
    block('text-success', 'bi-check2-circle', 'Es mantenen (amb la fitxa i l\'historial)', p.keepNames, p.keep),
    block('text-info', 'bi-plus-circle', 'Entren', p.enterNames, p.enter),
    block('text-warning', 'bi-dash-circle', 'Surten (s\'esborraran)', p.leaveNames, p.leave),
  ];
  if (p.leaveInEvents > 0)
    parts.push(block('text-body-secondary', 'bi-calendar-check', 'Surten però les fan servir esdeveniments (no es toquen)', p.leaveInEventsNames, p.leaveInEvents));
  if (p.elsewhere > 0)
    parts.push(el('div', 'form-text', `${p.elsewhere} fitxers de la carpeta ja són a la biblioteca en una altra subcategoria: no es toquen.`));
  $('renew-preview').replaceChildren(...parts);
}

/** @param {string} path */
function fileName(path) {
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
