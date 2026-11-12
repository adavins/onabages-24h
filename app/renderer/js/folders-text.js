// @ts-check
// Textos de les carpetes vigilades (fase 3, pas 7c-2; D-090): els avisos per a la barra d'estat i els noms de les
// accions del registre. Sense res de la pantalla: es prova amb «npm test».

/**
 * Els avisos de les carpetes vigilades: les que no es poden revisar i les que tenen pistes amb el fitxer que falta.
 * @param {any[]} folders  de folders.status o folders.changed (docs/protocol.md, 4.11)
 * @returns {string[]}
 */
export function folderWarnings(folders) {
  const list = [];
  for (const f of folders ?? []) {
    if (f.problem) list.push(`Carpeta vigilada de «${f.name}»: ${f.problem}`);
    else if (f.missing > 0)
      list.push(`Carpeta vigilada de «${f.name}»: ${f.missing} ${f.missing === 1 ? 'pista té' : 'pistes tenen'} el fitxer que falta`);
  }
  return list;
}

/** Com es diu cada acció del registre, amb icona i color (color + icona + text). */
const ACTIONS = {
  imported: { text: 'Importada', icon: 'bi-plus-circle', css: 'text-success' },
  recovered: { text: 'Recuperada', icon: 'bi-arrow-counterclockwise', css: 'text-success' },
  updated: { text: 'Actualitzada', icon: 'bi-arrow-repeat', css: 'text-info' },
  missing: { text: 'Falta', icon: 'bi-exclamation-triangle-fill', css: 'text-warning' },
  deleted: { text: 'Esborrada', icon: 'bi-trash', css: 'text-body-secondary' },
  error: { text: 'No es pot llegir', icon: 'bi-x-octagon-fill', css: 'text-danger' },
  folder: { text: 'Carpeta', icon: 'bi-folder', css: 'text-warning' },
};

/** @param {string} action  imported, recovered… @returns {{ text: string, icon: string, css: string }} */
export function folderAction(action) {
  return ACTIONS[/** @type {keyof typeof ACTIONS} */ (action)] ?? { text: action, icon: 'bi-dot', css: '' };
}
