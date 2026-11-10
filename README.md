# Ona Bages 24h

Aplicació d'escriptori per a Windows per a l'emissió continuada les 24 hores d'una ràdio local. Substituirà RadioDJ i el Programador del RadioDJ a l'ordinador de continuïtat.

**Estat:** fase 1 (esquelet i motor 24 h mínim): motor amb AutoDJ i rotacions, i interfície d'emissió mínima. És un projecte independent d'Ona Bages Play, del qual es copia el codi d'àudio (D-019).

## Què hi ha en aquest repositori

| On | Què |
|---|---|
| `CLAUDE.md` | Regles i context per a Claude Code |
| `docs/pla-de-fases.md` | Fases del projecte i criteris de tancament |
| `docs/decisions.md` | Decisions preses fins ara |
| `docs/planificador.md` | Planificador: modes, accions, recurrències, campanyes, esquema de dades i API del motor |
| `docs/pantalla-principal-i-encadenament.md` | Pantalla d'emissió, encadenament de pistes, rotacions i configuració actual de RadioDJ |
| `docs/context-operatiu.md` | Entorn de l'emissora i requisits que se'n deriven |
| `docs/maquetes/` | Maquetes de les pantalles Emissió i Planificador (obre els `.dc.html` al navegador) |
| `docs/radiodj/` | Esquema i dades de referència de RadioDJ per a la migració |
| `docs/referencia/` | Encàrrec de la fase 0, ja tancada (només com a història) |

## Scripts (a l'arrel, amb doble clic o des d'una consola)

Cal tenir instal·lats el SDK de .NET 10 i Node.js.

| Script | Què fa |
|---|---|
| `arrencar.cmd` | Compila el motor i obre la interfície, que arrenca el motor si no n'hi ha cap en marxa |
| `arrencar-motor.cmd` | Compila el motor i l'arrenca sol en aquella finestra, on es veu el registre (Ctrl+C l'atura) |
| `aturar-motor.cmd` | Atura el motor (`engine.shutdown`): l'emissió s'atura |
| `empaquetar.cmd` | Genera la versió portable a `build\OnaBages24\` i `build\OnaBages24-<versió>.zip`, amb el .NET inclòs i la carpeta `dades` buida (D-044) |

Si el motor ja funciona, no es pot recompilar (els seus fitxers estan en ús): primer `aturar-motor.cmd`.

## Com començar amb Claude Code

1. Obre aquesta carpeta amb VS Code i Claude Code. Claude Code llegeix `CLAUDE.md` automàticament.
2. Demana-li: «Comencem la fase 1 del pla de fases. Proposa'm un pla breu.»
