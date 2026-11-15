// @ts-check
// Paquet de campanya (fase 4, pas 7a; D-150): textos de l'exportació i de l'informe d'importació, sense tocar la pàgina
// (es proven amb node --test).

/** «Ferreteria Puig» → «Ferreteria Puig.json» (sense els caràcters que Windows no accepta en un nom de fitxer). @param {string} name */
export function packageFileName(name) {
  const clean = name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '').replace(/\s+/g, ' ').trim();
  return `${clean || 'Campanya'}.json`;
}

/** Cada estat d'una falca a l'informe: color + icona + text. */
const STATUS = {
  existing: { label: 'Ja és a la biblioteca', className: 'text-success', icon: 'bi-check-circle' },
  new: { label: 'Es crearà la pista', className: 'text-info', icon: 'bi-plus-circle' },
  missing: { label: 'No es troba: no hi entrarà', className: 'text-danger', icon: 'bi-x-circle' },
  unreadable: { label: 'No es pot llegir: no hi entrarà', className: 'text-danger', icon: 'bi-x-circle' },
};

/** @param {string} status @returns {{ label: string, className: string, icon: string }} */
export function spotStatusInfo(status) {
  return STATUS[/** @type {keyof typeof STATUS} */ (status)] ?? { label: status, className: 'text-warning', icon: 'bi-question-circle' };
}

/** «1 falca», «3 falques». @param {number} n */
function spots(n) {
  return n === 1 ? '1 falca' : `${n} falques`;
}

/** «1 casella», «4 caselles». @param {number} n */
function cells(n) {
  return n === 1 ? '1 casella' : `${n} caselles`;
}

/**
 * El que diu en acabar d'exportar.
 * @param {{ spots: number, cells: number, notInFolder: string[] }} result
 * @returns {{ summary: string, warning: string | null }}
 */
export function exportSummary(result) {
  const summary = `S'ha desat el paquet amb ${spots(result.spots)} i el calendari (${cells(result.cells)}).`;
  if (result.notInFolder.length === 0) return { summary, warning: null };
  const one = result.notInFolder.length === 1;
  return {
    summary,
    warning: `${one ? 'Aquesta falca no és' : 'Aquestes falques no són'} a la mateixa carpeta que el paquet: ${result.notInFolder.join(', ')}. ` +
      `Copia-${one ? 'la' : 'les'}-hi perquè l'altra emissora ${one ? 'la' : 'les'} trobi.`,
  };
}

/**
 * Què farà la importació (de l'informe de prova).
 * @param {{ spots: { status: string }[], calendar: { cells: number } }} report
 * @returns {{ found: number, missing: number, created: number, text: string }}
 */
export function importPlan(report) {
  const found = report.spots.filter((s) => s.status === 'existing' || s.status === 'new').length;
  const created = report.spots.filter((s) => s.status === 'new').length;
  const missing = report.spots.length - found;
  const parts = [`Es crearà la campanya en esborrany amb ${spots(found)}`];
  if (created > 0) parts.push(`${created === 1 ? 'es crearà 1 pista nova' : `es crearan ${created} pistes noves`}`);
  parts.push(report.calendar.cells > 0 ? `es marcaran ${cells(report.calendar.cells)} del calendari` : 'no hi ha cap casella del calendari per marcar');
  let text = `${parts.join(', ')}.`;
  if (missing > 0) text += ` ${missing === 1 ? '1 falca no hi entrarà' : `${missing} falques no hi entraran`}: mira'n el motiu a la llista.`;
  return { found, missing, created, text };
}
