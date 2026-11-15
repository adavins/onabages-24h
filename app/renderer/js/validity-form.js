// @ts-check
// Avisos de vigència (fase 4, pas 6b; D-146): textos, colors i icones, sense tocar la pàgina (es proven amb node --test).

/**
 * @typedef {{ kind: string, subject: 'track' | 'campaign', id: number, name: string, text: string,
 *   events: { id: number, name: string }[], canRemove: boolean }} ValidityWarning  docs/protocol.md, validity.list
 */

/** Cada tipus d'avís: el text curt, el color (classe de Bootstrap) i la icona. Sempre color + icona + text. */
const KINDS = {
  CADUCADA: { label: 'Caducada', className: 'text-danger', icon: 'bi-calendar-x' },
  NO_SONARA: { label: 'No sonarà', className: 'text-danger', icon: 'bi-volume-mute-fill' },
  DATES_INCORRECTES: { label: 'Dates incorrectes', className: 'text-warning', icon: 'bi-exclamation-triangle-fill' },
  COMENCA_AVIAT: { label: 'Comença aviat', className: 'text-info', icon: 'bi-hourglass-split' },
};

/** @param {string} kind @returns {{ label: string, className: string, icon: string }} */
export function kindInfo(kind) {
  return KINDS[/** @type {keyof typeof KINDS} */ (kind)] ?? { label: kind, className: 'text-warning', icon: 'bi-exclamation-triangle-fill' };
}

/** «Pista» o «Campanya». @param {ValidityWarning} w */
export function subjectText(w) {
  return w.subject === 'campaign' ? 'Campanya' : 'Pista';
}

/**
 * El que diu la barra de sota: null si no n'hi ha cap; si no, el text i si n'hi ha de greus (caducades o que no sonaran).
 * @param {ValidityWarning[]} warnings
 * @returns {{ text: string, serious: boolean } | null}
 */
export function barText(warnings) {
  if (warnings.length === 0) return null;
  return {
    text: warnings.length === 1 ? '1 avís de vigència' : `${warnings.length} avisos de vigència`,
    serious: warnings.some((w) => w.kind === 'CADUCADA' || w.kind === 'NO_SONARA'),
  };
}

/** Els avisos d'una pista o d'una campanya. @param {ValidityWarning[]} warnings @param {'track' | 'campaign'} subject @param {number} id */
export function warningsFor(warnings, subject, id) {
  return warnings.filter((w) => w.subject === subject && w.id === id);
}

/**
 * El resum de «Treu-la dels esdeveniments».
 * @param {{ changed: number, deleted: number, kept: { id: number, name: string }[] }} result
 */
export function removedText(result) {
  const parts = [];
  if (result.changed > 0) parts.push(result.changed === 1 ? 'S\'ha tret d\'1 esdeveniment.' : `S'ha tret de ${result.changed} esdeveniments.`);
  if (result.deleted > 0) parts.push(result.deleted === 1 ? 'S\'ha esborrat 1 bloc de falques que s\'havia quedat buit.' : `S'han esborrat ${result.deleted} blocs de falques que s'havien quedat buits.`);
  if (result.kept.length > 0) {
    parts.push(`No s'ha pogut treure de ${result.kept.map((e) => `«${e.name}»`).join(', ')}: ${result.kept.length === 1 ? 'és l\'única acció de l\'esdeveniment' : 'és l\'única acció d\'aquests esdeveniments'}. Canvia-ho o esborra'l al Planificador.`);
  }
  return parts.length > 0 ? parts.join(' ') : 'No era a cap esdeveniment actiu.';
}
