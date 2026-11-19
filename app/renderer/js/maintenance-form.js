// @ts-check
// Manteniment de la programació (D-194): els textos, les icones i els recomptes de la pantalla, sense tocar la pàgina (es
// proven amb node --test). Les propostes les fa el motor (maintenance.scan; docs/protocol.md, 4.17).

/**
 * @typedef {{ key: string, case: string, subject: string, id: number, name: string, reason: string, action: string }} Finding
 */

/** Els casos, en l'ordre de la pantalla: el títol, la icona i què vol dir. */
export const CASES = [
  ['ESDEVENIMENT_ACABAT', 'Esdeveniments acabats', 'bi-calendar-x', 'La data de fi ja ha passat, o totes les seves dates soltes.'],
  ['ESDEVENIMENT_DESACTIVAT', 'Esdeveniments desactivats fa temps', 'bi-slash-circle', 'Desactivats i sense cap canvi des de fa més de 90 dies.'],
  ['ESDEVENIMENT_DUPLICAT', 'Esdeveniments duplicats', 'bi-files', 'Un altre fa exactament el mateix, a la mateixa hora i els mateixos dies. Es queda el primer.'],
  ['ESDEVENIMENT_NO_GUANYA', 'Esdeveniments que no sonen mai', 'bi-trophy', 'Sempre coincideixen amb un altre de més prioritat (mirant un any endavant).'],
  ['ESDEVENIMENT_SENSE_EFECTE', 'Esdeveniments que ja no fan res', 'bi-volume-mute', 'Totes les seves pistes i campanyes s\'han acabat o ja no existeixen.'],
  ['ACCIONS_TRENCADES', 'Accions trencades', 'bi-link-45deg', 'Apunten a una rotació, una bossa o una subcategoria que ja no existeix.'],
  ['EXCEPCIONS_PASSADES', 'Dies d\'excepció passats', 'bi-calendar-minus', 'Dies que no s\'emet que ja han passat: ja no fan res.'],
  ['ACABADA_ALS_ESDEVENIMENTS', 'Pistes i campanyes acabades als esdeveniments', 'bi-hourglass-bottom', 'Ja no sonen, però encara són als esdeveniments.'],
  ['PISTA_CADUCADA', 'Pistes caducades', 'bi-music-note-beamed', 'La data de fi ja ha passat i no la fa servir cap esdeveniment. Les que han sonat o són d\'una campanya es desactiven, no s\'esborren (els certificats).'],
  ['SUBCATEGORIA_BUIDA', 'Subcategories buides', 'bi-folder-x', 'Sense cap pista, i no les fa servir res.'],
];

/** Què es farà amb cada proposta: el text i la icona (color + icona + text). */
const ACTIONS = /** @type {Record<string, { text: string, icon: string, css: string }>} */ ({
  ESBORRA_ESDEVENIMENT: { text: 'S\'esborra l\'esdeveniment', icon: 'bi-trash', css: 'text-danger' },
  TREU_ACCIONS: { text: 'Es treuen les accions trencades', icon: 'bi-scissors', css: 'text-warning' },
  TREU_EXCEPCIONS: { text: 'Es treuen els dies passats', icon: 'bi-eraser', css: 'text-info' },
  TREU_DELS_ESDEVENIMENTS: { text: 'Es treu dels esdeveniments', icon: 'bi-box-arrow-right', css: 'text-warning' },
  ESBORRA_PISTA: { text: 'S\'esborra de la biblioteca', icon: 'bi-trash', css: 'text-danger' },
  DESACTIVA_PISTA: { text: 'Es desactiva', icon: 'bi-slash-circle', css: 'text-warning' },
  ESBORRA_SUBCATEGORIA: { text: 'S\'esborra la subcategoria', icon: 'bi-trash', css: 'text-danger' },
});

/** @param {string} action */
export function actionInfo(action) {
  return ACTIONS[action] ?? { text: action, icon: 'bi-question-circle', css: 'text-body-secondary' };
}

/**
 * Les propostes agrupades per cas, en l'ordre de {@link CASES} (els casos sense propostes no hi són).
 * @param {Finding[]} findings
 * @returns {{ code: string, title: string, icon: string, help: string, findings: Finding[] }[]}
 */
export function groupFindings(findings) {
  return CASES
    .map(([code, title, iconName, help]) => ({ code, title, icon: iconName, help, findings: findings.filter((f) => f.case === code) }))
    .filter((g) => g.findings.length > 0);
}

/** «1 esdeveniment», «3 esdeveniments». @param {number} n @param {string} one @param {string} many */
function count(n, one, many) {
  return `${n} ${n === 1 ? one : many}`;
}

/**
 * Què passarà si s'apliquen aquestes propostes, en frases (per a la confirmació).
 * @param {Finding[]} findings
 * @returns {string[]}
 */
export function applySummary(findings) {
  const lines = [];
  /** @param {string} action @param {string} one @param {string} many  amb {n} */
  const say = (action, one, many) => {
    const k = findings.filter((f) => f.action === action).length;
    if (k > 0) lines.push(k === 1 ? one : many.replace('{n}', String(k)));
  };
  say('ESBORRA_ESDEVENIMENT', "S'esborrarà 1 esdeveniment.", "S'esborraran {n} esdeveniments.");
  say('TREU_ACCIONS', 'Es trauran accions trencades d\'1 esdeveniment.', 'Es trauran accions trencades de {n} esdeveniments.');
  say('TREU_EXCEPCIONS', 'Es trauran els dies d\'excepció passats d\'1 esdeveniment.', 'Es trauran els dies d\'excepció passats de {n} esdeveniments.');
  say('TREU_DELS_ESDEVENIMENTS', 'Es traurà dels esdeveniments 1 pista o campanya acabada.', 'Es trauran dels esdeveniments {n} pistes o campanyes acabades.');
  say('ESBORRA_PISTA', "S'esborrarà de la biblioteca 1 pista (el fitxer no es toca).", "S'esborraran de la biblioteca {n} pistes (els fitxers no es toquen).");
  say('DESACTIVA_PISTA', 'Es desactivarà 1 pista.', 'Es desactivaran {n} pistes.');
  say('ESBORRA_SUBCATEGORIA', "S'esborrarà 1 subcategoria.", "S'esborraran {n} subcategories.");
  return lines;
}

/**
 * El resultat d'aplicar-les (maintenance.apply), per explicar-lo.
 * @param {{ backup: string, done: { key: string, text: string }[], skipped: { key: string, reason: string }[], error: string | null }} result
 */
export function resultText(result) {
  const parts = [result.done.length === 0 ? 'No s\'ha fet cap canvi.' : `Fet: ${count(result.done.length, 'canvi', 'canvis')}.`];
  if (result.skipped.length > 0)
    parts.push(`${count(result.skipped.length, 'proposta no s\'ha aplicat', 'propostes no s\'han aplicat')}: ha canviat alguna cosa des de l'anàlisi.`);
  if (result.error) parts.push(`S'ha aturat per un error: ${result.error}`);
  const file = result.backup.split(/[\\/]/).pop();
  parts.push(`Abans s'ha fet la còpia de seguretat «${file}» (Configuració → Còpies): restaurant-la es desfà tot.`);
  return parts.join('\n\n');
}
