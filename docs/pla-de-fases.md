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
- Base de dades SQLite amb migracions: pistes, tipus de pista, categories, subcategories i etiquetes (els gèneres de RadioDJ passen a etiquetes, D-027).
- **Importació de la biblioteca de RadioDJ**, amb els punts de mescla (`cue_times`) i les dates de cada pista.
- Mesclador intern amb una sortida i cua amb encadenament i solapament (D-005, D-006).
- AutoDJ amb una rotació importada de RadioDJ (cicle de passos, «la menys reproduïda» i «aleatòria»).
- Historial de reproducció i exportació del títol a `NowPlaying.txt` per a BUTT.
- Cada element de la cua guarda d'on ve: AutoDJ, manual o esdeveniment (D-034).
- **Còpies de seguretat** automàtiques i restauració amb comprovació de la versió de l'esquema (D-032).
- Interfície mínima: pista que sona, cua i vúmetres. S'obre sense contrasenya i reprèn l'emissió (D-035).

**Criteri de tancament:** 48 hores seguides sonant amb AutoDJ a l'ordinador de proves, sense talls (comprovat amb `OnaBages.ToneCheck` o equivalent) i amb el títol actualitzat per a BUTT.

**Passos** (cadascun compila i té una prova concreta):
1. ✔ Esquelet i còpia d'`OnaBages.Audio` i `OnaBages.ToneCheck` de Play (`2f1608e`), T-1 i T-2 (D-037).
2. ✔ Notes de disseny (`docs/disseny-motor.md`, D-039): mesclador, cua i encadenament, solapament, fils, inici a l'hora exacta.
3. ✔ `TrackCuePoints` i analitzador (`CueAnalyzer`), amb proves amb àudio sintètic. Al pas 5 es compararà amb els `cue_times` de RadioDJ de pistes reals.
4. ✔ `OnaBages24.Data` (D-040): SQLite (Microsoft.Data.Sqlite), migracions, taules de pistes, classificació, historial i configuració; còpies de seguretat i restauració (D-032).
5. ✔ Importació de RadioDJ des del fitxer `.sql` del backup, amb canvi d'arrel i informe, i importació senzilla d'una carpeta local amb els punts calculats (D-041). Comparació amb els punts de RadioDJ: menys de 40 ms a la bossa 9.
6. ✔ Procés del motor, WebSocket amb testimoni i `docs/protocol.md` (D-043).
7. Mesclador, sortida i cua, en tres parts:
   - 7a. ✔ Mesclador amb veus, provat sense targeta de so (D-046).
   - 7b. Cua amb encadenament, solapament (D-024) i origen de cada element (D-034); lectura dels fitxers en fils propis.
   - 7c. Sortida WASAPI real (amb la d'emergència, D-038), `/health` segons el mesclador i recàlcul dels punts en segon pla (D-042).
8. AutoDJ amb una rotació importada; arrencada automàtica.
9. Historial i `NowPlaying.txt` amb plantilla.
10. Interfície mínima (Electron), oberta sense contrasenya (D-035).
11. Prova de 48 hores: primer a l'ordinador del desenvolupador, després a un altre ordinador de proves.

## Fase 2 — Planificador i puntualitat

- Esdeveniments amb modes, accions i recurrències (`docs/planificador.md`, seccions 2–4 i 12).
- Preparació anticipada i disparament a l'hora exacta (secció 7).
- Acció `STREAM` amb durada, connexió anticipada, salt a la pista següent si està caigut i final per silenci opcional (D-010, D-030).
- **Migració dels esdeveniments de RadioDJ** (secció 6), amb la correcció de l'ordre invers.
- Graella setmanal (setmanes reals i plantilla) i editor d'esdeveniments, segons la maqueta.

**Criteri de tancament:** una setmana de programació real migrada sonant a l'ordinador de proves, amb els senyals horaris a menys de 50 ms de l'hora (mesurat), un stream caigut que se salta sense silenci, un stream en silenci que s'acaba sol i un stream llarg que es talla dues vegades i es recupera sol (amb un servidor de proves local).

## Fase 3 — Pantalla d'emissió completa

- Pantalla Emissió segons la maqueta: previsió fins al pròxim esdeveniment, franja de pròxims esdeveniments, modes AutoDJ / Assistit / Manual, entrada de línia.
- Biblioteca amb cerca contínua, cartutxeres, notes i reproductors auxiliars.
- Processador d'àudio (compressor i volum).
- Gestor de pistes: importació de carpetes amb informe, carpetes vigilades per subcategoria (D-026), bosses renovables i assistent «Renova la bossa» (D-033), fitxa de pista, etiquetes, editor senzill de punts i canvi d'arrel de les rutes (D-025).
- Rotacions: editor, separació real, previsualització i bosses amb nom (D-014).
- Botó «Regenera la cua» (D-034).

**Criteri de tancament:** un operador pot fer servir l'aplicació un dia sencer sense obrir RadioDJ.

## Fase 4 — Falques, campanyes i certificats

- Pistes de durada variable amb detecció de canvis (D-012).
- Calendari per pista i campanyes amb diverses falques (D-011), amb l'ordre a la vista, dies propis per acció i recomptes de passis (D-028). Pla de passis de la campanya i blocs de falques cada 5 minuts (D-029).
- Informe de programació i certificat d'emissió en PDF, amb logotip i comparació entre previst i emès.
- Cerca a l'historial i informes.
- Exportacions i importacions parcials (esdeveniments, rotacions, campanyes…) amb informe de comprovació (D-032).

**Criteri de tancament:** una campanya real de diverses falques programada una sola vegada, i el seu certificat d'emissió generat a partir de l'historial.

## Fase 5 — Supervisió, avisos i integracions

- Detecció de pista encallada i de silencis, i avisos per correu enviats pel mateix motor (D-013, D-045). `/health` només respon «ok» si el mesclador funciona.
- Vigilant extern: de moment, l'Inicialitzador arrenca el motor i el torna a obrir si es tanca. Els canvis a l'Inicialitzador (o reescriure'l) es decideixen quan Ona Bages 24h estigui acabat (D-045).
- Avís de programa no actualitzat; avís opcional de l'Automatitzador quan actualitza un fitxer (`library.fileUpdated`).
- **Usuaris, rols** (administrador, programador, operador), inici de sessió per operar i **registre de canvis** (D-035).
- Opcional: edició remota a la xarxa local; codificador intern cap a Icecast.

**Criteri de tancament:** cada tipus d'error provocat a propòsit (motor tancat, pista encallada, silenci, stream caigut, fitxer que falta) genera el seu avís per correu i, quan toca, es recupera sol.

## Fase 6 — Prova en paral·lel i posada en marxa

- Una o dues setmanes a l'ordinador de proves, en paral·lel amb RadioDJ i amb la mateixa programació.
- Llista de diferències i correccions.
- Posada en marxa al PC 24h: còpia de la carpeta portable i inici automàtic amb Windows, amb RadioDJ preparat per tornar-hi si cal.
- Paquet portable (`.zip`, sense instal·lador, D-044) amb sons i rotació de mostra per a les instal·lacions noves (mode de demostració, D-038).

**Criteri de tancament:** decisió de l'emissora de deixar RadioDJ.

## Fase 7 — Fonts externes i integracions (després de la posada en marxa, D-036)

- **Entrades de línia** com a font programable, tractades com els streams (context operatiu, 2.10).
- **Integració amb l'Automatitzador:** identificador de programa compartit i pròximes emissions (estrena i repetició) per programar-ne l'execució (`scheduler.occurrences`; context operatiu, 2.7). Especificacions per detallar.
- **Directes de YouTube** (el ple de l'Ajuntament): espera del directe, connexió, detecció del final i botons manuals (context operatiu, 2.10).

**Criteri de tancament:** per concretar quan s'hi arribi.
