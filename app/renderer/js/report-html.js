// @ts-check
// Informe de programació i certificat d'emissió (fase 4, pas 4b; D-138): l'HTML del document a partir del que diu el motor
// (reports.programming, reports.certificate i station.get). No toca la pàgina: es prova amb «npm test»
// (app/tests/report-html.test.mjs). El mateix HTML es veu a la vista prèvia i s'imprimeix a PDF (report-print.html), amb
// l'estil de css/report.css. Tots els textos que vénen de les dades passen per esc().

import { DAY_SHORT, DAY_NAMES } from './calendar-form.js';
import { formatDuration } from './format.js';
import { dmy } from './campaign-form.js';

/** Escapa un text per posar-lo dins de l'HTML. @param {unknown} value */
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c);
}

/** «Ferreteria - Falca A» (o només el títol). @param {{ artist?: string | null, title?: string | null }} t */
function trackName(t) {
  return t.artist ? `${t.artist} - ${t.title ?? ''}` : (t.title ?? '');
}

/** El dia de la setmana d'una data 'AAAA-MM-DD' (0 = dilluns). @param {string} iso */
function weekday(iso) {
  const [y, m, d] = iso.split('-').map(Number);
  return (new Date(Date.UTC(y, m - 1, d)).getUTCDay() + 6) % 7;
}

/** De l'hora local amb zona del motor ('2026-11-02T10:00:05.123+01:00'), el dia i l'hora tal com es van escriure. @param {string} at */
function split(at) {
  return { date: at.slice(0, 10), time: at.slice(11, 19) };
}

/** «1 passi» / «3 passis». @param {number} n */
function passesText(n) {
  return `${n} ${n === 1 ? 'passi' : 'passis'}`;
}

/** Què és l'informe: la campanya o la pista, en una frase curta. @param {any} subject */
export function subjectName(subject) {
  return subject.kind === 'campaign' ? subject.name : trackName(subject);
}

/**
 * La capçalera comuna: el logotip, l'emissora i la freqüència, el títol del document i la data de l'informe.
 * @param {{ name: string, frequency: string, logo: string | null }} station @param {string} title @param {string} today 'AAAA-MM-DD'
 */
function header(station, title, today) {
  const logo = station.logo ? `<img class="rp-logo" src="${esc(station.logo)}" alt="">` : '';
  const name = station.name ? `<div class="rp-station">${esc(station.name)}</div>` : '';
  const frequency = station.frequency ? `<div class="rp-frequency">${esc(station.frequency)}</div>` : '';
  return `<header class="rp-header">${logo}<div class="rp-station-box">${name}${frequency}</div>`
    + `<div class="rp-date">Data de l'informe: ${esc(dmy(today))}</div></header>`
    + `<h1 class="rp-title">${esc(title)}</h1>`;
}

/**
 * Les dades de la campanya o de la pista: nom, anunciant i període.
 * @param {any} subject @param {string} from @param {string} to
 */
function subjectBlock(subject, from, to) {
  const rows = [];
  if (subject.kind === 'campaign') {
    rows.push(['Campanya', subject.name]);
    if (subject.advertiser) rows.push(['Anunciant', subject.advertiser]);
    rows.push(['Dates de la campanya', `del ${dmy(subject.startDate)} al ${dmy(subject.endDate)}`]);
  } else {
    rows.push(['Pista', trackName(subject)]);
    rows.push(['Durada', formatDuration(subject.durationMs)]);
  }
  rows.push(['Període de l\'informe', `del ${dmy(from)} al ${dmy(to)}`]);
  return `<table class="rp-facts"><tbody>${rows.map(([k, v]) => `<tr><th>${esc(k)}</th><td>${esc(v)}</td></tr>`).join('')}</tbody></table>`;
}

/**
 * Informe de programació d'emissions (D-138): les dades, el nombre de passis previstos, les falques (o la pista) amb els
 * seus passis, la graella setmanal amb les franges programades i la llista de cada passi previst.
 * @param {any} data  de reports.programming
 * @param {{ name: string, frequency: string, logo: string | null }} station  de station.get
 * @param {string} today  'AAAA-MM-DD'
 */
export function programmingHtml(data, station, today) {
  const campaign = data.subject.kind === 'campaign';
  const parts = [header(station, 'Informe de programació d\'emissions', today), subjectBlock(data.subject, data.from, data.to)];
  parts.push(`<p class="rp-total">Passis previstos: <strong>${data.total}</strong></p>`);

  // Les falques (o la pista), amb la durada i els passis previstos
  const spots = /** @type {any[]} */ (data.bySpot);
  parts.push('<h2>Falques</h2><table class="rp-table"><thead><tr><th>Falca</th><th class="rp-num">Durada</th><th class="rp-num">Passis previstos</th></tr></thead><tbody>'
    + spots.map((s) => `<tr><td>${esc(trackName(s))}</td><td class="rp-num">${esc(formatDuration(s.durationMs))}</td><td class="rp-num">${s.count}</td></tr>`).join('')
    + (data.empty > 0 ? `<tr><td>Sense cap falca vàlida aquell dia</td><td></td><td class="rp-num">${data.empty}</td></tr>` : '')
    + '</tbody></table>');

  // La graella setmanal: les franges programades
  const grid = /** @type {any[]} */ (data.grid);
  if (grid.length > 0) {
    parts.push('<h2>Franges programades</h2><table class="rp-grid"><thead><tr><th>Hora</th>'
      + DAY_SHORT.map((d, i) => `<th title="${esc(DAY_NAMES[i])}">${d}</th>`).join('') + '</tr></thead><tbody>'
      + grid.map((r) => `<tr><th>${esc(String(r.time).slice(0, 5))}</th>`
        + r.days.map((/** @type {boolean} */ on) => (on ? '<td class="rp-on">✔</td>' : '<td></td>')).join('') + '</tr>').join('')
      + '</tbody></table>');
  }

  // Cada passi previst
  const passes = /** @type {any[]} */ (data.passes);
  parts.push('<h2>Passis previstos</h2>');
  if (passes.length === 0) parts.push('<p>No hi ha cap passi previst en aquest període.</p>');
  else {
    parts.push(`<table class="rp-table rp-list"><thead><tr><th class="rp-num">Núm.</th><th>Data</th><th>Dia</th><th>Hora</th>${campaign ? '<th>Falca prevista</th>' : ''}</tr></thead><tbody>`
      + passes.map((p, i) => {
        const { date, time } = split(p.at);
        const spot = campaign ? `<td>${p.trackId == null ? '<em>cap falca vàlida</em>' : esc(p.title ?? '')}</td>` : '';
        return `<tr><td class="rp-num">${i + 1}</td><td>${esc(dmy(date))}</td><td>${esc(DAY_NAMES[weekday(date)])}</td><td>${esc(time.slice(0, 5))}</td>${spot}</tr>`;
      }).join('')
      + '</tbody></table>');
  }
  return `<article class="report">${parts.join('')}</article>`;
}

