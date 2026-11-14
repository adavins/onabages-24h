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
   - 7b. ✔ Cua amb encadenament, solapament (D-024) i origen de cada element (D-034); lectura dels fitxers en fils propis (D-047).
   - 7c. ✔ Sortida WASAPI real (amb la d'emergència, D-038), `/health` segons el mesclador i recàlcul dels punts en segon pla (D-042, D-048). Escolta feta pel desenvolupador (2026-11-09): so net; encadenaments bastant bé. Solapament màxim de 10 s confirmat al pas 8 (D-050).
8. ✔ AutoDJ amb una rotació importada; arrencada automàtica (D-049). També anota l'historial, que cal per triar «la menys reproduïda». Escolta feta pel desenvolupador (2026-11-09): sona seguit, alterna les bosses (Adult 5, Adult 9, Català), sense repeticions i amb solapaments adequats.
9. ✔ Consulta de l'historial (`history.list`) i `NowPlaying.txt` amb plantilla (`nowplaying.get` i `nowplaying.set`) (D-051). L'historial ja s'anota des del pas 8. Prova del desenvolupador (2026-11-10): el fitxer canvia a cada cançó i BUTT el llegeix bé, amb els accents i els punts volats. Que el títol no canviï amb les cortines i els indicatius només s'ha comprovat amb les proves automàtiques, perquè en aquest ordinador no hi ha els seus fitxers; s'ha de mirar al PC 24h.
10. Interfície mínima (Electron), oberta sense contrasenya (D-035), en tres parts:
   - 10a. ✔ Motor: esdeveniment `meters` amb els nivells de la sortida i la posició de la pista que sona. Comprovat amb `prova-motor.mjs --escolta`.
   - 10b. ✔ Esquelet d'Electron (D-052): arrencada o connexió amb el motor, pista que sona, vúmetres i rellotge. Prova del desenvolupador (2026-11-10): mostra la pista, el temps i els vúmetres; en tancar la finestra la música continua; amb el motor aturat avisa, i «Arrenca el motor» el torna a engegar. Falta provar-ho per escriptori remot.
   - 10c. ✔ Cua (amb la durada de cada pista, `durationMs`), últimes pistes, AutoDJ (interruptor i rotació), saltar a la següent i barra d'estat. Les accions que interrompen la música (saltar, treure de la cua, aturar l'AutoDJ) demanen confirmació. Provat pel desenvolupador: la cua s'actualitza sola, saltar, treure, canviar de rotació, aturar i activar l'AutoDJ i la barra d'estat funcionen (Dance90 no té pistes en aquest ordinador; s'ha provat amb la bossa de Nadal). En arrencar el motor, l'AutoDJ sempre s'activa (D-053).
   Fora d'abast fins a les fases 2 i 3: esdeveniments, cerca a la biblioteca, cartutxeres, auxiliars, inserir a la cua i hora prevista de cada pista.
11. Prova de 48 hores: primer a l'ordinador del desenvolupador, després a un altre ordinador de proves. **Posposada** pel desenvolupador (2026-11-10); la fase 1 no es tanca fins que es faci. Per comprovar-la sense talls: `OnaBages24.Engine comprova-emissio` (D-082), que busca forats entre pistes a l'historial (el to de `OnaBages.ToneCheck` no serveix amb música).

## Fase 2 — Planificador i puntualitat · feta excepte els passos 7 i 9, posposats (D-072)

- Esdeveniments amb modes, accions i recurrències (`docs/planificador.md`, seccions 2–4 i 12).
- Preparació anticipada i disparament a l'hora exacta (secció 7).
- Acció `STREAM` amb durada, connexió anticipada, salt a la pista següent si està caigut i final per silenci opcional (D-010, D-030).
- **Migració dels esdeveniments de RadioDJ** (secció 6), amb la correcció de l'ordre invers.
- Graella setmanal (setmanes reals i plantilla) i editor d'esdeveniments, segons la maqueta.

