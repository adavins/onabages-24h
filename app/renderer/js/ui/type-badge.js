// @ts-check
// L'etiqueta de color del tipus d'una pista (D-118): cada tipus el seu color, sempre amb el text (mai només el color).
// La fan servir la llista de pistes, els passos de les rotacions i el calendari d'una pista. Colors: app.css (.type-*).

import { el } from './dom.js';
import { typeName } from '../format.js';

/**
 * @param {string | null | undefined} type  el tipus del motor (music, commercial…); null = no se sap
 * @param {string} [title]  el text en passar-hi el ratolí
 */
export function typeBadge(type, title) {
  const badge = el('span', `type-badge type-${type ?? 'unknown'}`, type ? typeName(type) : '—');
  if (title) badge.title = title;
  return badge;
}
