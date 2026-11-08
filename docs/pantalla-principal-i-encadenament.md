# Ona Bages 24h — Pantalla principal i encadenament de pistes

*Esborrany v0.5 · 4 de novembre de 2026 · A partir de captures de RadioDJ 1.8.2 en funcionament i del backup de la base de dades*

## 1. Pantalla principal

**Es manté de RadioDJ (l'operador ja hi està acostumat)**
- **Capçalera:** pista que sona, controls, *Intro / Final / Restant / Rellotge*, barra de progrés i vúmetres.
- **Cua a l'esquerra:** hora prevista de cada pista i color segons el tipus (sweeper, identificació, música).
- **Botons de mode al costat de la cua:** AutoDJ, Automàtica, Insereix, Substitueix, Esborra i **Entrada**. Entrada activa l'entrada de línia, si està habilitada a la configuració.
- **Cercador a la dreta:** filtres de categoria, subcategoria i gènere, i pestanyes (reproductor immediat, notes).
- **3 reproductors auxiliars:** el primer, per als senyals horaris.
- **Historial breu:** les últimes pistes reproduïdes. La consulta completa es fa amb la cerca de l'historial (secció 4).
- **Compte enrere fins al pròxim esdeveniment** a la barra inferior.

**Millores**
1. **Previsió fins al pròxim esdeveniment:** si el pròxim esdeveniment és de tipus TALLA, la cua marca les pistes que no arribaran a sonar i quants segons es tallaran de la que estigui sonant.
2. **Franja dels pròxims esdeveniments:** els 3–5 següents, amb el color del seu mode (TALLA, ENCUA, SUPERPOSA).
3. **Cua més compacta i de mida ajustable:** ara només s'hi veuen 6 pistes.
4. **Biblioteca sense pàgines:** ara es mostra en pàgines de 100 sobre 5.056 pistes. Hi haurà desplaçament continu i cerca a mesura que s'escriu.
5. **Tot en català:** ara hi ha textos en castellà i en anglès.
6. **Rotació i categoria de sweepers actives** visibles, indicant quin esdeveniment les ha canviat per última vegada.

## 2. Encadenament de pistes (fade i solapament)

És una de les característiques de RadioDJ que més es valoren i s'ha de mantenir.

**Com ho fa RadioDJ:** quan s'importa una pista, l'analitza i en desa els punts al camp `songs.cue_times`:

| Clau | Significat |
|---|---|
| `sta` | Inici: punt on comença el so (es salta el silenci inicial) |
| `int` | Intro (no es fa servir a l'emissora: no n'hi ha cap) |
| `xta` | Punt de mescla: on comença a sonar la pista següent |
| `end` | Final: on s'acaba la pista sortint |
| `fin` | Durada del fade d'entrada (0 a tota la biblioteca) |
| `fou` | Durada del fade de sortida (= `end` − `xta`) |

La pista sortint fa un fade de sortida entre `xta` i `end`, i la següent comença a `xta`: les dues se solapen breument.

**Dades reals de la biblioteca** (4.968 pistes de més de 90 s):
- Solapament: mediana de **1,2 s**; el 80% de les pistes, entre 0,2 i 3,8 s; màxim 27 s.
- Silenci inicial retallat: mediana de 0,15 s; el 90% de les pistes, menys de 0,8 s.

**A Ona Bages 24h**
- **Anàlisi en importar, amb els mateixos llindars que RadioDJ:** inici a **−25 dB** (primer punt on el nivell el supera), mescla a **−15 dB** (últim punt on el nivell és per sobre: hi entra la pista següent) i final a **−30 dB**. Es desen `inici`, `mescla` i `final`. L'anàlisi corre en segon pla, com el `TrackAnalyzer` d'Ona Bages Play. Els llindars són configurables.
- **Edició visual (secundària):** editor amb la forma d'ona per corregir els punts a mà. L'emissora gairebé no el fa servir perquè el mode automàtic funciona bé; n'hi ha prou amb una versió senzilla (inici, mescla i final).
- **Reproducció:** el mesclador del motor fa sonar les dues pistes alhora durant el solapament, amb una corba de fade configurable (lineal o logarítmica).
- **Regles per tipus:**
  - Els sweepers i les identificacions poden entrar "a sobre" de la intro de la pista següent.
  - Els senyals horaris i els esdeveniments TALLA tenen prioritat de puntualitat (vegeu el document del planificador, secció 7).
- **Migració:** els punts de RadioDJ s'importen tal qual (`sta` → inici, `xta` → mescla, `end` → final). Així no cal tornar a analitzar la biblioteca i es manté exactament el mateix so que ara.

## 3. Configuració actual de RadioDJ (captures del 4 de novembre de 2026)

**Importació de pistes**
- S'importa una carpeta sencera (amb subcarpetes) i s'hi assignen categoria, subcategoria, gènere (opcionalment, automàtic), tipus de pista, límit de reproduccions i prioritat (50 per defecte).
- *Auto Fades* activat: es calculen els punts automàticament. *Use ID3 CUE data* desactivat.

**Fitxa d'una pista:** ruta, categoria, subcategoria, gènere, tipus, límit de reproduccions i acció posterior, dates d'inici i de fi, prioritat, activada, "High Precision Cue Points" i "Play As Overlay".

**Editor d'esdeveniments:** tipus "Repeteix-ho per dia", "Només un dia" (data concreta), dies de la setmana, graella d'hores, categoria i llista d'accions. Accions disponibles a RadioDJ: carrega una pista, **carrega una pista de la categoria**, carrega una llista (també M3U i M3U per data), neteja, reprodueix, pausa, treu la pausa, atura, AutoDJ i plugins. Les que no es fan servir ara també s'han de poder fer a Ona Bages 24h (`PISTA_CATEGORIA`, `LLISTA`).

**AutoDJ**
- Actiu i amb reproducció automàtica en arrencar.
- Durada del fade en mescla manual: 2.500 ms. Fade en aturar: 1.000 ms.
- Rotació: no repetir pista ni artista durant **1 minut** (a la pràctica, sense regla de separació). Quan una subcategoria no té pistes disponibles, es reinicia.
- Sweepers: si la intro dura més de 120 s, el sweeper es reprodueix a l'inici de la pista.
- En reproduir una pista, la seva prioritat baixa a 0 (prioritat per defecte, 50).

**Targetes de so:** totes les sortides (principal, preescolta, pista de veu, cartutxeres, auxiliars 1–5) van a la **mateixa targeta** (Sound BlasterX AE-5 Plus, Front Speakers). No hi ha cap entrada de línia configurada. Això confirma que cal el mesclador intern del motor.

**Altres**
- Nom de la ràdio: *Ràdio Santpedor*; eslògan: *OnaBages*. Títol de l'emissió: `$artist$ - $title$`.
- Historial: es conserva 30 dies.
- Memòria intermèdia de reproducció de 850 ms; el fitxer següent es prepara 20 s abans.
- **Streaming:** el plugin *Playing Info* escriu la pista actual a `C:\RadioDJ\butt\NowPlaying.txt`. L'emissió per internet la fa un programa extern (BUTT). Ona Bages 24h ha d'exportar aquest fitxer amb plantilla; el codificador intern a Icecast passa a ser opcional.
- **Processador d'àudio:** volum 40, compressor amb el valor predefinit "Suau", equalitzador i AGC desactivats.
- Plugins actius: reproductors auxiliars, cartutxeres (reproductor immediat), historial, notes, Playing Info, llistes, generador de llistes, peticions en línia, cerca, detalls de la pista, processador d'àudio, *teaser*, gestor de pistes i utilitats.

**Conseqüències per al disseny**
1. El mesclador intern és imprescindible: tot surt per una sola targeta.
2. L'analitzador de punts ha de fer servir els llindars −25 / −15 / −30 dB per sonar igual que ara.
3. L'exportació de la pista actual a fitxer (per a BUTT) és una funció mínima obligatòria.
4. La selecció de pistes de l'AutoDJ es basa en rotacions (secció 4).

## 4. Decisions i funcions que es fan servir realment

**Respostes de l'emissora (4 de novembre de 2026)**
- **Streaming:** es continua fent servir **BUTT**. L'aplicació queda preparada per emetre ella mateixa: es podria connectar un codificador intern (`LameEncoder` → Icecast) a l'**entrada de línia**, que és el que ara capta BUTT (la sortida d'emissió de la taula, amb els directes inclosos). Vegeu el document de context operatiu, 2.4.
- **Plugins que es fan servir:** només el **processador d'àudio** i **Playing Info** (exportació del títol per a BUTT). Tots dos formen part de la primera versió. La resta (peticions en línia, *teaser*, generador de llistes, cartutxeres…) no són prioritaris.
- **AutoDJ:** tria la música a partir de les **rotacions**.

**Categories:** 12 categories (Music, Sound Effects, Sweepers, Station IDs, Jingles, Promos, Commercials, News, Interviews, Radio Shows, Radio Streams, Capsules), cadascuna amb les seves subcategories. A Music hi ha Dance90, Elektro90, Ona Bages Adult 1–9, Ona Bages Actualitat, Ona Bages Català i Ona Bages nadal. Hi ha 3 gèneres, que es fan servir per distingir els sweepers en català dels generals.

**Rotacions:** 5 rotacions (OnaBages, OnaBages Nadal, Dance90, Elektro90, OnaBages_backup). Cada rotació és un **cicle ordenat de passos** que l'AutoDJ va repetint. Cada pas té:
- categoria, subcategoria i gènere;
- **tipus de selecció**: "la menys reproduïda recentment" o "aleatòria";
- sweeper (sobre la pista);
- si s'apliquen els **criteris de repetició** (activat per a la música, no per a sweepers i jingles).

Per exemple, la rotació OnaBages té 12 passos: Adult 5 → Adult 9 → sweeper català → Català → Adult 5 → jingle → Actualitat → Adult 9 → sweeper general → Adult 5 → Adult 9 → jingle.

A RadioDJ, una rotació també pot treure pistes de les sol·licituds o d'una consulta SQL, però l'emissora no ho fa servir.

**Rotacions a Ona Bages 24h:** es manté el mateix model (cicle de passos), que funciona bé, amb aquestes millores:
1. **Separació real:** ara els criteris de repetició són d'1 minut. Es podran definir minuts mínims entre la mateixa pista i el mateix artista, amb un avís si una subcategoria es queda sense pistes disponibles.
2. **Previsualització:** abans de desar, es mostren les 20 pistes següents que triaria la rotació.
3. **Editor amb arrossegar i deixar anar** per ordenar els passos, i duplicació de rotacions (en lloc de còpies manuals com "OnaBages_backup").
4. El canvi de rotació el fa el planificador (acció `ROTACIO`), com ara.

**Informes de RadioDJ:** gairebé no es fan servir; només per comprovar si una pista ha sonat i a quina hora. Ona Bages 24h ho resol amb una cerca ràpida a l'historial (pista, artista o data → hores d'emissió). Els informes que compten de veritat són els del Programador: l'**informe de programació** i el **certificat d'emissió** de cada pista, amb logotip. Es descriuen al document del planificador (secció 10).

**Utilitats:** optimitzar la base de dades, verificar les pistes (fitxers que falten), moure pistes de carpeta, sincronitzar etiquetes ID3, actualitzar durades, restablir prioritats i recomptes, i esborrar l'historial. Totes es mantenen. Amb SQLite, la verificació de fitxers pot ser automàtica i periòdica, com ja fa Ona Bages Play.
