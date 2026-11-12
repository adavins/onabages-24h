// @ts-check
// Les col·leccions de sweepers per al trepitjador (fase 3, pas 5b; D-084): a la barra de l'AutoDJ i a l'acció
// «Sweepers» de l'editor. Són les subcategories de la categoria «Sweepers» (o semblant) de library.filters.

/** @typedef {{ id: number, name: string, category: string, label: string }} SubcategoryOption */

/**
 * @param {any[]} categories  `categories` de library.filters: `[{ id, name, subcategories: [{ id, name }] }]`
 * @returns {SubcategoryOption[]}
 */
export function sweeperOptions(categories) {
  /** @type {SubcategoryOption[]} */
  const options = [];
  for (const category of categories) {
    if (!/sweep/i.test(category.name)) continue;
    for (const sub of category.subcategories) {
      options.push({ id: sub.id, name: sub.name, category: category.name, label: `${category.name} / ${sub.name}` });
    }
  }
  return options;
}

/**
 * Omple un <select> amb les col·leccions de sweepers.
 * @param {HTMLSelectElement} select
 * @param {SubcategoryOption[]} options
 * @param {string | null} emptyLabel  l'opció «cap» (valor buit), o null perquè no n'hi hagi
 * @param {boolean} [short]  només amb el nom de la subcategoria (on ja diu «Sweepers» al costat)
 */
export function fillSweeperSelect(select, options, emptyLabel, short = false) {
  const option = (/** @type {string} */ value, /** @type {string} */ text) => {
    const o = document.createElement('option');
    o.value = value;
    o.textContent = text;
    return o;
  };
  const children = options.map((s) => option(String(s.id), short ? s.name : s.label));
  if (emptyLabel != null) children.unshift(option('', emptyLabel));
  select.replaceChildren(...children);
}
