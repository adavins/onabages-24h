// @ts-check
// Zoom de la interfície: Ctrl + roda del ratolí, Ctrl + / Ctrl − i Ctrl 0 (100 %).
// El fa Electron (ho fa tot més gran o més petit, també la lletra). El valor es desa al motor (ui.set),
// perquè en tornar a obrir l'aplicació surti igual.

/** Passos de zoom (1 = 100 %). Electron accepta de 0,7 a 1,5. */
const ZOOM_STEPS = [0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

let current = 1.0;

/**
 * Activa les dreceres del zoom.
 * @param {(zoom: number) => void} onChange  es crida quan l'operador canvia el zoom (per desar-lo i mostrar-lo)
 */
export function setupZoom(onChange) {
  window.addEventListener('keydown', (e) => {
    if (!e.ctrlKey || e.altKey) return;
    if (e.key === '+' || e.key === '=') step(+1);
    else if (e.key === '-') step(-1);
    else if (e.key === '0') change(1.0);
    else return;
    e.preventDefault();
  });

  window.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault(); // si no, la llista de sota també es desplaçaria
    step(e.deltaY < 0 ? +1 : -1);
  }, { passive: false });

  /** Un pas amunt o avall des del zoom actual. @param {number} direction */
  function step(direction) {
    let index = ZOOM_STEPS.findIndex((s) => s >= current - 0.001);
    if (index < 0) index = ZOOM_STEPS.length - 1;
    // Si el zoom actual no és cap pas (p. ex. 0,75), el primer pas avall és el de sota
    if (direction < 0 && ZOOM_STEPS[index] > current + 0.001) index++;
    change(ZOOM_STEPS[Math.min(ZOOM_STEPS.length - 1, Math.max(0, index + direction))]);
  }

  /** @param {number} zoom */
  function change(zoom) {
    if (Math.abs(zoom - current) < 0.001) return;
    applyZoom(zoom);
    onChange(zoom);
  }
}

/** Aplica un zoom (p. ex. el desat al motor) sense tornar-lo a desar. @param {number} zoom */
export function applyZoom(zoom) {
  current = Math.min(1.5, Math.max(0.7, zoom));
  /** @type {any} */ (window).onabages?.setZoom(current);
}
