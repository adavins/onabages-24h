# Maquetes

Maquetes de dues pantalles, fetes el novembre del 2026. Les dades (programes, falques i hores) són exemples; les cançons surten de captures de RadioDJ.

| Fitxer | Pantalla | Imatge |
|---|---|---|
| `Emissio.dc.html` | Emissió (pantalla principal) | `emissio.png` |
| `Main.dc.html` | Planificador setmanal | `planificador.png` |

**Per veure-les:** obre el fitxer `.dc.html` amb el navegador. El fitxer `support.js` d'aquesta carpeta és un visor mínim que les fa funcionar sense connexió. Al planificador es pot clicar un esdeveniment per veure'n el detall al panell de la dreta. Sense internet, la tipografia passa a la del sistema.

L'original editable és un llenç de disseny a claude.ai: https://claude.ai/artifact/EV6FTibYKaNiyRJmhiBbM5

**Per a Claude Code:** són una referència d'aspecte i de contingut, no codi per reaprofitar. La interfície real es fa amb Bootstrap 5 (tema fosc) i JavaScript pur, com Ona Bages Play. Del disseny cal conservar:
- els colors i les icones dels quatre modes d'esdeveniment (Talla, Encua, Superposa, Sistema), sempre amb el text al costat;
- l'avís de previsió de la cua quan un esdeveniment que talla l'emissió arribarà abans que acabin les pistes;
- la graella per setmanes reals amb el detall de l'esdeveniment al costat.