**Passos**
1. ✔ Esdeveniments a la base de dades: taules `esdeveniment`, `accio`, `esdeveniment_data`, `categoria_esdeveniment` i `registre_execucio` (`planificador.md`, secció 3) i mòdul per llegir-los i desar-los.
2. ✔ Recurrències: quan sona cada esdeveniment entre dues dates (secció 12; cada hora i cada N minuts), amb `DateOnly` i els canvis d'hora de D-057.
3. ✔ Planificador bàsic al fil de control: pròxim esdeveniment, preparació 30 s abans i disparament; modes ENCUA i SISTEMA, accions `PISTA`, `ROTACIO` i `AUTODJ`; tolerància, conflictes per prioritat, registre d'execució i canvis atòmics; ordres `scheduler.*`.
4. TALLA a l'hora exacta: rellotge de mostres (hora ↔ fotograma, amb la latència de la sortida), fos de la pista que sonava al mateix fotograma, «Esborra la cua» i `NO_EMES`. Eina de mesura del retard real (objectiu: menys de 50 ms). En dues parts:
   - 4a. ✔ Tall a la mostra, rellotge de mostres, reserva de la primera pista 30 s abans, `NO_EMES` i diferència anotada al registre (D-061). Provat a l'ordinador real el 10-11-2026: fos de 3 s, tall sec i `NO_EMES`, els tres a la mostra prevista.
   - 4b. Mesura del retard real a la sortida i ajust de la latència.
     - Part 1 ✔: el motor mesura la latència amb el rellotge de la targeta i la fa servir per tallar (D-062).
     - Part 2 ✔: eina `OnaBages.ToneCheck retard` (loopback o cable) que comprova amb el so de veritat que la pista surt a l'hora prevista (D-063). Primera mesura per loopback: +4,4 ms. Falta, opcional, la prova amb cable.
