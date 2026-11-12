// @ts-check
// Editor de rotacions (fase 3, pas 8a; D-094): els passos tal com els mostra el formulari, i la conversió amb el que diu
// i demana el motor (rotations.list, rotations.save). Sense res de la pantalla: es prova amb «npm test».

/**
 * Un pas al formulari. `source`: «b:ID» (una bossa amb nom, pas 8c), «s:ID» (una subcategoria) o «c:ID» (qualsevol
 * subcategoria de la categoria), o '' si encara no s'ha triat.
 * @typedef {{ source: string, tagId: number | null, selection: string, repeatRule: boolean, sweeper: boolean }} FormStep
 */

/** Les seleccions, en l'ordre del desplegable. */
export const SELECTIONS = [
  ['leastRecent', 'La que fa més temps que no sona'],
  ['leastPlayed', 'La que ha sonat menys vegades'],
  ['random', 'A l\'atzar'],
];

/** Un pas nou (per defecte: música, la que fa més temps que no sona, amb repetició). @returns {FormStep} */
export function newStep() {
  return { source: '', tagId: null, selection: 'leastRecent', repeatRule: true, sweeper: false };
}

/** Un pas de rotations.list al format del formulari. @param {any} step @returns {FormStep} */
export function stepFromApi(step) {
  return {
    // Amb bossa, la bossa (la subcategoria que porta és la que té ara: no és del pas)
    source: step.bagId != null ? `b:${step.bagId}`
      : step.subcategoryId != null ? `s:${step.subcategoryId}` : step.categoryId != null ? `c:${step.categoryId}` : '',
    tagId: step.tagId ?? null,
    selection: step.selection ?? 'leastRecent',
    repeatRule: step.repeatRule !== false,
    sweeper: !!step.sweeper,
  };
}

/** Un pas del formulari al format de rotations.save. @param {FormStep} step */
export function stepToApi(step) {
  const [kind, id] = step.source.split(':');
  /** @type {Record<string, any>} */
  const result = { selection: step.selection, repeatRule: step.repeatRule, sweeper: step.sweeper };
  if (kind === 'b') result.bagId = Number(id);
  else if (kind === 's') result.subcategoryId = Number(id);
  else if (kind === 'c') result.categoryId = Number(id);
  if (step.tagId != null) result.tagId = step.tagId;
  return result;
}

/**
 * Si el formulari és diferent de la rotació desada (per saber si hi ha canvis sense desar).
 * @param {any | null} rotation  la de rotations.list (null = rotació nova)
 * @param {string} name @param {FormStep[]} steps
 */
export function rotationChanged(rotation, name, steps) {
  if (!rotation) return true;
  if (name.trim() !== rotation.name) return true;
  return JSON.stringify(steps) !== JSON.stringify(rotation.steps.map(stepFromApi));
}

/**
 * Problemes que impedeixen desar (en català).
 * @param {string} name @param {FormStep[]} steps
 * @returns {string[]}
 */
export function rotationProblems(name, steps) {
  const problems = [];
  if (!name.trim()) problems.push('Falta el nom de la rotació.');
  if (steps.length === 0) problems.push('Una rotació ha de tenir com a mínim un pas.');
  const empty = steps.map((s, i) => (s.source ? null : i + 1)).filter((n) => n != null);
  if (empty.length > 0) problems.push(`${empty.length === 1 ? 'Al pas' : 'Als passos'} ${empty.join(', ')} falta d'on surt la pista.`);
  return problems;
}
