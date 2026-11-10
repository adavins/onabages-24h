'use strict';

// Pont segur entre la interfície i el procés principal.
// La interfície només pot fer servir el que s'exposa aquí (window.onabages).

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('onabages', {
  /** Port i testimoni del motor. Si no n'hi ha cap en marxa, l'arrenca. */
  startEngine: () => ipcRenderer.invoke('engine:start'),

  /** Port i testimoni del motor que funciona ara (segons engine.json), o null. No n'arrenca cap. */
  findEngine: () => ipcRenderer.invoke('engine:find'),
});
