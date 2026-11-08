# Ona Bages 24h

Aplicació d'escriptori per a Windows per a l'emissió continuada les 24 hores d'una ràdio local. Substituirà RadioDJ i el Programador del RadioDJ a l'ordinador de continuïtat.

**Estat:** fase 1 (esquelet i motor 24 h mínim), encara sense codi. És un projecte independent d'Ona Bages Play, del qual es copia el codi d'àudio (D-019).

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

## Com començar amb Claude Code

1. Obre aquesta carpeta amb VS Code i Claude Code. Claude Code llegeix `CLAUDE.md` automàticament.
2. Demana-li: «Comencem la fase 1 del pla de fases. Proposa'm un pla breu.»
