// @ts-check
// Finestra d'un informe (fase 4, pas 4b; D-138): l'informe de programació o el certificat d'emissió d'una campanya o d'una
// pista. A dalt, el període (per defecte, el de defaultPeriod) i «Desa en PDF…»; a sota, la vista prèvia del document tal
// com sortirà al PDF. Les dades les dona el motor (reports.*, station.get); el PDF el fa el procés principal.

import { el, icon, statusLabel } from './dom.js';
import { programmingHtml, certificateHtml, suggestedName, defaultPeriod } from '../report-html.js';
import { todayIso } from '../event-form.js';

/**
 * @param {{
 *   send: (cmd: string, params?: object) => Promise<any>,
 *   savePdf: (html: string, name: string) => Promise<{ path?: string, error?: string } | null>,
 *   openStation: () => Promise<void>,
 *   kind: 'programming' | 'certificate',
 *   target: { campaignId: number } | { trackId: number },
 *   subject: { name: string, startDate?: string, endDate?: string, validFrom?: string | null, validUntil?: string | null },
 * }} options  openStation: obre la finestra de les dades de l'emissora
 */
export function openReport({ send, savePdf, openStation, kind, target, subject }) {
  const title = kind === 'programming' ? 'Informe de programació' : 'Certificat d\'emissió';
  const period = defaultPeriod(kind, subject, todayIso());

  const dialog = /** @type {HTMLDialogElement} */ (el('dialog', 'ob-dialog rp-dialog'));
  const head = el('div', 'rp-dialog-head');
  const h = el('h2', 'h5 m-0');
  h.append(icon(kind === 'programming' ? 'bi-calendar-range' : 'bi-patch-check'), ` ${title} · `, el('span', 'text-body-secondary', subject.name));
  head.append(h);

  // El període i els botons
  const tools = el('div', 'rp-dialog-tools');
  const date = (/** @type {string} */ label, /** @type {string} */ value) => {
    const wrap = el('label', 'rp-dialog-date');
    const input = /** @type {HTMLInputElement} */ (el('input', 'form-control form-control-sm'));
    input.type = 'date';
    input.value = value;
    wrap.append(el('span', '', label), input);
    return { wrap, input };
  };
  const from = date('Des de', period.from);
  const to = date('Fins a', period.to);
  const state = el('span', 'rp-dialog-state form-text m-0');
  state.setAttribute('aria-live', 'polite');
  const station = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  station.type = 'button';
  station.append(icon('bi-gear'), ' Dades de l\'emissora…');
  station.title = 'El nom, la freqüència i el logotip de la capçalera';
  const save = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-primary'));
  save.type = 'button';
  save.append(icon('bi-file-earmark-pdf'), ' Desa en PDF…');
  save.disabled = true;
  const close = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
  close.type = 'button';
  close.append(icon('bi-x-lg'), ' Tanca');
  close.addEventListener('click', () => dialog.close());
  // Al certificat, la comparació amb el previst és opcional i comença desactivada (D-141)
  const compare = /** @type {HTMLInputElement} */ (el('input', 'form-check-input'));
  compare.type = 'checkbox';
  compare.id = `rp-compare-${Date.now()}`;
  const compareWrap = el('div', 'form-check m-0 rp-dialog-compare');
  const compareLabel = /** @type {HTMLLabelElement} */ (el('label', 'form-check-label', 'Compara amb el previst'));
  compareLabel.htmlFor = compare.id;
  compareLabel.title = 'Afegeix quants passis estaven previstos, quants han sonat i el motiu dels que no';
  compareWrap.append(compare, compareLabel);
  tools.append(from.wrap, to.wrap, ...(kind === 'certificate' ? [compareWrap] : []), state, el('span', 'ms-auto'), station, save, close);

  const notice = el('div', 'rp-dialog-notice');
  // La vista prèvia: el document en blanc, com un full
  const preview = el('div', 'rp-preview');
  const page = el('div', 'rp-page');
  preview.append(page);
  dialog.append(head, tools, notice, preview);
  document.body.append(dialog);

  /** L'informe que es veu (per desar-lo tal qual). @type {{ html: string, name: string } | null} */
  let current = null;
  let token = 0;

  const load = async () => {
    const mine = ++token;
    save.disabled = true;
    current = null;
    if (!from.input.value || !to.input.value) {
      state.textContent = 'Tria les dues dates.';
      return;
    }
    state.replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Preparant l\'informe…'));
    try {
      const params = { ...target, from: from.input.value, to: to.input.value, ...(kind === 'certificate' && compare.checked ? { compare: true } : {}) };
      const [data, st] = await Promise.all([
        send(kind === 'programming' ? 'reports.programming' : 'reports.certificate', params),
        send('station.get'),
      ]);
      if (mine !== token) return;
      const stationData = st.station;
      const html = kind === 'programming' ? programmingHtml(data, stationData, todayIso()) : certificateHtml(data, stationData, todayIso());
      page.innerHTML = html; // tots els textos de les dades ja van escapats (report-html.js)
      current = { html, name: suggestedName(kind, data) };
      save.disabled = false;
      state.textContent = kind === 'programming' ? `${data.total} ${data.total === 1 ? 'passi previst' : 'passis previstos'}` : `${data.total} ${data.total === 1 ? 'emissió' : 'emissions'}`;
      notice.replaceChildren(...(!stationData.name && !stationData.logo
        ? [statusLabel('text-warning small', 'bi-exclamation-triangle-fill', 'Encara no hi ha les dades de l\'emissora: la capçalera sortirà sense nom ni logotip. Prem «Dades de l\'emissora…».')]
        : []));
    } catch (err) {
      if (mine !== token) return;
      page.replaceChildren();
      state.replaceChildren(statusLabel('text-danger', 'bi-x-circle', /** @type {Error} */ (err).message));
    }
  };

  from.input.addEventListener('change', load);
  to.input.addEventListener('change', load);
  compare.addEventListener('change', load);
  station.addEventListener('click', async () => {
    await openStation();
    await load();
  });
  save.addEventListener('click', async () => {
    if (!current) return;
    save.disabled = true;
    state.replaceChildren(statusLabel('text-info', 'bi-hourglass-split', 'Fent el PDF…'));
    const result = await savePdf(current.html, current.name);
    save.disabled = false;
    if (!result) state.textContent = 'No s\'ha desat (cancel·lat).';
    else if (result.error) state.replaceChildren(statusLabel('text-danger', 'bi-x-circle', result.error));
    else state.replaceChildren(statusLabel('text-success', 'bi-check-circle-fill', `Desat: ${result.path}`));
  });

  dialog.addEventListener('close', () => dialog.remove(), { once: true });
  dialog.showModal();
  load();
}
