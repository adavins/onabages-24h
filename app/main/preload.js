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

  /** Zoom de la finestra (de 0,7 a 1,5; js/ui/zoom.js). */
  setZoom: (factor) => ipcRenderer.invoke('ui:set-zoom', factor),

  /** Alçada de la capçalera en píxels CSS: els botons de la finestra hi fan la mateixa alçada. */
  setTopbarHeight: (cssHeight) => ipcRenderer.send('ui:topbar-height', cssHeight),

  /** Diàleg de Windows per triar un fitxer d'àudio (gestor de pistes). Retorna la ruta, o null si es cancel·la. */
  chooseAudioFile: (defaultPath) => ipcRenderer.invoke('dialog:choose-audio-file', defaultPath),

  /** Diàleg de Windows per triar un o diversos fitxers d'àudio (importació). Retorna les rutes, o null si es cancel·la. */
  chooseAudioFiles: (defaultPath) => ipcRenderer.invoke('dialog:choose-audio-files', defaultPath),

  /** Diàleg de Windows per triar una carpeta (importació). Retorna la ruta, o null si es cancel·la. */
  chooseFolder: (defaultPath) => ipcRenderer.invoke('dialog:choose-folder', defaultPath),
});
