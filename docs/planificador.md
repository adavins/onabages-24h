# Ona Bages 24h — Planificador: esquema de dades i ordres del motor

*Esborrany v0.8 · 5 de novembre de 2026*

**Abast:** una instal·lació per emissora, igual que ara. L'aplicació només gestiona una emissora. El nom de l'emissora és un paràmetre de configuració que surt als informes; substitueix `emissores.json`.

## 1. Principis

- **Hora lliure:** un esdeveniment comença a qualsevol hora (precisió de segon), no en franges fixes de 30 minuts.
- **Graella només visual:** la vista setmanal es manté (amb zoom de 30/15/5 minuts i ajust automàtic a 5 minuts quan s'arrossega), però només dibuixa els esdeveniments que existeixen. No hi ha cel·les buides a la base de dades.
- **Comportament explícit:** cada esdeveniment declara un **mode**. No s'ha d'endevinar a partir de les ordres que conté.
- **Ordre real d'emissió:** les pistes es desen i es mostren en l'ordre en què sonaran.
- **Aplicació atòmica:** el motor aplica cada esdeveniment de cop. Desapareix el pegat *Assisted Enable → … → Assisted Disable*.
- **L'execució és del motor:** el planificador corre dins del procés en C#. Si la interfície es penja, la programació continua.

## 2. Modes d'esdeveniment

| Mode | Què fa a l'hora programada | Equivalent a RadioDJ |
|---|---|---|
| **TALLA** | Talla la pista actual (amb el fade configurat) i el contingut de l'esdeveniment comença a sonar de seguida: la primera pista, a l'hora exacta (secció 7). | `Load…|Top` + `Play!` |
| **ENCUA** | Insereix el contingut com a següent a sonar. La pista actual continua. | `Load Track By ID…|Top` sense `Play!` |
| **SUPERPOSA** | Reprodueix pel reproductor auxiliar per sobre del que sona, amb atenuació (*ducking*) opcional. | `PluginAction|Auxiliary Players…` |
| **SISTEMA** | Només fa accions sense àudio: canviar la rotació o els sweepers, executar una aplicació, etc. | `Load Rotation`, `Run Application`… |

**Tallar i esborrar la cua són dues coses diferents (D-022).** Cada esdeveniment TALLA o ENCUA té l'opció **«Esborra la cua»** (`neteja_cua`), independent del mode. Si està marcada, les pistes que esperaven a la cua s'esborren abans d'inserir-hi el contingut de l'esdeveniment; si no, el contingut es posa al davant i la resta de la cua continua darrere. Les quatre combinacions són possibles:

| | Esborra la cua | No esborra la cua |
|---|---|---|
| **TALLA** | El cas habitual de les hores en punt (avui, tots els talls de RadioDJ) | El contingut sona a l'hora i després continua el que ja hi havia a la cua |
| **ENCUA** | Quan acaba la pista actual sona el contingut; el que hi havia a la cua es descarta | El cas habitual de les mitges hores |

En crear un esdeveniment, l'editor marca «Esborra la cua» per defecte als TALLA i la deixa desmarcada als ENCUA; es pot canviar sempre. És una opció de l'esdeveniment i no una acció de la llista perquè només té sentit en un moment (abans d'inserir el contingut); així no hi ha combinacions confuses segons on es posi.

Opcions per mode:
- **TALLA:** `fade_ms`, `neteja_cua`, i `marge_seg` (si a la pista actual li queden menys de N segons, s'espera que acabi). `marge_seg` està desactivat per defecte i no es fa servir als esdeveniments de la graella, perquè endarreriria l'hora.
- **ENCUA:** `neteja_cua`.
- **SUPERPOSA:** `ducking_db` i el reproductor auxiliar que es fa servir.

**Contingut de l'esdeveniment:** totes les pistes es tracten igual i sonen en l'ordre de la llista d'accions. La locució de l'hora (`hores\NN.mp3`) és una pista més: es pot programar o no, i en qualsevol posició.

### 2.1. Com es fa servir a l'emissora (D-021)

La graella de programació s'ha de complir rigorosament. A les dades de RadioDJ hi ha tres maneres de fer-ho:

- **Hores en punt amb tall (TALLA):** a l'hora, es talla el que soni (encara que sigui el programa anterior) i comença el contingut de l'esdeveniment. Per exemple, el diumenge a les 15:00: locució `15.mp3` → «Sons a cau d'orella»; a les 16:00: `16.mp3` → «El club del country». Només la **primera pista** ha de sonar a l'hora (secció 7). Les que vénen al darrere (locucions, falques, promos) fan que el programa entri uns segons més tard, i ja està previst així. Els programes s'han d'ajustar a la seva franja: si duren més, es tallen pel final.
- **Hores o mitges hores amb senyals horaris (SUPERPOSA):** quan en aquell moment no ha de sonar res més que els senyals, sonen pel reproductor auxiliar per sobre de la música, sense tallar-la ni afegir res a la cua. El fitxer comença a l'hora en punt (no s'avança perquè l'últim pip hi coincideixi).
- **Mitges hores amb continguts que s'encuen (ENCUA):** promos o falques que es posen com a pròximes pistes, sense tallar el que sona.

**Regles**
- **Un esdeveniment que esborra la cua** (normalment, un TALLA) fa que no soni el contingut d'un ENCUA anterior que encara hi esperava. Queda anotat al registre com a no emès (`NO_EMES`), i no compta per als certificats d'emissió, que es fan amb el que ha sonat de veritat. Si el TALLA no esborra la cua, aquest contingut sona després del del TALLA.
- **Programa més curt que la franja:** l'AutoDJ omple el temps amb música fins al pròxim esdeveniment.
- **Programa més llarg que la franja:** es talla a l'hora del pròxim TALLA.

**Avisos** (com més, millor: qui programa ha de saber què passarà abans que passi)
- **A l'editor, en desar:** per a cada esdeveniment, es calcula la durada del seu contingut i es compara amb el pròxim TALLA de cada dia en què sona. S'avisa:
  - si un ENCUA no arribarà a sonar perquè el contingut d'abans (un programa, un altre ENCUA) dura més que el temps que queda fins al pròxim esdeveniment que esborra la cua;
  - si el contingut d'un TALLA és més llarg que la seva franja (p. ex. «Sons a cau d'orella dura 61:20 i se'n tallaran 1:20»).
- **En emissió:** quan es prepara un esdeveniment (uns 30 s abans) i quan un fitxer de durada variable canvia (secció 11), es tornen a fer els mateixos càlculs amb les durades reals. La cua marca les pistes que no arribaran a sonar (`pantalla-principal-i-encadenament.md`, secció 1, millora 1).
- Els avisos d'emissió van al sistema d'avisos (`context-operatiu.md`, 2.3).

## 3. Esquema de dades (SQLite)

```sql
-- Esdeveniment programat (o plantilla, si es_plantilla = 1)
CREATE TABLE esdeveniment (
  id              INTEGER PRIMARY KEY,
  nom             TEXT NOT NULL,
  mode            TEXT NOT NULL CHECK (mode IN ('TALLA','ENCUA','SUPERPOSA','SISTEMA')),
  hora_inici      TEXT NOT NULL,            -- 'HH:MM:SS'
  -- Quins dies (secció 12)
  recurrencia     TEXT NOT NULL DEFAULT 'SETMANAL'
                  CHECK (recurrencia IN ('SETMANAL','CADA_N_SETMANES','MENSUAL_ORDINAL','DIES_MES','DATES')),
  dies_mask       INTEGER NOT NULL DEFAULT 127, -- bit0 = dilluns … bit6 = diumenge
  interval_setm   INTEGER,                  -- CADA_N_SETMANES: 2 = cada 15 dies
  data_ancora     TEXT,                     -- CADA_N_SETMANES: un dia en què segur que toca
  ordinals        TEXT,                     -- MENSUAL_ORDINAL: '1,3' o 'last'
  dies_mes        TEXT,                     -- DIES_MES: '1,15,last'
  data_inici      TEXT,                     -- 'YYYY-MM-DD'; null = sense límit
  data_fi         TEXT,
  -- Repetició dins del dia
  repeticio       TEXT NOT NULL DEFAULT 'CAP' CHECK (repeticio IN ('CAP','CADA_HORA','CADA_N_MIN')),
  interval_min    INTEGER,                  -- per a CADA_N_MIN
  hores_mask      INTEGER,                  -- 24 bits; per a CADA_HORA (null = totes)
  prioritat       INTEGER NOT NULL DEFAULT 50,
  tolerancia_seg  INTEGER NOT NULL DEFAULT 60, -- si s'arriba tard més d'aquest temps, s'omet
  fade_ms         INTEGER,
  marge_seg       INTEGER,
  ducking_db      REAL,
  durada_max      INTEGER,                  -- segons; durada màxima prevista del contingut (D-028)
  color           TEXT,
  categoria_id    INTEGER REFERENCES categoria_esdeveniment(id),
  neteja_cua      INTEGER NOT NULL DEFAULT 0, -- 1 = esborra la cua abans d'inserir el contingut (D-022)
  bloc_falques    INTEGER NOT NULL DEFAULT 0, -- 1 = bloc de falques: surt com a fila al calendari de falques (D-029)
  es_plantilla    INTEGER NOT NULL DEFAULT 0,
  actiu           INTEGER NOT NULL DEFAULT 1,
  notes           TEXT,
  modificat       TEXT NOT NULL
);

-- Llista ordenada d'accions (en ordre real d'execució i emissió)
CREATE TABLE accio (
  id               INTEGER PRIMARY KEY,
  esdeveniment_id  INTEGER NOT NULL REFERENCES esdeveniment(id) ON DELETE CASCADE,
  ordre            INTEGER NOT NULL,
  tipus            TEXT NOT NULL,           -- vegeu la secció 4
  params           TEXT NOT NULL DEFAULT '{}', -- JSON
  dies_mask        INTEGER                  -- null = tots els dies de l'esdeveniment; si no, només aquests (D-028)
);

-- Dates soltes (recurrencia = 'DATES') i excepcions (dates en què no s'emet)
CREATE TABLE esdeveniment_data (
  esdeveniment_id  INTEGER NOT NULL REFERENCES esdeveniment(id) ON DELETE CASCADE,
  data             TEXT NOT NULL,           -- 'YYYY-MM-DD'
  tipus            TEXT NOT NULL CHECK (tipus IN ('INCLOU','EXCLOU')),
  PRIMARY KEY (esdeveniment_id, data)
);

CREATE TABLE categoria_esdeveniment (id INTEGER PRIMARY KEY, nom TEXT NOT NULL UNIQUE COLLATE NOCASE, color TEXT);

-- Registre d'execució (auditoria i informe)
CREATE TABLE registre_execucio (
  id               INTEGER PRIMARY KEY,
  esdeveniment_id  INTEGER,                 -- sense clau forana: el registre es conserva si l'esdeveniment s'esborra
  nom              TEXT NOT NULL,           -- nom de l'esdeveniment en aquell moment
  programat_per    TEXT NOT NULL,           -- data i hora previstes
  executat_a       TEXT,
  resultat         TEXT NOT NULL,           -- OK | OMES_TARD | OMES_CONFLICTE | OMES_CANVI_HORA | OMES_ATURAT | NO_EMES | ERROR
                                            -- OMES_ATURAT: els esdeveniments estaven aturats a mà (D-073)
                                            -- OMES_CANVI_HORA: l'hora no existeix aquell dia (pas a l'hora d'estiu, D-057)
                                            -- NO_EMES: contingut d'un ENCUA esborrat per un TALLA abans de sonar (2.1)
  detall           TEXT
);
```

**Conflictes:** quan dos esdeveniments TALLA o ENCUA coincideixen al mateix instant, s'executa el de prioritat més alta i l'altre queda al registre com a `OMES_CONFLICTE`. Els esdeveniments SUPERPOSA poden coexistir amb qualsevol altre.

**Graella:** cada targeta es dibuixa des de `hora_inici` fins a `durada_max`. Si no hi ha `durada_max`, es dibuixa com una marca puntual.

**Durada màxima (`durada_max`, D-028):** els programes són de durada variable, i la durada del fitxer d'avui no diu quant durarà el de la setmana que ve. Per això l'esdeveniment guarda quant pot durar com a màxim el seu contingut (p. ex. 55 min). Serveix per dibuixar el bloc, per marcar les franges ocupades al calendari de les falques (secció 10) i per avisar: si el contingut real (amb la durada actual dels fitxers) la supera, surt un avís, i si a més passa del pròxim TALLA, l'avís de D-021. Es posa a mà a l'editor; si està buida, l'editor proposa la durada actual del contingut. A la migració es calcula a partir dels esdeveniments desactivats de RadioDJ que el segueixen (secció 6).

**Pistes només alguns dies (`accio.dies_mask`, D-028):** dins d'un esdeveniment de diversos dies (p. ex. de dilluns a divendres a les 15:00), cada acció pot sonar només alguns d'aquests dies. Així una falca pot anar «només dimarts a les 15:00» sense separar l'esdeveniment. L'editor mostra els dies de l'acció al costat de la pista, i el calendari de la falca els posa sol. Els esdeveniments recurrents (per exemple, cada hora) es poden ocultar o mostrar en una fila pròpia.

## 4. Tipus d'acció

| `tipus` | `params` | Origen a RadioDJ |
|---|---|---|
| `PISTA` | `{songId}` | `Load Track By ID` |
| `STREAM` | `{songId, durada_seg, fade_ms, silenci_seg?, reconnecta?, tornada?, reserva_songId?}` | `Load Track By ID` d'una URL + `Seconds To Wait`. Connexió anticipada i tall automàtic. Si està caigut, se salta a la pista següent (la reserva és opcional); amb `silenci_seg`, s'acaba si arriba en silenci (D-030). Amb `reconnecta`, omple amb música i torna al stream quan es recupera, tantes vegades com calgui dins de la durada (D-031). El stream és una pista de la biblioteca de tipus Stream (`songId`; l'adreça és la seva ruta, D-108); els esdeveniments d'abans poden portar l'adreça escrita (`url`). `durada_seg` és obligatòria. Vegeu el document de context operatiu, 2.2 |
| `CAMPANYA` | `{campanyaId}` | Nou: sona la falca que indica el pla de passis de la campanya (secció 10, D-029) |
| `PLANTILLA` | `{plantillaId}` | Esdeveniment arxivat (s'insereixen les seves accions) |
| `AUXILIAR` | `{songId, player, duckingDb?}` | `PluginAction|Auxiliary Players|Play File From Database` |
| `ROTACIO` | `{rotacioId}` | `Load Rotation` |
| `SWEEPERS` | `{subcategoriaId}` | `Set Sweeper Category` |
| `AUTODJ` | `{actiu}` | `Assisted Enable!` / `Assisted Disable!` (només quan es vol de veritat, no com a pegat) |
| `ESPERA` | `{segons}` | `Seconds To Wait` |
| `ATURA` | `{fadeMs?}` | `Stop!` |
| `ENTRADA` | `{silencia}` | Silenciar / treure el silenci de l'entrada. Més endavant es reconvertirà en una font programable com els streams (fase 7, D-036; context operatiu, 2.10) |
| `EXECUTA` | `{ruta, args}` | `Run Application` |
| `REPRODUEIX` | `{}` | `Play!`: avançada, per a casos especials. Normalment la implica el mode. (`Clear Playlist!` passa a ser l'opció «Esborra la cua» de l'esdeveniment, D-022.) |

## 5. API del motor

Es fa servir el mateix transport i format que Ona Bages Play (`docs/protocol.md`): WebSocket a `127.0.0.1` amb testimoni, i missatges `{ "v": 1, "id": …, "cmd": "àmbit.acció", … }`. Les ordres de sota s'hi afegeixen com a àmbits nous.

**Ordres (interfície o planificador → motor)**

| Ordre | Paràmetres |
|---|---|
| `queue.replace` | `items[]`, `fadeMs`, `startNow` |
| `queue.insert` | `items[]`, `position`: `next` \| `end` \| índex |
| `queue.remove` / `queue.move` / `queue.clear` | — |
| `player.play` / `player.stop` / `player.next` | `fadeMs?` |
| `aux.play` | `songId`, `player`, `duckingDb?` |
| `autodj.set` | `enabled`, `rotationId?` |
| `sweepers.setCategory` | `subcategoryId` |
| `input.mute` | `muted` |
| `system.run` | `path`, `args` |
| `status.get` | — |
| `scheduler.reload` | — (després de desar canvis a la interfície) |

**Esdeveniments (motor → interfície)**

`player.trackStarted`, `player.trackEnded`, `queue.changed`, `autodj.trackAdded`, `scheduler.fired {esdevenimentId, resultat}`, `levels {L, R}` (vúmetres), `engine.error`.

Les ordres `queue.replace` i `queue.insert` són **atòmiques**: l'AutoDJ no pot afegir pistes mentre s'apliquen.

## 6. Migració des de RadioDJ

1. Llegir la taula `events`: `type = 1` són els esdeveniments setmanals de la graella (`day` = `&1`…`&6` per a dilluns–dissabte i `&0` per a diumenge; `time` = hora).
2. Ometre els que tenen `data` buit (142 de 336 al backup del gener del 2026). Abans, però, fer servir els **desactivats** per calcular la `durada_max` del TALLA que tenen al davant: les franges desactivades seguides que vénen darrere d'un TALLA són les que ocupa el programa (D-028). Si no n'hi ha cap, `durada_max` queda buida.
3. Deduir el mode: si hi ha `Play!`, **TALLA**; si només hi ha `PluginAction|Auxiliary Players`, **SUPERPOSA**; si només hi ha `Load…|Top`, **ENCUA**. Si hi ha `Clear Playlist!`, `neteja_cua = 1` (al backup, els 79 esdeveniments amb `Play!` tenen també `Clear Playlist!`).
4. Treure les parelles `Assisted Enable!` / `Assisted Disable!` que embolcallen el muntatge de la cua, i treure els `Clear`/`Play` (ja recollits al mode i a `neteja_cua`).
5. **Invertir l'ordre** dels `Load Track By ID…|Top` consecutius perquè quedin en ordre d'emissió.
6. Agrupar esdeveniments idèntics en dies diferents en un de sol amb `dies_mask` (fet; l'agrupament per falques no es fa de moment perquè a les dades no n'hi ha cap cas, D-066). Per exemple, les 15:00 de dilluns a divendres passen a ser 1 esdeveniment en lloc de 5, si el contingut coincideix. Si només es diferencien en falques (pistes de tipus Falca) que hi ha uns dies sí i uns altres no, també s'agrupen, i aquestes pistes porten els seus dies a `accio.dies_mask` (D-028). L'informe de migració llista els agrupaments perquè es puguin revisar.
7. Els esdeveniments `type = 2` (amb màscara de dies i hores) passen a `repeticio = CADA_HORA` amb `hores_mask`.

## 7. Puntualitat

**Requisit:** el primer element de l'esdeveniment ha de sonar exactament a l'hora programada. L'objectiu és un error inferior a 50 ms, que l'oïda no percep.

Com s'aconsegueix:
1. **Preparació anticipada:** uns 30 segons abans de l'hora, el motor obre i descodifica el primer element de l'esdeveniment i el deixa a punt en un reproductor de reserva. Així no hi ha retard per llegir el fitxer.
2. **Punt d'entrada:** es fa servir el *cue-in* de la pista (salt automàtic del silenci inicial), perquè el so comenci justament a l'hora i no uns mil·lisegons més tard. Per començar des d'un punt de la pista es fa servir el salt exacte d'`OnaBages.Audio` (`Mp3Seek`, D-155 d'Ona Bages Play), també amb els MP3 de bitrate variable.
3. **Rellotge d'alta resolució:** el disparament fa servir `Stopwatch` sobre l'hora del sistema, no un temporitzador de la interfície. Els últims mil·lisegons es compensen amb la latència coneguda de la sortida WASAPI.
4. **Tall a l'hora exacta (D-021):** en el mode TALLA, la primera pista de l'esdeveniment comença a l'hora en punt i, en aquell mateix moment, la pista que sonava comença el seu fos de sortida (`fade_ms`), de manera que les dues se solapen durant el fos. El fos no s'avança ni s'escurça. `marge_seg` es deixa desactivat.
5. **Hora del sistema sincronitzada:** cal configurar el servei d'hora de Windows (NTP) amb sincronització freqüent. L'aplicació avisa si detecta una desviació superior a 1 segon.

Els esdeveniments ENCUA no necessiten aquesta precisió: s'apliquen quan toca i sonen quan acaba la pista actual.

## 8. Preparat per a l'edició remota

Ara no cal, però amb l'arquitectura d'Ona Bages Play ja és gairebé gratuït:
- **Tota la lògica és al procés en C#:** base de dades, planificador i motor. La interfície no accedeix mai directament a la base de dades.
- **La interfície ja parla per WebSocket** amb testimoni. És el mateix protocol que faria servir un navegador remot.
- **Per activar-ho:** el motor escolta també a la xarxa local (opció de configuració), amb usuari i contrasenya i HTTPS, i serveix els mateixos fitxers HTML/JS de la interfície. No cal reescriure res.

## 9. Reaprofitament d'Ona Bages Play

Ona Bages Play ja té l'arquitectura que necessita Ona Bages 24h: **Electron + HTML + Bootstrap 5 + JS pur** per a la interfície i un **motor en C# / .NET 10 amb NAudio** (WASAPI) en un procés separat, comunicats per WebSocket. Es manté aquesta arquitectura (substitueix la proposta de WebView2).

**Es reaprofita tal qual**
- `OnaBages.Audio`: lectura de fitxers (Media Foundation), sortida WASAPI, memòria intermèdia, encadenament sense silenci, volum amb rampa, mesuradors, detecció de dispositius, `TrackInfo` (TagLibSharp), preescolta (`CuePlayer`).
- `LameEncoder`: base per al codificador MP3 de l'streaming a Icecast.
- Motor: servidor WebSocket (Kestrel), `ClientHub`, `CommandRouter`, configuració amb escriptura atòmica, revisió del maquinari en arrencar, registre de talls i errors.
- Interfície: `engine-client.js`, `store.js`, `states.js`, mesuradors, faders, selector de dispositius, menú contextual, zoom.
- Eines de proves: `OnaBages.ToneCheck` i la consola de proves. Són molt útils per validar les proves de 48 h.

**Cal afegir per al 24/7** (Ona Bages Play és per a directe amb operador, decisió D-001)
1. **Mesclador propi:** ara cada reproductor obre la seva sortida i Windows els barreja. Per a crossfades, sweepers superposats i *ducking*, cal un mesclador intern per sortida (`MixingSampleProvider` amb diverses fonts).
2. **Fades i crossfade** entre pistes, amb punts d'entrada i de sortida (*cue-in*, intro, *cue-out*) per pista, i detecció automàtica de silencis.
3. **Disparament programat** amb la precisió de la secció 7. Ara `Play` espera fins a 1 s que hi hagi 0,5 s preparats, i això s'ha de substituir per una preparació anticipada.
4. **Base de dades** (SQLite): biblioteca de pistes, categories, rotacions, historial i planificador. Ona Bages Play només fa servir fitxers JSON (D-005 ja preveia SQLite si calia).
5. **AutoDJ i rotacions** amb regles de no repetició.
6. **Streaming a Icecast/Shoutcast** (opcional; ara ho fa BUTT): codificació de l'entrada de línia amb `LameEncoder`. Vegeu el document de context operatiu, 2.4.
7. **Supervisió desatesa:** vigilant del procés, reinici automàtic del motor i pista d'emergència si no hi ha res a sonar.

**Com es reaprofita (D-019):** Ona Bages 24h és independent de Play. A l'inici de la fase 1 es copia `OnaBages.Audio` (i la resta de peces d'aquesta llista) una sola vegada, i a partir d'aquí cada projecte evoluciona pel seu compte. Es deixa anotat el commit de Play d'on surt la còpia, per poder revisar de tant en tant si alguna correcció de Play també convé aquí.

## 10. Falques: calendari per pista, informe de programació i certificat d'emissió

**Com ho fa ara el Programador** (vista d'una pista, `views/song.ejs` i `routes/songs.js`):
- **Calendari de la pista:** sobre la graella setmanal es marquen les franges on ha de sonar una pista (normalment una falca). En desar, s'afegeix o es treu la pista de l'esdeveniment de RadioDJ de cada franja. Es poden desar calendaris i reutilitzar-los ("Calendaris desats": artista, títol, data d'inici, nombre d'hores).
- **Informe de programació d'emissions (PDF):** logotip, emissora, freqüència, data de l'informe, dates d'inici i de fi, durada, nom de la pista, nombre de passis i la graella amb les franges programades.
- **Certificat d'emissió (PDF):** logotip, emissora, freqüència, interval de dates, nombre de passis i la llista de cada emissió real (data i hora, artista o anunciant, títol), extreta de l'historial.

És una funció imprescindible: serveix per justificar davant dels anunciants que les falques han sonat.

**Limitacions del Programador** (revisió del codi, novembre del 2026):
- La falca s'insereix just després de `Clear Playlist!` (o al principi). Amb `Load…|Top`, la primera pista que es carrega sona **l'última**: en un tall de programa, la falca sonaria darrere del programa i el tall següent l'esborraria. Des del Programador no es veu l'ordre; cal anar al calendari d'esdeveniments.
- Les franges ocupades per un programa s'indiquen amb esdeveniments buits i desactivats, mantinguts a mà.
- Es programen fitxers separats, no campanyes.
- Els recomptes multipliquen el patró setmanal pels dies del període; no hi ha recurrències ni excepcions.
- El certificat busca les emissions per **artista i títol** (no per identificador), el «núm. passis» compta tot l'historial i no només el de les dates triades, i RadioDJ només guarda **30 dies** d'historial: les campanyes més llargues perden les primeres emissions.

**A Ona Bages 24h** (D-011, D-028)
- **Vista "Calendari de la pista o de la campanya":** la mateixa manera de treballar que ara: una graella setmanal on es cliquen les caselles on ha de sonar. Marcar o desmarcar una casella afegeix o treu l'acció `PISTA` (o `CAMPANYA`) de l'esdeveniment corresponent.
  - **Files = hores on hi ha esdeveniments (D-029):** no hi ha una graella fixa de 30 minuts. Cada fila és una hora de la setmana on hi ha algun esdeveniment que pot portar falques: els talls de les hores en punt, els ENCUA de les mitges hores i els **blocs de falques** (esdeveniments ENCUA amb `bloc_falques = 1`). Si un dia no hi ha esdeveniment a aquella hora, la casella surt desactivada.
  - **«+ Afegeix una hora»:** crea un bloc de falques nou en **múltiples de 5 minuts** (p. ex. 8:05, 8:35). Així el calendari només té les files que es fan servir (unes 30–40), no 48 ni 288.
  - Un bloc de falques és un ENCUA: sona quan s'acaba la pista que hi ha en aquell moment, no a l'hora exacta.
  - A la graella del planificador, els blocs es dibuixen com a marques curtes amb el color del tipus Falca.
  - Cada casella mostra l'esdeveniment que hi ha, amb el color, la icona i el text del mode.
  - **Franges ocupades:** es calculen soles amb la `durada_max` dels TALLA (secció 3). Es poden marcar igualment, però amb un avís. No cal mantenir esdeveniments desactivats.
  - **L'ordre a la vista:** en clicar una casella, un panell lateral mostra el contingut de l'esdeveniment en ordre d'emissió, amb l'hora prevista de cada pista i la falca destacada, i s'hi pot moure.
  - **Posició per defecte:** just abans de la primera pista de tipus Programa; si no n'hi ha, al final del contingut. Així la falca no queda mai darrere d'un programa.
  - **Dies propis:** si l'esdeveniment és de diversos dies i la falca només ha de sonar alguns d'aquests dies, l'acció porta el seu `dies_mask` (secció 3). Si la falca s'ha de posar en tots els dies de l'esdeveniment, no en porta.
  - **Avisos:** si en aquella posició el pròxim TALLA l'esborraria (D-021), o si la franja és ocupada per un programa.
- **Recomptes de passis** (com ara): per dia de la setmana, setmana natural i dins del període, mes natural i dins del període, i total del període. Es calculen dia a dia amb les dates reals: recurrències (secció 12), excepcions, dies propis de l'acció i dates de la pista, de la campanya i de cada falca. En una campanya, també per falca.
- **Calendaris desats** (com ara): una distribució de franges amb nom, que es pot aplicar a una altra pista o campanya. Botó «Temporada» amb les dates de la temporada a la configuració.
- Una pista amb esdeveniments, campanyes o calendaris desats no es pot esborrar (com ara).
- **Avisos de vigència** (com ara): pista o campanya vençuda, que comença aviat, dates incorrectes o desactivada.
- **Campanya amb diverses falques:** la campanya és l'element que es programa, no la falca. Es programa una sola vegada, amb la mateixa vista de calendari que una pista i l'acció `CAMPANYA` en lloc de `PISTA`.
  - Cada falca de la campanya pot tenir **dates pròpies** dins de les de la campanya, i **dies de la setmana** propis. Canviar de falca a mitja campanya es fa amb aquestes dates.
  - **Repartiment** entre les falques vàlides de cada passi: **Alterna** (a parts iguals), **Pes** (p. ex. A el doble que B) o **Seqüència** (sempre A, B, C, A, B, C…, per a falques que són capítols).
  - **Pla de passis calculat per endavant (D-029):** en desar la campanya, l'aplicació calcula quina falca sona a cada passi i ho desa a `campanya_passi`. El repartiment també s'equilibra per hora, perquè una falca no surti sempre al matí i l'altra sempre al vespre. Si la campanya o els esdeveniments canvien, només es recalculen els passis futurs. En emetre, el motor fa sonar la falca del pla; si no la pot fer sonar (fitxer que falta), fa sonar una altra falca vàlida de la campanya i ho anota.
  - **Recomptes en temps real:** mentre es marquen caselles o es canvien dates, la pantalla mostra els passis totals, per dia, per setmana i per falca. Opcionalment es poden indicar els **passis contractats** (totals i per dia) i l'aplicació avisa si el pla en té més o menys.
  - **Calendari mensual de la campanya:** cada dia mostra quantes vegades sonarà cada falca, amb el color de la falca.
  - **Avisos:** dies sense cap falca vàlida (el passi quedarà buit), la mateixa campanya dues vegades en el mateix esdeveniment, passis contractats que no quadren.
  - **Estat:** esborrany, activa o acabada.
  - Un passi que no s'emet **no es recupera**: queda al certificat com a no emès.
  - Exemple: campanya "Ferreteria" de l'1 al 30 de novembre, de dilluns a divendres a les 8:30, 13:30 i 19:30. Les falques A i B s'alternen tot el mes, i la falca C ("Black Friday") només sona del 24 al 28 amb pes 2. Es programa una sola vegada.
- Una campanya d'**una sola falca** és el cas més simple: funciona igual que programar la pista directament.
- **Per a més endavant:** avís si dues campanyes del mateix sector (p. ex. dues ferreteries) coincideixen en el mateix esdeveniment. Caldria un camp `sector` a la campanya.

```sql
CREATE TABLE campanya (
  id          INTEGER PRIMARY KEY,
  nom         TEXT NOT NULL,
  anunciant   TEXT,
  data_inici  TEXT NOT NULL,   -- 'YYYY-MM-DD'
  data_fi     TEXT NOT NULL,
  seleccio    TEXT NOT NULL DEFAULT 'ALTERNA' CHECK (seleccio IN ('ALTERNA','PES','SEQUENCIA')),
  estat       TEXT NOT NULL DEFAULT 'ESBORRANY' CHECK (estat IN ('ESBORRANY','ACTIVA','ACABADA')),
  passis_contractats_total INTEGER,   -- opcional; només per avisar
  passis_contractats_dia   INTEGER,
  notes       TEXT
);

-- Pla de passis (D-029): quina falca toca a cada passi previst
CREATE TABLE campanya_passi (
  campanya_id     INTEGER NOT NULL REFERENCES campanya(id) ON DELETE CASCADE,
  esdeveniment_id INTEGER NOT NULL,
  programat_per   TEXT NOT NULL,       -- data i hora previstes del passi
  pista_id         INTEGER,             -- null = cap falca vàlida aquell dia
  PRIMARY KEY (campanya_id, esdeveniment_id, programat_per)
);

CREATE TABLE campanya_falca (
  campanya_id INTEGER NOT NULL REFERENCES campanya(id) ON DELETE CASCADE,
  pista_id     INTEGER NOT NULL,
  data_inici  TEXT,            -- null = les de la campanya
  data_fi     TEXT,
  dies_mask   INTEGER NOT NULL DEFAULT 127,
  pes         INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (campanya_id, pista_id)
);
```

- **Fora de dates:** si en un passi no hi ha cap falca vàlida, l'acció no fa res i queda anotat al registre. Quan una campanya s'acaba, l'aplicació ho avisa i ofereix treure-la dels esdeveniments.

- **Dates de la pista (com ara):** cada pista conserva les seves dates d'inici i de fi, perquè també es fan servir fora de les falques. Al backup del gener del 2026, 42 pistes tenen dates, sobretot programes i promocions de temporada (per exemple, del 15/09/2025 al 30/06/2026). Una pista programada directament (`PISTA`) funciona com ara, només amb les seves dates. Una falca dins d'una campanya ha de complir les dates de la pista **i** les de la campanya (i les seves pròpies, si en té).
- **Migració:** les dates d'inici i de fi de RadioDJ es copien a la pista. Les falques es continuen programant amb `PISTA`, com ara; agrupar-les en campanyes es fa a mà quan convingui, amb un assistent que proposa ajuntar les falques del mateix anunciant.
- **Informe de programació:** es calcula a partir dels esdeveniments i les dates de la campanya. Inclou el nombre de passis previstos i la llista de dates i hores, amb la falca prevista per a cada passi.
- **Certificat d'emissió:** per pista o per campanya (indicant quina falca ha sonat a cada passi). Es fa a partir de l'**historial real del motor**, que identifica cada pista pel seu identificador i registra l'hora exacta en què ha sonat (i si s'ha reproduït sencera). El nombre de passis i la llista surten de les mateixes dates, i per tant sempre coincideixen.
- **Previst i emès:** el certificat pot incloure una comparació amb l'informe de programació: passis previstos, passis emesos i la llista dels que falten amb el motiu (`NO_EMES` per un tall, fitxer que faltava, esdeveniment omès o en conflicte…), presos del registre d'execució.
- **PDF generat per l'aplicació**, amb logotip, nom de l'emissora i freqüència presos de la configuració. En lloc de capturar la pantalla amb `html2canvas`, s'imprimeix l'HTML amb `webContents.printToPDF` d'Electron: el text es pot seleccionar i el fitxer pesa menys.
- **Historial:** no hi ha el límit de 30 dies de RadioDJ. L'historial de **totes** les pistes es conserva el temps que es configuri, amb un mínim de 2 anys (D-011, D-028). Ocupa poc (unes 300 files al dia), i així es pot fer un certificat de qualsevol campanya, per llarga o antiga que sigui, sense haver-lo de fer cada mes.

## 11. Pistes de durada variable (programes, promos i falques)

**Com es fa ara:** els programes, les promos i les falques s'entren a RadioDJ com a "pistes de durada variable". La pista és una referència fixa a un fitxer, i n'hi ha prou amb substituir el fitxer per actualitzar-ne el contingut. Els programes els actualitza automàticament l'**Automatitzador**; les promos i les falques, a mà.

**A Ona Bages 24h**
- **Pista = ruta fixa:** el contingut pot canviar sense tocar la programació, els esdeveniments ni les campanyes.
- **Detecció de canvis:** abans de preparar una pista per sonar (uns 30 s abans), el motor comprova la mida i la data de modificació del fitxer. Si han canviat, en torna a llegir la durada i els punts d'inici, mescla i final. Una vigilància de la carpeta (`FileSystemWatcher`) ho pot fer també en segon pla, perquè la cua mostri les hores previstes correctes.
- **Substitució abans de l'emissió (D-018):** l'Automatitzador substitueix el fitxer abans de l'hora d'emissió, quan el motor encara no el té obert, i per tant no hi ha bloqueig. No cal cap fitxer temporal. Les pistes curtes (promos i falques, menys de 3 minuts) es carreguen senceres a memòria en preparar-les, de manera que el fitxer queda lliure de seguida. A Ona Bages Play es va comprovar que un MP3 que sona es pot esborrar o reanomenar, però que no s'hi pot escriure a sobre (`docs/notes-ona-bages-24h.md`, secció 2, d'aquell repositori).
- **Fitxer a mig escriure:** si el fitxer canvia en els últims segons abans de sonar, o no es pot llegir, el motor fa servir l'última versió bona que tenia preparada i deixa un avís.
- **Avís de fitxer desactualitzat (opcional):** per a cada pista es pot indicar cada quant s'ha d'actualitzar (per exemple, un programa diari). Si el fitxer no ha canviat en aquest temps, l'aplicació ho avisa abans que soni.

## 12. Recurrències que no són setmanals

**Limitació de RadioDJ:** els esdeveniments només es poden repetir per dies de la setmana (o un sol dia concret). Un programa que s'emet **els divendres cada 15 dies**, el **primer dissabte de mes** o en unes **dates soltes** no es pot programar directament.

Avui l'**Automatitzador** ho resol per la seva banda: té patrons de dates (setmanal, cada N setmanes, ordinal mensual, dies del mes, dates soltes i un generador de dates). A més, té una "finestra de disponibilitat": publica el programa 5 minuts abans de l'emissió i hi torna a posar un fitxer alternatiu 12 hores després. A RadioDJ l'esdeveniment és setmanal i sona cada divendres; l'Automatitzador fa que, les setmanes que no toca, el fitxer sigui l'alternatiu. Funciona, però la planificació és en dos llocs diferents, i l'Automatitzador ja adverteix que "el programa d'emissió no comparteix la seva planificació".

**A Ona Bages 24h,** el mateix esdeveniment sap quins dies li toca:

| `recurrencia` | Exemple | Camps |
|---|---|---|
| `SETMANAL` | Dilluns a divendres a les 15:00 | `dies_mask` |
| `CADA_N_SETMANES` | **Divendres cada 15 dies**, a partir del 6/11/2026 | `dies_mask`, `interval_setm = 2`, `data_ancora` |
| `MENSUAL_ORDINAL` | Primer i tercer dissabte de mes; últim diumenge de mes | `dies_mask`, `ordinals` |
| `DIES_MES` | Dia 1 i dia 15 de cada mes; últim dia de mes | `dies_mes` |
| `DATES` | 24/12, 31/12 i 5/1 | files `INCLOU` a `esdeveniment_data` |

- **Cada 15 dies no depèn de la setmana del mes:** es compta des de la `data_ancora` (un divendres en què segur que toca). No falla els mesos amb cinc divendres ni en canviar d'any.
- **Excepcions** per a qualsevol tipus: dates en què no s'emet encara que toqui (files `EXCLOU`). Per exemple, un festiu o una setmana de vacances del programa.
- **Rang de dates** (`data_inici`, `data_fi`) per a temporades, com ara.
- **Previsualització:** a l'editor es mostren les 10 pròximes dates en què sonarà l'esdeveniment, abans de desar.
- **Graella per setmanes reals:** la vista setmanal mostra una setmana concreta del calendari (amb fletxes per anar a la setmana següent o anterior). Un esdeveniment de cada 15 dies surt en una setmana sí i en l'altra no. A més, hi ha una vista de "plantilla" que mostra tots els esdeveniments setmanals sense data, per editar la graella habitual com ara.
- **Càlcul de dates:** es fa amb dates de calendari, no sumant hores, perquè els canvis d'horari d'estiu i d'hivern no facin saltar un dia. L'Automatitzador ja ho fa així (`schedule-generator.mjs`); al motor en C# es farà amb `DateOnly`.
- **Migració:** tots els esdeveniments de RadioDJ passen com a `SETMANAL`.
- **Detalls del càlcul** (D-059): «cada N setmanes» compta setmanes senceres des de la de l'àncora (pot sonar diversos dies d'aquelles setmanes); un dia del mes que no existeix no sona (per a l'últim dia, «last»); les dates afegides sonen amb qualsevol recurrència i les excepcions manen sobre tot; «cada N minuts» va fins a mitjanit. Canvis d'hora: una hora que no existeix no sona (`OMES_CANVI_HORA`) i una hora repetida sona només la primera vegada (D-057).

**Coordinació amb l'Automatitzador**
- Quan l'esdeveniment ja només sona els dies que toca, la finestra de disponibilitat amb fitxer alternatiu deixa de ser imprescindible. L'Automatitzador només ha de tenir el fitxer a punt abans de l'emissió.
- **Més endavant (opcional):** Ona Bages 24h pot donar a l'Automatitzador les pròximes emissions de cada fitxer (una ordre de l'API com `scheduler.occurrences {path, from, to}`). Així l'Automatitzador pot avisar si un programa no estarà a punt a temps, i la planificació d'emissió deixa d'estar duplicada. Els patrons de recurrència són els mateixos a les dues aplicacions, i el format s'hauria de mantenir compatible.
