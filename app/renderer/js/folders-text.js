// @ts-check
// Textos de les carpetes vigilades (fase 3, pas 7c-2; D-090): els noms de les accions del registre. Els avisos de la
// barra d'estat ara són incidències del motor (fase 5, pas 1; D-156). Sense res de la pantalla: es prova amb «npm test».

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