5. SUPERPOSA i senyals horaris: una veu a sobre de la música, amb atenuació opcional; acció `AUXILIAR`. En dues parts (D-064):
   - 5a. ✔ Reproductors auxiliars (3) i atenuació de la música al mesclador, a la mostra exacta; ordres `aux.*`.
   - 5b. ✔ Mode SUPERPOSA i acció `AUXILIAR` al planificador (oberta 30 s abans i a la mostra de l'hora), i `retard --auxiliar` per mesurar-ho amb el so real (D-065). Primera mesura: +4,9 ms; un SUPERPOSA amb els senyals horaris ha començat a la mostra prevista.
6. ✔ Migració dels esdeveniments de RadioDJ a `importa-radiodj` (secció 6), amb informe (D-066). Provada amb el fitxer de referència i una còpia de la base de dades: 195 franges → 84 esdeveniments (35 TALLA, 31 ENCUA, 18 SUPERPOSA). L'escolta amb els fitxers de debò es farà a l'ordinador de proves (pas 9).
7. Acció `STREAM` (D-010, D-030, D-031), amb un servidor de proves local. **Posposat** fins després de la fase 3 (D-072).
8. Interfície (abans del pas 7, D-068):
   - 8a. ✔ Pestanyes Emissió | Planificador; graella per setmanes reals i setmana tipus (lectura), amb zoom, filtres per mode, desactivats, fila dels que es repeteixen cada hora i detall amb les 10 pròximes emissions i les accions.
   - 8b. ✔ Editor d'esdeveniments al panell de la dreta: tots els camps, accions (afegir, treure, reordenar, dies propis), cercador de pistes (`library.search`), pròximes emissions mentre s'edita (`scheduler.preview`), Desa, Duplica i Esborra amb confirmació (D-070).
   - 8c. ✔ Avisos a l'editor, mentre s'edita i en desar (franja massa llarga, ENCUA que no sonarà o es tallarà, durada màxima superada, emissions perdudes pel canvi d'hora, pistes que falten) (D-071). Els avisos en emissió (en preparar l'esdeveniment i quan canvia un fitxer de durada variable) queden per a la fase 4, amb la detecció de canvis als fitxers.
9. Prova de tancament (criteri de sota). **Posposat**: necessita el pas 7 i l'ordinador de proves amb els fitxers (D-072).

Les accions `ESPERA`, `ATURA`, `EXECUTA` i `PLANTILLA` s'afegeixen al pas 3 o al 6, segons el que demani la migració. Fora d'aquesta fase: `CAMPANYA` i detecció de canvis als fitxers (fase 4), `SWEEPERS` (fase 3, amb el trepitjador) i `ENTRADA` (encara no hi ha entrada).

**Criteri de tancament:** una setmana de programació real migrada sonant a l'ordinador de proves, amb els senyals horaris a menys de 50 ms de l'hora (mesurat), un stream caigut que se salta sense silenci, un stream en silenci que s'acaba sol i un stream llarg que es talla dues vegades i es recupera sol (amb un servidor de proves local).

## Fase 3 — Pantalla d'emissió completa · feta excepte el pas 9, posposat (D-101)

- Pantalla Emissió segons la maqueta: previsió fins al pròxim esdeveniment, franja de pròxims esdeveniments. Sense modes Assistit / Manual (D-073); l'entrada de línia passa a la fase 7 (D-073).
- Biblioteca amb cerca contínua, notes i reproductors auxiliars (les cartutxeres, més endavant: D-081).
- Processador d'àudio (compressor i volum).
- Gestor de pistes: importació de carpetes amb informe, carpetes vigilades per subcategoria (D-026), bosses renovables i assistent «Renova la bossa» (D-033), fitxa de pista, etiquetes, editor senzill de punts i canvi d'arrel de les rutes (D-025).
- Rotacions: editor, separació real, previsualització i bosses amb nom (D-014).
- **Rotació de reserva** (D-099): si cap pas de la rotació té pistes, la rotació de reserva configurable i, si tampoc, tota la biblioteca sense les subcategories «no de reserva» (p. ex. Nadal), sempre amb avís. (A la prova de la 10c, amb Dance90 buida, van sortir cançons de Nadal.)
- Botó «Regenera la cua» (D-034).
- **Clonar esdeveniments al planificador** (a decidir, D-080): una utilitat per facilitar la planificació, més enllà del botó «Duplica» de l'editor (que fa una còpia idèntica). Per concretar amb el desenvolupador: copiar a altres dies o hores, diverses còpies de cop, copiar una franja o un dia sencer…
- **Migració d'altres emissores amb RadioDJ** (D-080): els ajustos de la importació per a Ràdio 010, Sant Fruitós i Sallent, amb el pas 7 de la fase 2 (STREAM).

**Passos** (D-073)
1. ✔ Previsió a la cua (D-074): hora prevista de cada pista; si el pròxim esdeveniment talla, les pistes que no arribaran a sonar i quant es tallarà de la que soni; franja dels pròxims esdeveniments i compte enrere a la barra d'estat.
2. ✔ Cua i control (en tres parts: 2a ✔ cua editable i historial, D-075; 2b ✔ AutoDJ protegit, «Regenera la cua» i d'on ve la rotació, D-076 (els sweepers actius, amb el pas 5); 2c ✔ «Atura els esdeveniments», D-077): afegir (amb la biblioteca, pas 3), treure i reordenar; botó de reproduir a cada pista (salta fins allà; les d'entremig es treuen, NO_EMES si venien d'un esdeveniment); «Regenera la cua» (D-034); AutoDJ protegit (es reactiva sol al cap d'un temps i, si la cua s'acaba amb l'AutoDJ aturat, es torna a activar amb avís); «Atura els esdeveniments» amb temps límit (resultat `OMES_ATURAT`); rotació i sweepers actius visibles; l'historial tanca la pista que sonava en aturar el motor.
3. ✔ Biblioteca: el quadre de cerca de la pantalla d'Emissió (la pantalla «Biblioteca» de la maqueta és el gestor de pistes, pas 7): cerca a mesura que s'escriu (també sense accents), filtres per categoria, subcategoria i etiqueta, i afegir a la cua (D-079). En dues parts: 3a ✔ motor; 3b ✔ interfície (doble clic, «Següent», «Al final» i arrossegar a la cua).
4. ✔ Reproductors auxiliars i notes a la interfície (D-081). En dues parts: 4a ✔ motor (carregar una pista a un reproductor, `aux.load`, i el bloc de notes, `notes.get` / `notes.set`); 4b ✔ interfície (quadre dels reproductors amb ▶, ■, buidar i temps que queda; arrossegar-hi pistes des de la cerca; pestanya «Notes», que es desa sola). Les **cartutxeres queden per més endavant** (D-081).
4c. ✔ Pantalla a 1280 × 1024 i 1920 × 1080 (D-083): (a) finestra sense la barra de Windows (la capçalera en fa la feina) i zoom amb Ctrl + roda, desat al motor; (b) redistribució: la pantalla ocupa l'alçada de la finestra, l'AutoDJ a la capçalera de la cua i requadres plegables.
5. ✔ Trepitjador (D-084): sweepers a sobre de l'inici de la pista, amb atenuació, i acció SWEEPERS. Amb l'**atenuació per defecte** de D-078 per als sweepers (−8 dB, provisional); la dels auxiliars (−10 dB) i les ordres `ducking.get` / `ducking.set` ja es van fer al pas 4b. En dues parts: 5a ✔ motor (categoria de sweepers activa, `sweepers.set`, acció SWEEPERS, el sweeper a la cua i sonant al mateix fotograma que la pista, historial); 5b ✔ interfície (categoria de sweepers a la barra de l'AutoDJ, el sweeper a la cua, l'acció «Sweepers» a l'editor i el valor per defecte de l'atenuació). **Pendent: escoltar-ho** amb sweepers de debò (en aquest ordinador no hi ha els fitxers de `D:\EMISSIO`) i decidir l'atenuació (−8 dB, provisional). «A la intro», pendent (cap pista té el punt d'intro).
6. ✔ Processador d'àudio (compressor i volum) a la sortida (D-086). En dues parts: 6a ✔ motor (preajusts «Sense compressió», «Suau», «Mitjà» i «Fort», volum de sortida, sostre de seguretat sempre actiu, `processor.get` / `processor.set` i la reducció als mesuradors); 6b ✔ interfície (a la barra d'estat de l'Emissió, «Processador: Suau · 0 dB» amb una barreta de quant comprimeix; en fer-hi clic, un diàleg amb el preajust, el volum i la compressió d'ara). **Pendent: escoltar-ho** al PC 24h, comparar-ho amb RadioDJ i decidir els valors dels preajusts i el volum (ara, provisionals).
7. ✔ Gestor de pistes: fitxa editable i etiquetes, editor senzill de punts, importació de carpetes amb informe, carpetes vigilades (D-026), bosses renovables (D-033) i canvi d'arrel (D-025). En parts (D-087):
   - 7a. ✔ Pantalla «Biblioteca» i fitxa editable. Motor ✔ (`library.get` / `update` / `relink` / `delete`, `category.save`, `subcategory.save`, filtre per estat); interfície ✔ (arbre amb estats i categories, crear i reanomenar categories i subcategories, llista amb cerca, fitxa editable amb «Desa» / «Descarta», «Canvia el fitxer…» amb el diàleg de Windows i «Esborra de la biblioteca»).
   - 7b. ✔ Importació de carpetes amb informe, des de la pantalla (D-088): diàleg (carpeta, subcategoria, tipus, etiquetes, durada variable), en segon pla amb progrés i «Atura», informe en acabar, recuperació de les pistes que tornen a tenir el fitxer (o que s'han mogut: mateix nom de fitxer a la mateixa subcategoria), «Buida la subcategoria» i l'ordre `desfes-importacio`. Més ràpida (D-089): anàlisi dels punts només del principi i del final dels MP3 (mateix resultat) i dos fitxers alhora; correcció del salt exacte dels MP3 (2 fotogrames desplaçat); «Buida la subcategoria» també com a acció de l'arbre.
   - 7c. ✔ Carpetes vigilades i bosses renovables, amb l'assistent «Renova la bossa» (D-090, D-091). En tres parts: 7c-1 ✔ motor (revisió cada 2 minuts, importació, recuperació, fitxers substituïts, desaparicions amb protecció, registre i migració 005); 7c-2 ✔ interfície (botó «Carpeta vigilada» a l'arbre de la Biblioteca amb la carpeta, bossa renovable, valors per defecte, estat, registre i «Revisa ara»; marques a l'arbre; avisos a la barra d'estat); 7c-3 ✔ «Renova la bossa» (previsualització de què es manté, entra i surt; renovació en segon pla).
   - 7d. ✔ Canvi d'arrel de les rutes (D-092): des de la Biblioteca, amb la llista de carpetes on falten fitxers, «Comprova» (quantes trobaran el fitxer, exemples) i el canvi en una sola transacció.
   - 7e. ✔ Editor senzill de punts (D-093): sense escolta; forma d'ona en dB en tres vistes, punts arrossegables o escrits, «Desa» (manuals) i «Recalcula».
