# Pla de fases

Proposta inicial (novembre del 2026). Cada fase acaba amb un **criteri de tancament** que es pot comprovar. Quan una fase es tanca, s'anota a `docs/decisions.md` i s'actualitza la línia «Fase actual» de `CLAUDE.md`.

El desenvolupament d'Ona Bages 24h comença quan Ona Bages Play estigui acabat (D-016, complert a D-017). Ona Bages 24h és independent de Play: el codi d'àudio se'n copia una sola vegada (D-019).

---

## Fase 0 — Preparació del motor (dins d'Ona Bages Play) · tancada (D-020)

Encàrrec original a `docs/referencia/preparar-motor-ona-bages-play.md`.

- ✔ Revisar que `OnaBages.Audio` no depengui del motor de Play (D-046 de Play).
- ✔ Prova automàtica: un fitxer es pot substituir mentre sona (`FileLockTests` de Play).
- ✔ Decidir com es comparteix `OnaBages.Audio`: es copia (D-019).
- → Model de punts de pista i notes de disseny del mesclador: passen a la fase 1.

**Criteri de tancament:** decisió sobre com es comparteix `OnaBages.Audio`, informe de fitxers bloquejats i notes de disseny escrites. Ajustat per D-019: les notes de disseny es fan a la fase 1.

## Fase 1 — Esquelet i motor 24 h mínim

- Estructura del repositori (`engine/`, `app/`, `tools/`, `docs/`), copiada de l'organització d'Ona Bages Play.
- **Còpia d'`OnaBages.Audio`** i de les peces reaprofitables de Play (D-019), amb el commit d'origen anotat. Tasques T-1, T-2 i T-3 de la revisió de Play.
- **Notes de disseny** del mesclador intern, de l'inici a l'hora exacta i del solapament (abans d'implementar-los).
- **Punts de pista** (`TrackCuePoints`: inici, mescla i final) i analitzador amb llindars configurables (−25 / −15 / −30 dB per defecte), amb proves amb àudio sintètic.
- Base de dades SQLite amb migracions: pistes, categories, subcategories i gèneres.
- **Importació de la biblioteca de RadioDJ**, amb els punts de mescla (`cue_times`) i les dates de cada pista.
- Mesclador intern amb una sortida i cua amb encadenament i solapament (D-005, D-006).
- AutoDJ amb una rotació importada de RadioDJ (cicle de passos, «la menys reproduïda» i «aleatòria»).
- Historial de reproducció i exportació del títol a `NowPlaying.txt` per a BUTT.
- Interfície mínima: pista que sona, cua i vúmetres.

**Criteri de tancament:** 48 hores seguides sonant amb AutoDJ a l'ordinador de proves, sense talls (comprovat amb `OnaBages.ToneCheck` o equivalent) i amb el títol actualitzat per a BUTT.

## Fase 2 — Planificador i puntualitat

- Esdeveniments amb modes, accions i recurrències (`docs/planificador.md`, seccions 2–4 i 12).
- Preparació anticipada i disparament a l'hora exacta (secció 7).
- Acció `STREAM` amb durada, connexió anticipada i pista de reserva (D-010).
- **Migració dels esdeveniments de RadioDJ** (secció 6), amb la correcció de l'ordre invers.
- Graella setmanal (setmanes reals i plantilla) i editor d'esdeveniments, segons la maqueta.

**Criteri de tancament:** una setmana de programació real migrada sonant a l'ordinador de proves, amb els senyals horaris a menys de 50 ms de l'hora (mesurat) i un stream que falla substituït per la pista de reserva.

## Fase 3 — Pantalla d'emissió completa

- Pantalla Emissió segons la maqueta: previsió fins al pròxim esdeveniment, franja de pròxims esdeveniments, modes AutoDJ / Assistit / Manual, entrada de línia.
- Biblioteca amb cerca contínua, cartutxeres, notes i reproductors auxiliars.
- Processador d'àudio (compressor i volum).
- Gestor de pistes: importació de carpetes, fitxa de pista, editor senzill de punts.
- Rotacions: editor, separació real, previsualització i bosses amb nom (D-014).

**Criteri de tancament:** un operador pot fer servir l'aplicació un dia sencer sense obrir RadioDJ.

## Fase 4 — Falques, campanyes i certificats

- Pistes de durada variable amb detecció de canvis (D-012).
- Calendari per pista i campanyes amb diverses falques (D-011).
- Informe de programació i certificat d'emissió en PDF, amb logotip.
- Cerca a l'historial i informes.

**Criteri de tancament:** una campanya real de diverses falques programada una sola vegada, i el seu certificat d'emissió generat a partir de l'historial.

## Fase 5 — Supervisió, avisos i integracions

- Vigilant extern del motor, detecció de pista encallada i de silencis, avisos per correu (D-013).
- Avís de programa no actualitzat; integració opcional amb l'Automatitzador (`scheduler.occurrences`, `library.fileUpdated`).
- Opcional: edició remota a la xarxa local, amb usuaris; codificador intern cap a Icecast.

**Criteri de tancament:** cada tipus d'error provocat a propòsit (motor tancat, pista encallada, silenci, stream caigut, fitxer que falta) genera el seu avís per correu i, quan toca, es recupera sol.

## Fase 6 — Prova en paral·lel i posada en marxa

- Una o dues setmanes a l'ordinador de proves, en paral·lel amb RadioDJ i amb la mateixa programació.
- Llista de diferències i correccions.
- Instal·lació al PC 24h, amb RadioDJ preparat per tornar-hi si cal.

**Criteri de tancament:** decisió de l'emissora de deixar RadioDJ.
