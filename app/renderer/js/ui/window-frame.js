// @ts-check
// Capçalera com a barra de títol (com a Ona Bages Play). La finestra no té la barra de títol de Windows:
// la capçalera la substitueix (s'hi arrossega la finestra i amb doble clic es maximitza: .topbar a css/app.css) i els
// botons de minimitzar, maximitzar i tancar es dibuixen a sobre, a la dreta. Aquí:
// - es diu a Electron l'alçada de la capçalera, perquè els botons hi encaixin (canvia amb el zoom);
// - es deixa lloc a la dreta (--ob-window-controls), perquè els botons no tapin l'estat del motor.

export function setupWindowFrame() {
  const topbar = /** @type {HTMLElement} */ (document.querySelector('.topbar'));
  const bridge = /** @type {any} */ (window).onabages;

  // Sense la vora de sota (clientHeight): si no, els botons de Windows la taparien.
  new ResizeObserver(() => bridge?.setTopbarHeight(topbar.clientHeight)).observe(topbar);

  /** API del navegador per a aquest cas (Window Controls Overlay) @type {any} */
  const overlay = /** @type {any} */ (navigator).windowControlsOverlay;
  const update = () => {
    // Espai que ocupen els botons de Windows, en píxels CSS. Si el navegador no ho diu, uns 140.
    const area = overlay?.visible ? overlay.getTitlebarAreaRect() : null;
    const width = area && area.width > 0 ? Math.max(0, window.innerWidth - (area.x + area.width)) : 140;
    document.documentElement.style.setProperty('--ob-window-controls', `${Math.ceil(width)}px`);
  };
  overlay?.addEventListener?.('geometrychange', update);
  window.addEventListener('resize', update);
  update();
}
