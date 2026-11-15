// @ts-check
// Còpies de seguretat (fase 4, pas 7b; D-152): textos de la finestra, sense tocar la pàgina (es proven amb node --test).

/** Dos dígits. @param {number} n */
function two(n) {
  return String(n).padStart(2, '0');
}

/** «12/11/2026 03:00» (hora d'aquest ordinador). @param {string} iso */
export function backupDateText(iso) {
  const d = new Date(iso);
  return `${d.getDate()}/${d.getMonth() + 1}/${d.getFullYear()} ${two(d.getHours())}:${two(d.getMinutes())}`;
}

/** «Diària o feta a mà» o «Abans de restaurar». @param {string} kind */
export function kindText(kind) {
  return kind === 'safety' ? 'Abans de restaurar' : 'Diària o feta a mà';
}

/** «850 kB», «12,4 MB». @param {number | null | undefined} bytes */
export function sizeText(bytes) {
  if (bytes == null) return '—';
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} kB`;
  return `${(bytes / (1024 * 1024)).toFixed(1).replace('.', ',')} MB`;
}

/** «1 pista», «3.210 pistes». @param {number} n @param {string} one @param {string} many */
function count(n, one, many) {
  return `${n.toLocaleString('ca-ES')} ${n === 1 ? one : many}`;
}

/**
 * Què hi ha a la còpia (de backups.inspect), per a la confirmació.
 * @param {{ version: number, currentVersion: number, tracks: number, events: number, rotations: number, campaigns: number, lastPlayed: string | null }} info
 * @returns {string[]}
 */
export function inspectLines(info) {
  const lines = [
    `${count(info.tracks, 'pista', 'pistes')}, ${count(info.events, 'esdeveniment', 'esdeveniments')}, ` +
      `${count(info.rotations, 'rotació', 'rotacions')} i ${count(info.campaigns, 'campanya', 'campanyes')}.`,
    info.lastPlayed ? `L'última emissió de l'historial és del ${backupDateText(info.lastPlayed)}.` : 'L\'historial és buit.',
  ];
  if (info.version < info.currentVersion) lines.push(`És d'una versió anterior de l'aplicació: després de restaurar-la es posarà al dia.`);
  return lines;
}

/**
 * El que diu en acabar de restaurar.
 * @param {{ safety: string, keptHistory: number, keptExecutions: number }} result @param {boolean} keepHistory
 */
export function restoredText(result, keepHistory) {
  const name = result.safety.split(/[\\/]/).pop();
  const parts = ['S\'ha restaurat la còpia. L\'emissió no s\'ha aturat.'];
  if (keepHistory) {
    parts.push(result.keptHistory === 0 && result.keptExecutions === 0
      ? 'No hi havia res a l\'historial posterior a la còpia.'
      : `S'han conservat ${count(result.keptHistory, 'entrada', 'entrades')} de l'historial i ${count(result.keptExecutions, 'línia', 'línies')} del registre dels esdeveniments posteriors a la còpia.`);
  }
  parts.push(`Si cal desfer-ho, a la llista hi ha la còpia de com estava abans («Abans de restaurar»): ${name}`);
  return parts.join('\n\n');
}
