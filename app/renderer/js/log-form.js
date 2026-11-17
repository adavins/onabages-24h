// @ts-check
// Pestanya «Registre» (D-167): textos, icones i paràmetres, sense tocar la pàgina (es proven amb node --test). El registre
// ajunta les incidències, els canvis, les sessions i l'execució dels esdeveniments (log.list; docs/protocol.md, 4.15).

import { kindText as incidentKindText, stateInfo, spanText, periodFrom } from './incidents-form.js';
import { areaText } from './changes-form.js';
import { MISSING_REASONS } from './report-html.js';

/**
 * @typedef {{ source: 'incident' | 'change' | 'session' | 'event', id: number, at: string, endedAt: string | null,
 *   open: boolean, kind: string, severity: 'danger' | 'warning' | 'info', text: string, detail: string | null,
 *   recovered: boolean, userId: number | null, user: string }} LogEntry
 * @typedef {{ period: string, sources: string[], severity: string, type: string, userId: string, text: string,
 *   openOnly: boolean }} LogFilters
 */

/** D'on ve: el codi, el text i la icona (sempre icona + text). Els esdeveniments, desmarcats per defecte. */
export const SOURCES = /** @type {const} */ ([
  ['incident', 'Incidències', 'bi-exclamation-diamond'],
  ['change', 'Canvis', 'bi-pencil-square'],
  ['session', 'Sessions', 'bi-person-badge'],
  ['event', 'Esdeveniments', 'bi-calendar-event'],
]);

/** Els orígens marcats al principi (els esdeveniments en són centenars al dia: els omesos ja són incidències). */
export const DEFAULT_SOURCES = ['incident', 'change', 'session'];

/** La icona d'un origen. @param {string} source */
export function sourceIcon(source) {
  return SOURCES.find(([code]) => code === source)?.[2] ?? 'bi-circle';
}

/** El nom d'un origen en singular («Incidència», «Canvi», «Sessió», «Esdeveniment»). @param {string} source */
export function sourceName(source) {
  return { incident: 'Incidència', change: 'Canvi', session: 'Sessió', event: 'Esdeveniment' }[source] ?? source;
}

/** La columna «Tipus»: el tipus d'incidència, l'àmbit del canvi o «Planificador». @param {LogEntry} e */
export function typeText(e) {
  if (e.source === 'incident') return incidentKindText(e.kind);
  if (e.source === 'event') return 'Planificador';
  return areaText(e.kind);
}

/** El resultat d'un esdeveniment: «Executat» o el motiu pel qual no ha sonat. @param {string} result */
export function eventResultText(result) {
  return result === 'OK' ? 'Executat' : (MISSING_REASONS[result] ?? result);
}

/**
 * La columna «Estat»: per a una incidència, oberta, resolta… (i quant ha durat); per a un esdeveniment, el resultat; per
 * a un canvi o una sessió, res.
 * @param {LogEntry} e @param {Date} now
 * @returns {{ text: string, className: string, icon: string } | null}
 */
export function stateText(e, now) {
  if (e.source === 'incident') {
    const state = stateInfo(/** @type {any} */ ({ ...e, startedAt: e.at }));
    const ms = (e.endedAt ? new Date(e.endedAt) : now).getTime() - new Date(e.at).getTime();
    const duration = e.open ? ` (fa ${spanText(ms)})` : (ms >= 1000 ? ` (${spanText(ms)})` : '');
    return { ...state, text: state.text + duration };
  }
  if (e.source === 'event') {
    if (e.kind === 'OK') return { text: 'Executat', className: 'text-success', icon: 'bi-check-circle' };
    return {
      text: eventResultText(e.kind),
      className: e.severity === 'danger' ? 'text-danger' : 'text-warning',
      icon: e.severity === 'danger' ? 'bi-x-octagon' : 'bi-skip-forward',
    };
  }
  return null;
}

/** La columna «Què ha passat»: per a un esdeveniment, el nom amb el prefix. @param {LogEntry} e */
export function entryText(e) {
  return e.source === 'event' ? `Esdeveniment «${e.text}»` : e.text;
}

/**
 * Els paràmetres de log.list per a aquests filtres. <c>type</c>: «incident:KIND» o «change:ÀREA» (amb els prefixos).
 * @param {LogFilters} f @param {Date} today @param {(area: string) => string[]} areaPrefixes
 */
export function logParams(f, today, areaPrefixes) {
  /** @type {Record<string, any>} */
  const p = { sources: f.sources };
  if (f.openOnly) p.openOnly = true;
  else if (f.period) p.from = periodFrom(f.period, today);
  if (f.severity) p.minSeverity = f.severity;
  if (f.type.startsWith('incident:')) p.kinds = [f.type.slice('incident:'.length)];
  if (f.type.startsWith('change:')) p.commands = areaPrefixes(f.type.slice('change:'.length));
  if (f.userId) p.userId = Number(f.userId);
  if (f.text.trim()) p.text = f.text.trim();
  return p;
}

/** «Cap entrada», «1 entrada», «1.234 entrades». @param {number} n */
export function countText(n) {
  if (n === 0) return 'Cap entrada';
  return `${n.toLocaleString('ca-ES')} ${n === 1 ? 'entrada' : 'entrades'}`;
}
