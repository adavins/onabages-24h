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
| **TALLA** | Substitueix la cua pel contingut de l'esdeveniment i comença a sonar de seguida. La pista actual es talla amb el fade configurat. | `Clear Playlist!` + `Load…` + `Play!` |
| **ENCUA** | Insereix el contingut com a següent a sonar. La pista actual continua. | `Load Track By ID…|Top` sense *Clear* |
| **SUPERPOSA** | Reprodueix pel reproductor auxiliar per sobre del que sona, amb atenuació (*ducking*) opcional. | `PluginAction|Auxiliary Players…` |
| **SISTEMA** | Només fa accions sense àudio: canviar la rotació o els sweepers, executar una aplicació, etc. | `Load Rotation`, `Run Application`… |

Opcions per mode:
- **TALLA:** `fade_ms`, i `marge_seg` (si a la pista actual li queden menys de N segons, s'espera que acabi).
- **SUPERPOSA:** `ducking_db` i el reproductor auxiliar que es fa servir.

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
  durada_visual   INTEGER,                  -- segons; només per dibuixar el bloc a la graella
  color           TEXT,
  categoria_id    INTEGER REFERENCES categoria_esdeveniment(id),
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
  params           TEXT NOT NULL DEFAULT '{}' -- JSON
);

-- Dates soltes (recurrencia = 'DATES') i excepcions (dates en què no s'emet)
CREATE TABLE esdeveniment_data (
  esdeveniment_id  INTEGER NOT NULL REFERENCES esdeveniment(id) ON DELETE CASCADE,
  data             TEXT NOT NULL,           -- 'YYYY-MM-DD'
  tipus            TEXT NOT NULL CHECK (tipus IN ('INCLOU','EXCLOU')),
  PRIMARY KEY (esdeveniment_id, data)
);

CREATE TABLE categoria_esdeveniment (id INTEGER PRIMARY KEY, nom TEXT NOT NULL, color TEXT);

-- Registre d'execució (auditoria i informe)
CREATE TABLE registre_execucio (
  id               INTEGER PRIMARY KEY,
  esdeveniment_id  INTEGER,
  programat_per    TEXT NOT NULL,           -- data i hora previstes
  executat_a       TEXT,
  resultat         TEXT NOT NULL,           -- OK | OMES_TARD | OMES_CONFLICTE | ERROR
  detall           TEXT
);
```

**Conflictes:** quan dos esdeveniments TALLA o ENCUA coincideixen al mateix instant, s'executa el de prioritat més alta i l'altre queda al registre com a `OMES_CONFLICTE`. Els esdeveniments SUPERPOSA poden coexistir amb qualsevol altre.

**Graella:** cada targeta es dibuixa des de `hora_inici` fins a `durada_visual`. Si no hi ha `durada_visual`, es dibuixa com una marca puntual. Els esdeveniments recurrents (per exemple, cada hora) es poden ocultar o mostrar en una fila pròpia.

## 4. Tipus d'acció

| `tipus` | `params` | Origen a RadioDJ |
|---|---|---|
| `PISTA` | `{songId}` | `Load Track By ID` |
| `STREAM` | `{url, durada_seg, fade_ms, reserva_songId?}` | `Load Track By ID` d'una URL + `Seconds To Wait`. Connexió anticipada, tall automàtic i pista de reserva si falla (vegeu el document de context operatiu, 2.2) |
| `CAMPANYA` | `{campanyaId}` | Nou: en el moment d'emetre, el motor tria quina falca de la campanya sona (secció 10) |
| `PLANTILLA` | `{plantillaId}` | Esdeveniment arxivat (s'insereixen les seves accions) |
| `AUXILIAR` | `{songId, player, duckingDb?}` | `PluginAction|Auxiliary Players|Play File From Database` |
| `ROTACIO` | `{rotacioId}` | `Load Rotation` |
| `SWEEPERS` | `{subcategoriaId}` | `Set Sweeper Category` |
| `AUTODJ` | `{actiu}` | `Assisted Enable!` / `Assisted Disable!` (només quan es vol de veritat, no com a pegat) |
| `ESPERA` | `{segons}` | `Seconds To Wait` |
| `ATURA` | `{fadeMs?}` | `Stop!` |
| `ENTRADA` | `{silencia}` | Silenciar / treure el silenci de l'entrada |
| `EXECUTA` | `{ruta, args}` | `Run Application` |
| `NETEJA`, `REPRODUEIX` | `{}` | `Clear Playlist!`, `Play!`: avançades, per a casos especials. Normalment les implica el mode. |

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
2. Ometre els que tenen `data` buit (142 de 336 al backup del gener del 2026).
3. Deduir el mode: si hi ha `Clear Playlist!`, **TALLA**; si només hi ha `PluginAction|Auxiliary Players`, **SUPERPOSA**; si només hi ha `Load…|Top`, **ENCUA**.
4. Treure les parelles `Assisted Enable!` / `Assisted Disable!` que embolcallen el muntatge de la cua, i treure els `Clear`/`Play` implícits en el mode.
5. **Invertir l'ordre** dels `Load Track By ID…|Top` consecutius perquè quedin en ordre d'emissió.
6. Agrupar esdeveniments idèntics en dies diferents en un de sol amb `dies_mask`. Per exemple, les 15:00 de dilluns a divendres passen a ser 1 esdeveniment en lloc de 5, si el contingut coincideix.
7. Els esdeveniments `type = 2` (amb màscara de dies i hores) passen a `repeticio = CADA_HORA` amb `hores_mask`.

## 7. Puntualitat

**Requisit:** el primer element de l'esdeveniment ha de sonar exactament a l'hora programada. L'objectiu és un error inferior a 50 ms, que l'oïda no percep.

Com s'aconsegueix:
1. **Preparació anticipada:** uns 30 segons abans de l'hora, el motor obre i descodifica el primer element de l'esdeveniment i el deixa a punt en un reproductor de reserva. Així no hi ha retard per llegir el fitxer.
2. **Punt d'entrada:** es fa servir el *cue-in* de la pista (salt automàtic del silenci inicial), perquè el so comenci justament a l'hora i no uns mil·lisegons més tard. Per començar des d'un punt de la pista es fa servir el salt exacte d'`OnaBages.Audio` (`Mp3Seek`, D-155 d'Ona Bages Play), també amb els MP3 de bitrate variable.
3. **Rellotge d'alta resolució:** el disparament fa servir `Stopwatch` sobre l'hora del sistema, no un temporitzador de la interfície. Els últims mil·lisegons es compensen amb la latència coneguda de la sortida WASAPI.
4. **Tall a l'hora exacta:** en el mode TALLA, el fade de sortida de la pista actual comença `fade_ms` abans de l'hora, perquè el nou element entri a l'hora en punt i no després del fade. Per a esdeveniments d'hora exacta, `marge_seg` es deixa desactivat.
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

**Recomanació:** convertir `OnaBages.Audio` en una biblioteca compartida entre els dos projectes (submòdul de Git o paquet NuGet local). Així les correccions del motor beneficien les dues aplicacions.

## 10. Falques: calendari per pista, informe de programació i certificat d'emissió

**Com ho fa ara el Programador** (vista d'una pista, `views/song.ejs` i `routes/songs.js`):
- **Calendari de la pista:** sobre la graella setmanal es marquen les franges on ha de sonar una pista (normalment una falca). En desar, s'afegeix o es treu la pista de l'esdeveniment de RadioDJ de cada franja. Es poden desar calendaris i reutilitzar-los ("Calendaris desats": artista, títol, data d'inici, nombre d'hores).
- **Informe de programació d'emissions (PDF):** logotip, emissora, freqüència, data de l'informe, dates d'inici i de fi, durada, nom de la pista, nombre de passis i la graella amb les franges programades.
- **Certificat d'emissió (PDF):** logotip, emissora, freqüència, interval de dates, nombre de passis i la llista de cada emissió real (data i hora, artista o anunciant, títol), extreta de l'historial.

És una funció imprescindible: serveix per justificar davant dels anunciants que les falques han sonat.

**A Ona Bages 24h**
- **Vista "Calendari de la pista":** la mateixa graella setmanal que el planificador, amb les franges on la pista ja és dins d'un esdeveniment marcades. Marcar o desmarcar una franja afegeix o treu l'acció `PISTA` de l'esdeveniment corresponent. Si en aquella franja encara no hi ha cap esdeveniment, se'n crea un d'ENCUA.
- **Campanya amb diverses falques:** la campanya és l'element que es programa, no la falca. Es programa una sola vegada, amb la mateixa vista de calendari que una pista i l'acció `CAMPANYA` en lloc de `PISTA`, i el motor tria en cada passi quina de les seves falques ha de sonar.
  - Cada falca de la campanya pot tenir **dates pròpies** dins de les de la campanya, i **dies de la setmana** propis.
  - Entre les falques vàlides en aquell moment, el motor **alterna** (la que fa més temps que no ha sonat) o fa servir un **pes** (per exemple, A el doble que B).
  - Exemple: campanya "Ferreteria" de l'1 al 30 de novembre, de dilluns a divendres a les 8:30, 13:30 i 19:30. Les falques A i B s'alternen tot el mes, i la falca C ("Black Friday") només sona del 24 al 28. Es programa una sola vegada.
- Una campanya d'**una sola falca** és el cas més simple: funciona igual que programar la pista directament.

```sql
CREATE TABLE campanya (
  id          INTEGER PRIMARY KEY,
  nom         TEXT NOT NULL,
  anunciant   TEXT,
  data_inici  TEXT NOT NULL,   -- 'YYYY-MM-DD'
  data_fi     TEXT NOT NULL,
  seleccio    TEXT NOT NULL DEFAULT 'ALTERNA' CHECK (seleccio IN ('ALTERNA','PES')),
  notes       TEXT
);

CREATE TABLE campanya_falca (
  campanya_id INTEGER NOT NULL REFERENCES campanya(id) ON DELETE CASCADE,
  song_id     INTEGER NOT NULL,
  data_inici  TEXT,            -- null = les de la campanya
  data_fi     TEXT,
  dies_mask   INTEGER NOT NULL DEFAULT 127,
  pes         INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (campanya_id, song_id)
);
```

- **Fora de dates:** si en un passi no hi ha cap falca vàlida, l'acció no fa res i queda anotat al registre. Quan una campanya s'acaba, l'aplicació ho avisa i ofereix treure-la dels esdeveniments.

- **Dates de la pista (com ara):** cada pista conserva les seves dates d'inici i de fi, perquè també es fan servir fora de les falques. Al backup del gener del 2026, 42 pistes tenen dates, sobretot programes i promocions de temporada (per exemple, del 15/09/2025 al 30/06/2026). Una pista programada directament (`PISTA`) funciona com ara, només amb les seves dates. Una falca dins d'una campanya ha de complir les dates de la pista **i** les de la campanya (i les seves pròpies, si en té).
- **Migració:** les dates d'inici i de fi de RadioDJ es copien a la pista. Les falques es continuen programant amb `PISTA`, com ara; agrupar-les en campanyes es fa a mà quan convingui, amb un assistent que proposa ajuntar les falques del mateix anunciant.
- **Informe de programació:** es calcula a partir dels esdeveniments i les dates de la campanya. Inclou el nombre de passis previstos i la llista de dates i hores, amb la falca prevista per a cada passi.
- **Certificat d'emissió:** per pista o per campanya (indicant quina falca ha sonat a cada passi). Es fa a partir de l'**historial real del motor**, que registra l'hora exacta en què ha sonat cada pista (i si s'ha reproduït sencera). És més fiable que l'historial de RadioDJ.
- **PDF generat per l'aplicació**, amb logotip, nom de l'emissora i freqüència presos de la configuració. En lloc de capturar la pantalla amb `html2canvas`, s'imprimeix l'HTML amb `webContents.printToPDF` d'Electron: el text es pot seleccionar i el fitxer pesa menys.
- **Historial de les falques:** es conserva com a mínim 2 anys, o el temps que es configuri, independentment dels 30 dies que es guarden per a la resta de pistes. Així es pot emetre un certificat d'una campanya antiga.

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

**Coordinació amb l'Automatitzador**
- Quan l'esdeveniment ja només sona els dies que toca, la finestra de disponibilitat amb fitxer alternatiu deixa de ser imprescindible. L'Automatitzador només ha de tenir el fitxer a punt abans de l'emissió.
- **Més endavant (opcional):** Ona Bages 24h pot donar a l'Automatitzador les pròximes emissions de cada fitxer (una ordre de l'API com `scheduler.occurrences {path, from, to}`). Així l'Automatitzador pot avisar si un programa no estarà a punt a temps, i la planificació d'emissió deixa d'estar duplicada. Els patrons de recurrència són els mateixos a les dues aplicacions, i el format s'hauria de mantenir compatible.