/**
 * Certificat d'emissió (D-138): les dades, la frase del certificat, el nombre de passis emesos, per falca, i la llista de
 * cada emissió real (de l'historial) amb la data, l'hora, l'anunciant o artista, el títol i si va sonar sencera.
 * @param {any} data  de reports.certificate
 * @param {{ name: string, frequency: string, logo: string | null }} station
 * @param {string} today  'AAAA-MM-DD'
 */
export function certificateHtml(data, station, today) {
  const campaign = data.subject.kind === 'campaign';
  const parts = [header(station, 'Certificat d\'emissió', today), subjectBlock(data.subject, data.from, data.to)];
  const who = station.name ? esc(station.name) : 'L\'emissora';
  const what = campaign ? `la campanya «${esc(data.subject.name)}»` : `la pista «${esc(trackName(data.subject))}»`;
  parts.push(`<p class="rp-statement">${who} certifica que ${what} s'ha emès <strong>${esc(passesText(data.total))}</strong> `
    + `entre el ${esc(dmy(data.from))} i el ${esc(dmy(data.to))}, segons el detall següent, extret del registre d'emissió.</p>`);

  if (campaign && data.bySpot.length > 0) {
    parts.push('<h2>Per falca</h2><table class="rp-table"><thead><tr><th>Falca</th><th class="rp-num">Passis emesos</th></tr></thead><tbody>'
      + data.bySpot.map((/** @type {any} */ s) => `<tr><td>${esc(trackName(s))}</td><td class="rp-num">${s.count}</td></tr>`).join('')
      + '</tbody></table>');
  }

  const emissions = /** @type {any[]} */ (data.emissions);
  parts.push('<h2>Emissions</h2>');
  if (emissions.length === 0) parts.push('<p>No hi ha cap emissió en aquest període.</p>');
  else {
    parts.push('<table class="rp-table rp-list"><thead><tr><th class="rp-num">Núm.</th><th>Data</th><th>Hora</th><th>Anunciant</th><th>Títol</th><th>Sencera</th></tr></thead><tbody>'
      + emissions.map((e, i) => {
        const { date, time } = split(e.at);
        const complete = e.complete == null ? '' : e.complete ? 'Sí' : 'No';
        return `<tr><td class="rp-num">${i + 1}</td><td>${esc(dmy(date))}</td><td>${esc(time)}</td><td>${esc(e.artist)}</td><td>${esc(e.title)}</td><td>${complete}</td></tr>`;
      }).join('')
      + '</tbody></table>');
  }
  return `<article class="report">${parts.join('')}</article>`;
}

/** 'AAAA-MM-DD' més n dies (en UTC: sense canvis d'hora). @param {string} iso @param {number} n */
function addDays(iso, n) {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}

/**
 * El període per defecte d'un informe (D-138):
 * - informe de programació: les dates de la campanya (o de la pista); una pista sense dates, 4 setmanes des d'avui;
 * - certificat: des de l'inici fins avui (o fins a la data de fi, si ja ha passat); una pista sense data d'inici, els
 *   últims 30 dies.
 * @param {'programming' | 'certificate'} kind
 * @param {{ startDate?: string, endDate?: string, validFrom?: string | null, validUntil?: string | null }} subject
 * @param {string} today 'AAAA-MM-DD'
 * @returns {{ from: string, to: string }}
 */
export function defaultPeriod(kind, subject, today) {
  const start = subject.startDate ?? subject.validFrom ?? null;
  const end = subject.endDate ?? subject.validUntil ?? null;
  if (kind === 'programming') {
    const from = start ?? today;
    return { from, to: end ?? addDays(from, 27) };
  }
  const to = end && end < today ? end : today;
  const from = start ?? addDays(to, -29);
  return { from, to: from > to ? from : to };
}

/**
 * El nom proposat per al fitxer PDF: «Certificat - Ferreteria - del 2026-11-01 al 2026-11-30.pdf». Sense els caràcters
 * que Windows no admet en un nom de fitxer.
 * @param {'programming' | 'certificate'} kind @param {any} data
 */
export function suggestedName(kind, data) {
  const what = kind === 'programming' ? 'Informe de programació' : 'Certificat d\'emissió';
  const name = subjectName(data.subject).replace(/[<>:"/\\|?*\u0000-\u001f]/g, '').trim().slice(0, 80);
  return `${what} - ${name} - del ${data.from} al ${data.to}.pdf`;
}
