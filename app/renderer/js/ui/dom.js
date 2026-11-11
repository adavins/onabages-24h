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

/**
 * Posa el text només si ha canviat. Les dades arriben 20 vegades per segon i gairebé sempre són iguals: sense tocar
 * la pàgina no cal tornar-la a pintar (sense acceleració gràfica, pintar ho fa tot el processador; D-069).
 * @param {HTMLElement} node  @param {string} text
 */
export function setText(node, text) {
  if (node.textContent !== text) node.textContent = text;
}

/**
 * Posa una propietat d'estil només si ha canviat (vegeu {@link setText}).
 * @param {HTMLElement} node  @param {string} property  p. ex. «width»  @param {string} value
 */
export function setStyle(node, property, value) {
  if (node.style.getPropertyValue(property) !== value) node.style.setProperty(property, value);
}
