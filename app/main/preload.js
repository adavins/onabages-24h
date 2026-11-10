'use strict';

// Pont segur entre la interfície i el procés principal.
// La interfície només pot fer servir el que s'exposa aquí (window.onabages).

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('onabages', {
  /** Port i testimoni del motor. Si no n'hi ha cap en marxa, l'arrenca. */
  startEngine: () => ipcRenderer.invoke('engine:start'),

  /** Port i testimoni del motor que funciona ara (segons engine.json), o null. No n'arrenca cap. */
  findEngine: () => ipcRenderer.invoke('engine:find'),

  /** Avisa quan l'operador vol tancar la finestra (la interfície pregunta què vol fer). */
  onCloseRequested: (callback) => ipcRenderer.on('app:close-requested', () => callback()),

  /** Tanca la finestra (el motor no s'atura si no se li ha demanat abans). */
  closeWindow: () => ipcRenderer.invoke('app:close'),
});
