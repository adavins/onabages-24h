# Ona Bages 24h

Aplicació d'escriptori per a Windows per a l'emissió continuada les 24 hores d'una ràdio local. Substituirà RadioDJ i el Programador del RadioDJ a l'ordinador de continuïtat.

**Estat:** disseny. El desenvolupament comença quan s'acabi Ona Bages Play.

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
| `docs/referencia/` | Encàrrec per preparar el motor dins d'Ona Bages Play (fase 0) |

## Com començar amb Claude Code

1. Crea un repositori nou (per exemple `adavins/ona-bages-24h`) amb aquesta carpeta i fes el primer commit.
2. Obre'l amb VS Code i Claude Code. Claude Code llegeix `CLAUDE.md` automàticament.
3. Per a la fase 0, obre el repositori d'**Ona Bages Play** i passa-li l'encàrrec de `docs/referencia/preparar-motor-ona-bages-play.md`.
4. Quan Ona Bages Play estigui acabat, demana a Claude Code aquí: «Comencem la fase 1 del pla de fases. Proposa'm un pla breu.»
