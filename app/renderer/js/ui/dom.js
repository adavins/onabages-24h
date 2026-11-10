// @ts-check
// Ajudes petites per construir la pàgina. Els textos sempre van amb textContent (mai innerHTML):
// un títol de cançó amb «<» o «&» es mostra tal qual i no pot trencar la pàgina.

/** @param {string} id */
export function $(id) {
  return /** @type {HTMLElement} */ (document.getElementById(id));
}

/**
 * Crea un element amb classes i, opcionalment, text.
 * @param {string} tag
 * @param {string} [className]
 * @param {string | null} [text]
 */
export function el(tag, className = '', text = null) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

/** Icona de Bootstrap Icons (decorativa: el text sempre va al costat). @param {string} name  p. ex. «bi-check-circle-fill» */
export function icon(name) {
  const i = el('i', `bi ${name}`);
  i.setAttribute('aria-hidden', 'true');
  return i;
}

/**
 * Estat amb color + icona + text (mai només color).
 * @param {string} className  p. ex. «text-success»
 * @param {string} iconName
 * @param {string} text
 */
export function statusLabel(className, iconName, text) {
  const span = el('span', `status-label ${className}`);
  span.append(icon(iconName), ' ', el('span', '', text));
  return span;
}