8. Rotacions: editor, separació real, previsualització, bosses amb nom (D-014) i rotació de reserva. En parts (D-094):
   - 8a. ✔ Editor de rotacions: pantalla «Rotacions» (llista, nova, duplica, esborra; passos que s'afegeixen, mouen i treuen), `rotations.save` / `rotations.delete`.
   - 8b. ✔ Separació real (cançó 6 h, artista 1 h, configurable; s'afluixa per ordre si cal) i previsualització de les properes 20 cançons (D-095).
   - 8c. ✔ Bosses amb nom (D-096): requadre «Bosses» a la pantalla Rotacions, passos que hi apunten i acció del planificador «Bossa» (migració 006). A més (D-097): l'AutoDJ omple uns 30 minuts de cua i la torna a triar quan canvia la rotació, una bossa o la separació.
   - 8d. ✔ Rotació de reserva i subcategories «no de reserva» (D-099): requadre «Reserva» a la pantalla Rotacions, `reserve.get` / `reserve.set`; les pistes de la reserva ho diuen a la cua i a la previsualització.
9. Prova de tancament (criteri de sota).

Afegit després (durant la fase 4): ✔ pausa i anar a un altre punt de la pista que sona, amb la previsió de la cua recalculada (D-103).

**Criteri de tancament:** un operador pot fer servir l'aplicació un dia sencer sense obrir RadioDJ.

## Fase 4 — Falques, campanyes i certificats

- Pistes de durada variable amb detecció de canvis (D-012).
- Calendari per pista i campanyes amb diverses falques (D-011), amb l'ordre a la vista, dies propis per acció i recomptes de passis (D-028). Pla de passis de la campanya i blocs de falques cada 5 minuts (D-029).
- Informe de programació i certificat d'emissió en PDF, amb logotip i comparació entre previst i emès.
- Cerca a l'historial i informes.
- Exportacions i importacions parcials (esdeveniments, rotacions, campanyes…) amb informe de comprovació (D-032).
- **Paquets de campanya per a diverses emissores** (a decidir; proposta del desenvolupador, 2026-11-12). Ara les falques es deixen en una unitat compartida de Google Drive i s'importen i es programen a mà a cada emissora. Proposta: una carpeta per campanya amb els fitxers i un `campanya.json` (falques, campanya, dates, dies, franges, passis). Des d'OB24h, **a mà** a cada emissora (no totes programen les mateixes): «Importa una campanya…» → previsualització del que es crearà i comprovacions (fitxers que falten, dates passades, si ja s'havia importat) → importar. Punts per decidir quan s'hi arribi: (1) que el `campanya.json` el generi OB24h mateix («Exporta la campanya per compartir»), no escrit a mà; (2) un identificador de campanya perquè tornar a importar-la l'actualitzi (p. ex. una falca substituïda) en lloc de duplicar-la; (3) els fitxers de Google Drive poden no ser al disc (sincronització a demanda) o tenir una altra ruta a cada ordinador: probablement copiar-los a una carpeta local de l'emissora en importar (seria una excepció a «els fitxers no es copien», D-004/CLAUDE.md) o exigir-los «disponibles sense connexió»; (4) la campanya no pot dependre d'esdeveniments concrets de cada emissora: franges horàries o blocs genèrics (D-029); (5) el certificat d'emissió és de cada emissora.

**Passos** (proposta, D-101; cada pas, en parts motor / interfície si cal)
1. ✔ Pistes de durada variable (D-012, D-102, `planificador.md` 11): abans de preparar una pista, comprovar si el fitxer ha canviat (mida i data) i tornar-ne a llegir la durada i els punts; falques i promos curtes carregades a memòria; si el fitxer és a mig escriure o no es pot llegir, l'última versió bona, amb avís.
2. ✔ Calendari d'una pista, de qualsevol tipus (falques, càpsules, promos…; D-028, D-029, D-104), a la pestanya nova «Calendari». En parts: 2a ✔ motor (graella, marcar caselles, blocs de falques, recomptes i avisos); 2b ✔ interfície (pestanya «Calendari»: cercador de pistes, graella clicable amb el color del mode i els avisos, recomptes); a més, ✔ blocs de falques automàtics per hora (una casella buida crea o amplia el bloc d'aquella hora; els buits s'esborren) i vista «Només aquesta pista» (D-105); 2c ✔ contingut de l'esdeveniment en ordre d'emissió, amb hores previstes, per moure-hi la pista; totes les hores a la graella i filtres de categoria al cercador (D-106). Contingut: graella setmanal amb una fila per a cada hora on hi ha esdeveniments; clicar una casella afegeix o treu la falca (acció PISTA) en la posició per defecte (abans del primer Programa); el contingut en ordre d'emissió per moure-la; dies propis; blocs de falques en múltiples de 5 minuts («+ Afegeix una hora»); franges ocupades i avisos; recomptes de passis.
3. ✔ Campanyes (D-011, D-029, D-122). En parts: 3a ✔ dades i ordres al motor (migració 007, `campaigns.*`, falques protegides, `campanya_id` a l'historial); 3b ✔ pla de passis i acció CAMPANYA (`CampaignPlanner`, `campaigns.plan`, historial amb la campanya); 3c ✔ interfície: pestanya «Campanyes» amb la llista i l'editor, i l'acció «Campanya» a l'editor d'esdeveniments (D-124); 3d calendari de la campanya, per programar-la a les hores que es vulgui com una pista, sense blocs fixos (D-126): 3d-1 ✔ graella setmanal amb l'acció CAMPANYA (`calendar.*` amb `campaignId`), «Calendari…» a l'editor; 3d-2 ✔ el que ha sonat (historial) i el que està previst (pla) per separat, per falca i per dia, calendari mensual i avisos dels passis contractats (D-127). Contingut: dades (migració), editor (falques amb dates, dies i pes propis; Alterna, Pes o Seqüència; estat; passis contractats), acció CAMPANYA al mateix calendari, pla de passis calculat per endavant i el motor fent sonar la falca del pla; calendari mensual i avisos.
4. Informe de programació i certificat d'emissió en PDF (`printToPDF`), amb el logotip, l'emissora i la freqüència de la configuració, i la comparació entre previst i emès (D-138). En parts: 4a ✔ motor (dades de l'emissora, `station.*`; `reports.programming` i `reports.certificate`); 4b ✔ interfície (finestra de les dades de l'emissora; «Informe de programació…» i «Certificat d'emissió…» a la campanya i a la pista, amb les dates, la vista prèvia i «Desa en PDF…»; D-139); 4c previst i emès al certificat (opcional, desactivat per defecte).
5. Cerca a l'historial i informes.
6. Calendaris desats, avisos de vigència i assistent per agrupar en campanyes les falques importades de RadioDJ.
7. Exportacions i importacions parcials (D-032), amb els paquets de campanya per a diverses emissores (a decidir).
8. Prova de tancament (criteri de sota).

**Criteri de tancament:** una campanya real de diverses falques programada una sola vegada, i el seu certificat d'emissió generat a partir de l'historial.

## Fase 5 — Supervisió, avisos i integracions

- Detecció de pista encallada i de silencis, i avisos per correu enviats pel mateix motor (D-013, D-045). `/health` només respon «ok» si el mesclador funciona.
- Vigilant extern: de moment, l'Inicialitzador arrenca el motor i el torna a obrir si es tanca. Els canvis a l'Inicialitzador (o reescriure'l) es decideixen quan Ona Bages 24h estigui acabat (D-045). L'Inicialitzador només arrenca i vigila un executable, amb arguments; segurament caldrà reescriure'l. Cal tenir-ho en compte amb D-054: si vigila la interfície i l'operador tria «Atura l'emissió i tanca», l'Inicialitzador la tornaria a obrir i tornaria a arrencar el motor. El vigilant ha de distingir una aturada voluntària d'una caiguda. Ara, amb l'Inicialitzador, per aturar-ho de debò cal tancar-lo o fer servir «Pausa monitorització» (10 minuts per a totes les aplicacions).
- Avís de programa no actualitzat; avís opcional de l'Automatitzador quan actualitza un fitxer (`library.fileUpdated`).
- **Usuaris, rols** (administrador, programador, operador), inici de sessió per operar i **registre de canvis** (D-035).
- Arrencada sense silenci: la primera vegada que el motor va obrir un fitxer d'una unitat de xarxa (`O:\musica`) va trigar 10 s a sonar; en tornar-lo a engegar, va ser immediat (prova del pas 8). Si la primera pista no s'obre en uns segons, fer sonar una pista de reserva de disc local i avisar. **Causa** (desenvolupador, 2026-11-12): era el NAS de l'ordinador de desenvolupament, que s'adorm per inactivitat i tarda uns segons a servir els fitxers en despertar-se. **El NAS de la ràdio no s'adorm**, per tant a l'emissora no s'hi espera aquest retard. La protecció continua tenint sentit per a qualsevol fitxer que no s'obri a temps (xarxa lenta, NAS reiniciant-se): la pista de reserva local també per a qualsevol pista, no només la primera.
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
- **«Dona pas a 24h» des de Play** (proposta del desenvolupador, 2026-11-14; D-125). Substitueix l'escriptori remot quan l'última part d'un programa en directe està gravada (pista de durada variable). (1) **Prepara**, minuts abans, des de Play: Play copia el fitxer a la carpeta compartida (on hi ha la pista de durada variable) i envia «prepara»; OB24h obre el fitxer, en comprova la durada, el deixa carregat a la memòria intermèdia (com el planificador 30 s abans), talla el farciment amb un fos curt i queda **«En espera de Play»**; respon «Llest» amb la durada, que Play mostra. Els errors (fitxer que falta, xarxa, OB24h que no respon) surten aquí, abans d'anar en directe, i es pot tornar a l'escriptori remot. L'operador apuja el fader del 24h com ara. (2) **Dona pas:** botó (i tecla ràpida) a Play per al «dona pas» del locutor, o automàtic en acabar una pista marcada de la llista de Play (al punt de final o de mescla, amb solapament opcional). Com que el fitxer ja és carregat, l'inici és immediat (objectiu: menys de 50 ms). (3) **Després:** sona com una pista normal (historial i registre) i en acabar continua la programació; Play mostra el temps que queda. **Per resoldre:** el motor ha d'escoltar a la xarxa local, amb testimoni propi, només l'IP de l'ordinador de directe, només les ordres prepara, dona pas i cancel·la, i mai obert a internet (canvi d'arquitectura: cal una decisió; es pot compartir amb l'edició remota de la fase 5); l'espera és un silenci volgut que la detecció de silencis no ha d'avisar, ben visible i cancel·lable des de Play i des d'OB24h; si l'ordre no arriba (ordinador de directe penjat), proposta: continua en espera i avisa al cap d'uns minuts, **no** torna sola al farciment (el fader és apujat i sonaria per sobre del directe). Cal feina a Play (connexió, botó, marca de pista, estat d'OB24h), a partir d'una especificació comuna del protocol; des d'aquí no es toca Play.

**Criteri de tancament:** per concretar quan s'hi arribi.

## Millores anotades (sense fase assignada)

- **Agenda de bosses** (demanada el 2026-11-12, després del pas 8c): programar els canvis de subcategoria de les bosses des de la pantalla de les bosses, amb una **rotació mensual automàtica**. Cas real: hi ha 9 bosses «Adult» i cada mes n'han de sonar 2, rotant d'una en una (mes 1: Adult 1 i 2; mes 2: Adult 2 i 3; mes 3: Adult 3 i 4…; després d'Adult 9 es torna a Adult 1). Per dins es pot fer amb esdeveniments BOSSA (D-096). De moment es pot fer a mà, amb un esdeveniment SISTEMA de data solta i l'acció BOSSA.
- **Manteniment de la programació** (proposta del desenvolupador, 2026-11-13): a diferència del Planificador de RadioDJ, es poden anar acumulant esdeveniments i pistes que ja no sonaran. Una pantalla d'utilitats que **detecta i proposa**, mai esborra sola: llista amb caselles, el motiu de cada element, còpia de seguretat abans i confirmació. Casos: (1) esdeveniments acabats (data de fi passada o totes les dates soltes passades) i desactivats des de fa temps; (2) esdeveniments que ja no faran res: totes les pistes fora de dates, inexistents o sense fitxer (els buits ja no n'hi pot haver: D-105, D-108); (3) esdeveniments que no guanyen mai: a la mateixa hora i dies n'hi ha un altre de més prioritat; (4) esdeveniments duplicats (mateixa hora, dies i contingut); (5) accions que apunten a rotacions, bosses o plantilles que ja no existeixen, i excepcions amb dates passades; (6) pistes candidates (sobretot falques) amb la data de fi passada que cap esdeveniment ni campanya activa no fa servir; (7) campanyes acabades encara posades als esdeveniments (ja previst al pas 3); (8) subcategories buides, plantilles que no fa servir cap esdeveniment. **Compte amb els certificats:** l'historial guarda l'id de la pista (en esborrar-la, queda a null) i el certificat per pista el necessita; una falca amb emissions dels últims 2 anys (D-011) no s'hauria d'esborrar sinó **arxivar** (fora de la biblioteca i de les cerques, però amb l'historial lligat). El registre d'execució ja copia el nom de l'esdeveniment, per tant esborrar esdeveniments no afecta l'historial. Encaixa amb els avisos de vigència (fase 4, pas 6) o amb les utilitats de la fase 5.
- **Normalització per sonoritat i canals** (conversa amb el desenvolupador, 2026-11-13; D-121). **Tot opcional i desactivat per defecte:** desactivat, el motor no mesura ni aplica res i no gasta cap recurs de més. (1) **Normalització per sonoritat (LUFS, BS.1770 amb porta)**, no per pic: un guany fix per pista perquè totes arribin a l'objectiu (com Play: −12 LUFS, com a molt +10 dB; valors a decidir escoltant). En sonar és una multiplicació per mostra. **Guany màxim** i, opcionalment, no apujar més del que permet el pic de la pista; el limitador de sortida (sostre, D-086) atura qualsevol pic que passi. No canvia les diferències dins d'una pista (rampes, intros fluixes): això seria feina de l'AGC. (2) **Mesura en segon pla, mai durant la importació** (la sonoritat obliga a descodificar el fitxer sencer; els punts només en llegeixen el principi i el final): d'una en una i amb pauses, com el recàlcul de punts (D-042), amb la possibilitat de limitar-la més (pauses més llargues o només de nit). La primera vegada, tota la biblioteca (hores, en segon pla; cal mesurar-ho al PC 24h); després, només les pistes noves i els fitxers que canvien. Migració per desar la mesura amb la mida i la data del fitxer: si el fitxer canvia (pistes de durada variable que actualitza l'Automatitzador), es torna a mesurar, i **mentre no hi ha mesura, guany 0** (sona tal com és). (3) **Exclusions per tipus de pista** (p. ex. no normalitzar els programes). (4) **Canal buit** (estèreo amb un sol canal que sona): es detecta en mesurar (`Loudness.cs`, copiat de Play: un canal per sota de −50 dB, l'altre per sobre de −40 i 30 dB de diferència) i sona en mono, amb el canal bo pels dos costats; la normalització ho té en compte. Automàtic. (5) **Desequilibri de canals** (un canal sensiblement més baix tota la pista, p. ex. més de 3 dB de mitjana): es detecta en mesurar i es mostra com a avís, amb un filtre «canals desequilibrats» a Pistes i un camp de **balanç** a la fitxa amb el valor proposat; **no es corregeix sol** (moltes gravacions antigues tenen el desequilibri volgut). La correcció és un guany fix per canal en sonar. El fitxer no es toca mai. (6) **AGC de sortida**, més endavant i només si escoltant-ho encara cal (sobretot per als streams i les entrades, que no es poden mesurar abans): sobre tota la mescla, lent, que **congela el guany** per sota d'un llindar (no puja les cues ni les pauses), amb preajusts Desactivat (per defecte), Suau, Mitjà i Fort, com el de RadioDJ. No es copia el nivellador de Play (pensat per a la veu dins d'una pista), però se'n reaprofita la mesura de sonoritat. Cada stream i cada entrada tindrien, a més, un guany propi.
- **Ajuda contextual a cada pestanya** (proposta del desenvolupador, 2026-11-14; per quan s'elabori la documentació). Perquè no calgui consultar el manual sencer: un botó d'ajuda (icona «?» i text «Ajuda») a cada pestanya (Emissió, Planificador, Pistes, Rotacions, Campanyes) i a les vistes de dins (calendari d'una pista i d'una campanya, editor d'esdeveniments…), que obre una **finestra modal gran** amb el funcionament d'aquella pantalla: per a què serveix, què vol dir cada part, els colors i les icones, les accions més habituals pas a pas i els avisos que hi poden sortir. Proposta de com fer-ho: el text de cada ajuda en un fitxer propi dins de la interfície (p. ex. `app/renderer/ajuda/campanyes.html`), escrit en català i sense dependre de connexió, perquè sigui fàcil de mantenir al dia quan canviï la pantalla; el manual complet es pot fer amb els mateixos textos. Encara no hi ha cap fase de documentació al pla: encaixa amb la fase 6 (posada en marxa), o abans si es vol anar fent pantalla per pantalla.
