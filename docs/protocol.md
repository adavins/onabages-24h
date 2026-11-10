# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 2, pas 4 (motor, dispositius, sortida, cua, biblioteca, AutoDJ, historial, títol per a BUTT, mesuradors i planificador). Basat en el protocol d'Ona Bages Play (commit `2f1608e`), amb el mateix format.

Qualsevol canvi en aquest protocol s'ha d'anotar aquí **en el mateix commit** que el canvi de codi. Les ordres i els esdeveniments previstos per a les fases següents són a `docs/planificador.md` (secció 5).

## 1. Transport

- WebSocket a `ws://127.0.0.1:<port>/ws?token=<testimoni>`.
- El motor escolta **només** a `127.0.0.1`. Port per defecte: **`47824`** (Ona Bages Play fa servir el `47800`; tots dos poden funcionar alhora).
- El testimoni el genera qui arrenca el motor (`--token`) o, si no se n'hi passa cap, el mateix motor. Un testimoni incorrecte es rebutja amb HTTP `401`.
- El motor publica la informació de connexió al **fitxer d'execució** `<dades>\engine.json`, i l'esborra en aturar-se:

```json
{ "schemaVersion": 1, "pid": 1234, "port": 47824, "token": "…", "startedAt": "2026-11-09T15:51:20.0000000+00:00", "version": "0.1.0" }
```

- `<dades>` és la carpeta `dades\` de l'aplicació portable (D-044; la interfície la passa amb `--data-dir`). Si el motor arrenca sol, la busca pujant des de la seva carpeta (ha de contenir `LLEGEIX-ME.txt`) i, si no la troba, fa servir la de reserva `%ProgramData%\OnaBages24`, amb un avís al registre. Hi ha també la base de dades (`onabages24.db`), les còpies de seguretat (`copies\`) i els registres (`logs\`).
- Comprovació de vida sense testimoni: `GET http://127.0.0.1:<port>/health` → `200 ok` **només si el mesclador ha enviat àudio a la sortida en els últims 2 s** (D-045, D-048). Si no (motor penjat, sense cap sortida), `503` amb el motiu en text. El vigilant extern el fa servir per reiniciar el motor.
- Es poden connectar diversos clients alhora (la interfície, `tools/prova-motor.mjs`…). Els esdeveniments s'envien a tots.
- Mida màxima d'un missatge: 64 KB.

Arrencada del motor:

```text
OnaBages24.Engine [--port 47824] [--token TESTIMONI] [--data-dir CARPETA]
```

## 2. Format dels missatges

Tots els missatges són objectes JSON amb el camp `"v": 1`.

### Ordre (client → motor)

```json
{ "v": 1, "id": 7, "cmd": "engine.hello" }
```

- `id`: valor que tria el client (número o text); la resposta porta el mateix `id`.
- `cmd`: nom de l'ordre, en format `àmbit.acció`.
- Altres camps: paràmetres de l'ordre.

### Resposta (motor → client)

```json
{ "v": 1, "id": 7, "ok": true, "result": { … } }
{ "v": 1, "id": 7, "ok": false, "error": { "code": "unknown_command", "message": "…" } }
```

El `message` és en català i es pot mostrar a l'operador.

### Esdeveniment (motor → clients)

```json
{ "v": 1, "ev": "engine.status", "version": "0.1.0", "uptime": 12.0, "clients": 1 }
```

## 3. Codis d'error

| Codi | Significat |
|---|---|
| `bad_json` | El missatge no és JSON vàlid, no és un objecte o té camps repetits (p. ex. dos `id`) |
| `protocol_version` | El camp `v` falta o no és compatible |
| `missing_cmd` | Falta el camp `cmd` |
| `bad_params` | Falta un paràmetre o té un valor no vàlid |
| `database_error` | La base de dades no ha pogut fer l'operació. El missatge n'explica el motiu |
| `unknown_command` | L'ordre no existeix |
| `internal` | Error inesperat del motor (el motor continua funcionant) |

## 4. Ordres

| Ordre | Paràmetres | Resultat | Des de |
|---|---|---|---|
| `engine.hello` | — | `{ name, version, protocol }` | fase 1, pas 6 |
| `engine.ping` | — | `{ pong: true, time }` (`time` en UTC, ISO 8601) | fase 1, pas 6 |
| `engine.shutdown` | — | `{ stopping: true }`. El motor s'atura uns 300 ms després de respondre (en menys de 5 s) | fase 1, pas 6 |
| `state.get` | — | `{ engine: { version, uptime, clients }, library: { tracks } }` | fase 1, pas 6 |
| `devices.list` | `kind`: `"output"`, `"input"` o `"all"` (per defecte); `includeInactive`: `true` per incloure també els desactivats i desconnectats (per defecte, `false`) | `{ devices: [ … ] }` (vegeu 4.1) | fase 1, pas 6 |
| `output.get` | — | `{ output }` (vegeu 4.3) | fase 1, pas 7c |
| `output.set` | `deviceId` | `{ output }`. Desa la sortida a la configuració (id i nom) i hi passa de seguida. Si no existeix, `bad_params` | fase 1, pas 7c |
| `queue.get` | — | `{ queue }` (vegeu 4.2) | fase 1, pas 7c |
| `queue.insert` | `items`: llista de `{ "trackId": 12 }` (de la biblioteca) o `{ "path": "D:\\…" }` (un fitxer qualsevol), **o bé** `folder` (carpeta de la biblioteca: pistes barrejades, sense les que falten) i `limit` (per defecte 10); `position`: `"end"` (per defecte), `"next"` o un índex (0 = la primera que sonarà). Origen: `manual` | `{ queue }` | fase 1, pas 7c |
| `queue.remove` | `itemId` | `{ queue }`. Només les que encara no han començat; si no, `bad_params` | fase 1, pas 7c |
| `player.next` | `fadeMs` (de 0 a 10.000; per defecte 1.000) | `{ queue }`. La que sona baixa amb un fos i la següent comença de seguida | fase 1, pas 7c |
| `library.list` | `folder`, `limit` (per defecte 100, com a molt 1.000) | `{ tracks: [ … ] }` (vegeu 4.4): pistes de la carpeta i subcarpetes. De moment, per a les proves | fase 1, pas 7c |
| `autodj.get` | — | `{ autodj }` (vegeu 4.5) | fase 1, pas 8 |
| `autodj.set` | `enabled` (`true`/`false`), `rotationId`. Tots dos opcionals: el que no s'envia es queda com està | `{ autodj }`. La rotació es desa i es manté en tornar a arrencar; `enabled` no: el motor sempre arrenca amb l'AutoDJ actiu (D-053). Si la rotació no existeix, `bad_params` | fase 1, pas 8 |
| `rotations.list` | — | `{ rotations: [ … ] }` (vegeu 4.5) | fase 1, pas 8 |
| `history.list` | `from`, `to`: una hora ISO 8601 amb zona (`"2026-11-10T12:00:00Z"`) o un dia (`"2026-11-10"`, en hora local; a `to`, el dia hi és inclòs). `search`: text de l'artista o del títol. `limit`: per defecte 100, com a molt 2.000. Tots opcionals | `{ entries: [ … ], more }` (vegeu 4.6): les més recents primer. `more`: n'hi ha més que les retornades | fase 1, pas 9 |
| `nowplaying.get` | — | `{ nowPlaying }` (vegeu 4.6) | fase 1, pas 9 |
| `nowplaying.set` | `enabled`, `path` (ruta completa del fitxer; `null` o `""` = el per defecte), `template` (ha de tenir `$artist$` o `$title$`), `types` (llista de tipus de pista, 4.2). Tots opcionals: el que no s'envia es queda com està | `{ nowPlaying }`. Es desa a la configuració i es torna a escriure el títol de seguida | fase 1, pas 9 |
| `scheduler.list` | — | `{ events: [ … ] }` (vegeu 4.7): tots, també els desactivats i les plantilles | fase 2, pas 3 |
| `scheduler.get` | `eventId` | `{ event }` | fase 2, pas 3 |
| `scheduler.save` | `event` (4.7). Sense `id` (o 0), en crea un de nou; amb `id`, el substitueix sencer (els camps que no s'envien prenen el valor per defecte) | `{ event }` tal com s'ha desat. El planificador el té en compte de seguida. Si no és coherent (p. ex. «cada N setmanes» sense dia de referència), `bad_params` amb el motiu en català | fase 2, pas 3 |
| `scheduler.delete` | `eventId` | `{ deleted: <id> }`. El registre d'execució es conserva | fase 2, pas 3 |
| `scheduler.occurrences` | `from`, `to` (com a `history.list`; per defecte, d'ara a 24 h; com a molt 400 dies), `eventId` (només aquest, encara que estigui desactivat), `limit` (per defecte 200, com a molt 2.000) | `{ occurrences: [ … ], more }` (4.7), per ordre. Sense `eventId`, només els actius que no són plantilles | fase 2, pas 3 |
| `scheduler.log` | `from`, `to` (per defecte, avui), `eventId`, `limit` (per defecte 500) | `{ entries: [ … ], more }` (4.7): el registre d'execució, per hora prevista | fase 2, pas 3 |

**Atenció:** el camp `id` del missatge és el número de l'ordre (2): per dir de quin esdeveniment es tracta, `eventId`.

### 4.1. Dispositius

```json
{ "id": "{0.0.0.00000000}.{f2d6376b-71c4-4ffc-a362-a446ddd885a8}",
  "name": "Altavoces - Sound Blaster Z (Sound Blaster Z)",
  "kind": "output",
  "status": "active",
  "format": { "sampleRate": 48000, "bits": 24, "channels": 2 } }
```

- `id`: identificador estable de Windows. És el que es desa a la configuració.
- `name`: nom complet tal com el mostra Windows. Canvia si l'usuari reanomena el dispositiu; l'`id`, no.
- `kind`: `output` (sortida) o `input` (entrada).
- `status`: `active`, `disabled` (desactivat a Windows) o `unplugged` (la targeta hi és però no hi ha res connectat).
- `format`: el format triat a Windows; `null` si no se sap.
- `connector`: tipus de connector segons Windows: `analog`, `digital`, `hdmi`, `remote` o `unknown`. Serveix per triar la sortida d'emergència (D-038).

### 4.2. Cua

```json
{ "current": { "id": "6f0c…", "trackId": 23977, "path": "O:\\musica\\…\\The Kooks - Bad Habit.mp3",
               "artist": "The Kooks", "title": "Bad Habit", "type": "music", "origin": "manual", "eventName": null,
               "durationMs": 193500 },
  "upcoming": [ { … }, { … } ] }
```

- `current`: la pista que sona (la que ha començat més tard, si dues se solapen), o `null`.
- `upcoming`: les que sonaran, en ordre (la primera pot estar preparant-se).
- `id`: identificador de l'element a la cua (no és el de la pista); `trackId`: el de la pista a la biblioteca (`null` si s'ha afegit per ruta i no hi és).
- `type`: `music`, `sweeper`, `stationId`, `promo`, `commercial`, `program`, `news`, `timeAnnouncement`, `timeSignal`, `stream` o `other`.
- `origin` (D-034): `autodj`, `manual` o `event`.
- `durationMs`: quant sonarà, del punt d'inici al final (sense restar el solapament amb la següent), o `null` si no se sap (p. ex. un fitxer que no és a la biblioteca). Des del pas 10c.

### 4.3. Sortida (D-038)

```json
{ "configured": { "id": "{0.0.0.00000000}.{…}", "name": "Altavoces - Realtek (…)" },
  "current": { …dispositiu (4.1)… },
  "emergency": false,
  "problem": null }
```

- `configured`: la desada amb `output.set` (`id` i `name` `null` si no n'hi ha cap).
- `current`: per on s'emet ara (`null` si no hi ha cap sortida).
- `emergency`: s'emet per una sortida d'emergència (no és la configurada). Llavors `problem` ho explica i la interfície ho ha de mostrar fins que es resolgui (D-039).
- `problem`: text en català si hi ha algun problema (sortida d'emergència, cap sortida disponible…), o `null`.

### 4.4. Pistes

```json
{ "id": 23977, "path": "O:\\musica\\…", "artist": "…", "title": "…", "type": "music",
  "durationMs": 215400, "cueSource": "analyzer", "fileState": "ok" }
```

- `cueSource`: d'on surten els punts d'encadenament: `radiodj`, `analyzer`, `manual` o `null` (encara no n'hi ha).
- `fileState`: `ok`, `missing` (el fitxer falta) o `retired` (ha sortit d'una bossa renovable).

### 4.5. AutoDJ i rotacions (D-049)

```json
{ "enabled": true, "rotation": { "id": 1, "name": "OnaBages" }, "nextStep": 3, "problem": null }
```

- `enabled`: l'AutoDJ afegeix pistes quan a la cua n'hi ha menys de 2. En arrencar, el motor l'activa sol (amb la configuració desada o, la primera vegada, amb la primera rotació).
- `rotation`: la rotació activa, o `null` si no n'hi ha cap (llavors sona música de tota la biblioteca, amb avís).
- `nextStep`: índex del pas que es farà servir la pròxima vegada (0 = el primer).
- `problem`: avís en català (un pas sense pistes que s'ha saltat, la rotació que no troba res…) o `null`.

Rotació:

```json
{ "id": 1, "name": "OnaBages",
  "steps": [ { "category": "Sweepers", "subcategory": "Ona Bages", "tag": "SW - catala",
               "selection": "leastRecent", "repeatRule": false, "sweeper": false } ] }
```

- `subcategory`: `null` si el pas tria de tota la categoria. `tag`: `null` si no filtra per etiqueta.
- `selection`: `leastRecent` (la que fa més temps que no sona), `leastPlayed` (la que ha sonat menys vegades) o `random`.
- `repeatRule`: s'apliquen els criteris de repetició (no repetir pista ni artista de la cua).
- `sweeper`: trepitjador (sweeper) a l'inici de la pista (importat de RadioDJ; encara no es fa servir).

### 4.6. Historial i títol per a BUTT (D-051)

Entrada de l'historial:

```json
{ "id": 812, "trackId": 23977, "startedAt": "2026-11-10T11:51:44.0000000+00:00", "playedMs": 214350, "complete": true,
  "origin": "autodj", "artist": "Macy Gray", "title": "Still", "path": "O:\musica\…" }
```

- `startedAt`: hora real en què ha començat a sonar, en UTC.
- `playedMs` i `complete`: quant ha sonat i si ha arribat al final previst; `null` mentre encara sona.
- `trackId`: `null` si era un fitxer que no és a la biblioteca o si la pista s'ha esborrat. L'artista, el títol i la ruta són els del moment en què va sonar.

Exportació del títol:

```json
{ "enabled": true, "path": "D:\apps\onabages-24h\dades\NowPlaying.txt", "customPath": false,
  "template": "$artist$ - $title$", "types": ["music", "program"], "text": "Macy Gray - Still", "problem": null }
```

- `path`: el fitxer on s'escriu; `customPath` és `false` si és el per defecte (`<dades>\NowPlaying.txt`).
- `template`: `$artist$` i `$title$` es substitueixen (sense distingir majúscules). Si falta l'artista o el títol, es treu el separador que quedaria sol.
- `types`: tipus de pista que canvien el títol. Amb els altres, el fitxer es queda com estava.
- `text`: l'últim títol exportat; `null` si encara no n'hi ha cap.
- `problem`: per què no s'ha pogut escriure el fitxer (es torna a provar cada 5 s), o `null`.
- El fitxer és una sola línia en UTF-8 sense BOM, sense salt de línia final.

### 4.7. Planificador (docs/planificador.md; D-057, D-058, D-059)

```json
{ "id": 12, "name": "El Magazín", "mode": "TALLA", "startTime": "10:00:00",
  "recurrence": "CADA_N_SETMANES", "daysMask": 16, "intervalWeeks": 2, "anchorDate": "2026-11-06",
  "ordinals": [], "monthDays": [], "startDate": null, "endDate": null,
  "repeat": "CAP", "intervalMinutes": null, "hoursMask": null,
  "priority": 50, "toleranceSeconds": 60, "fadeMs": 2000, "marginSeconds": null, "duckingDb": null,
  "maxDurationSeconds": 3600, "color": null, "categoryId": null,
  "clearQueue": true, "commercialBlock": false, "isTemplate": false, "enabled": true, "notes": null,
  "modified": "2026-11-10T18:00:00.000+00:00",
  "actions": [ { "type": "PISTA", "params": { "songId": 10 }, "daysMask": null },
               { "type": "ROTACIO", "params": { "rotacioId": 1 }, "daysMask": 16 } ],
  "includedDates": [], "excludedDates": ["2027-01-01"] }
```

- Els valors de `mode` (`TALLA`, `ENCUA`, `SUPERPOSA`, `SISTEMA`), `recurrence` (`SETMANAL`, `CADA_N_SETMANES`, `MENSUAL_ORDINAL`, `DIES_MES`, `DATES`), `repeat` (`CAP`, `CADA_HORA`, `CADA_N_MIN`) i `type` de les accions són els de `planificador.md` (seccions 2–4 i 12).
- `startTime`: hora local (`HH:MM:SS`; en desar també s'admet `HH:MM`). Dates: `AAAA-MM-DD`.
- `daysMask`: bit 0 = dilluns (1) … bit 6 = diumenge (64); `127` = tots. `hoursMask`: bit 0 = 00 h … bit 23 = 23 h; `null` = totes.
- `ordinals`: 1 = primer … 5 = cinquè, `-1` = l'últim. `monthDays`: 1–31, `-1` = l'últim dia del mes.
- `params` de les accions disponibles ara: `PISTA` `{ songId }` (id de la pista a la biblioteca, el `trackId` de la cua), `ROTACIO` `{ rotacioId }`, `AUTODJ` `{ actiu }`. Les altres es poden desar, però encara no fan res (el registre ho diu).
- Modes disponibles ara: `ENCUA` (el contingut va al davant de la cua; amb `clearQueue`, abans s'esborra la cua), `TALLA` i `SISTEMA`. Un `SUPERPOSA` s'anota com a `ERROR` fins al pas 5.
- `TALLA` (pas 4, D-061): es programa uns 2 s abans, a la mostra exacta de l'hora. La pista que sona comença el fos (`fadeMs`; si és `null`, 1.000 ms) en aquell instant i la primera pista de l'esdeveniment comença alhora; la resta va darrere. Amb `clearQueue`, abans s'esborra la cua.

Ocurrència (`scheduler.occurrences`):

```json
{ "eventId": 12, "name": "El Magazín", "mode": "TALLA", "date": "2026-11-20", "time": "10:00:00",
  "at": "2026-11-20T10:00:00.0000000+01:00", "skippedClockChange": false }
```

- `date` i `time`: dia i hora locals programats. `at`: l'instant. `skippedClockChange`: aquella hora no existeix pel canvi d'hora i no sonarà (D-057); `at` és llavors el moment del salt.

Línia del registre (`scheduler.log` i `scheduler.fired`):

```json
{ "id": 33, "eventId": 12, "name": "Mitja hora", "scheduledFor": "2026-11-20T09:30:00.0000000+00:00",
  "executedAt": "2026-11-20T09:30:00.0120000+00:00", "result": "OK", "detail": "2 pistes a la cua" }
```

- `result`: `OK`, `OMES_TARD` (s'hi ha arribat més tard que la tolerància, p. ex. amb el motor aturat), `OMES_CONFLICTE` (coincidia amb un TALLA o ENCUA de més prioritat), `OMES_CANVI_HORA`, `NO_EMES` o `ERROR`. `executedAt` és `null` si no s'ha executat. `name` és el nom en aquell moment.
- `NO_EMES`: el contingut d'una emissió (normalment un ENCUA) s'ha esborrat de la cua abans de sonar, perquè un esdeveniment posterior amb `clearQueue` l'ha tret. És una línia nova amb el `scheduledFor` d'aquella emissió; la línia `OK` d'aquella emissió es manté. No compta per als certificats.
- Un `TALLA` s'anota en programar-lo, amb `executedAt: null`. Quan la primera pista comença, la mateixa línia (mateix `id`) es completa: `executedAt` és l'hora en què sona i `detail` acaba amb «ha començat a la mostra prevista» o «ha començat amb +N ms de diferència». Per això `scheduler.fired` arriba dues vegades amb el mateix `id`.

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `version`, `uptime` (s), `clients` | cada segon | fase 1, pas 6 |
| `queue.changed` | `queue` (4.2) | quan s'afegeix, es treu, comença o s'acaba una pista | fase 1, pas 7c |
| `player.trackStarted` | `item` (element de la cua), `time` (UTC) | quan una pista comença a sonar | fase 1, pas 7c |
| `player.trackEnded` | `item`, `playedMs`, `complete` (ha arribat al final previst) | quan una pista s'acaba | fase 1, pas 7c |
| `player.trackFailed` | `item`, `message` | quan una pista no es pot fer sonar (fitxer que falta o il·legible) i se salta | fase 1, pas 7c |
| `output.changed` | `output` (4.3) | quan canvia la sortida, l'estat d'emergència o el problema | fase 1, pas 7c |
| `autodj.changed` | `autodj` (4.5) | quan s'activa o es desactiva, canvia la rotació o canvia l'avís (les pistes que afegeix surten a `queue.changed`) | fase 1, pas 8 |
| `meters` | `output` (nivells de la sortida), `position` (la pista que sona); vegeu 5.1 | unes 20 vegades per segon, només si hi ha algun client connectat | fase 1, pas 10a |
| `scheduler.fired` | `entry` (línia del registre, 4.7) | quan el planificador executa o omet una emissió, i quan comença la primera pista d'un TALLA (la mateixa línia, completada) | fase 2, passos 3 i 4 |
| `scheduler.changed` | — | quan es desa o s'esborra un esdeveniment (la interfície torna a demanar el que mostra) | fase 2, pas 3 |

### 5.1. Mesuradors i posició

```json
{ "v": 1, "ev": "meters", "output": [-12.3, -11.8],
  "position": { "itemId": "6f0c…", "elapsedMs": 61500, "remainingMs": 120000, "mixInMs": 110000, "durationMs": 181500 } }
```

- `output`: `[esquerre, dret]` en **dBFS**, el pic més alt de la sortida des de l'esdeveniment anterior (uns 50 ms). `-90` vol dir silenci. És la mescla del motor, abans d'adaptar-la a la targeta. La interfície fa la caiguda suau, la retenció del pic i l'indicador de saturació.
- `position`: la pista que sona (`itemId` és el de `queue.current`), o `null` si no sona res.
  - `elapsedMs`: quant ha sonat des del punt d'inici.
  - `remainingMs`: quant falta per al final (amb el fos de sortida inclòs).
  - `mixInMs`: quant falta perquè entri la següent (el punt de mescla); `0` si ja ha entrat.
  - `durationMs`: de l'inici al final (els punts de la pista, no la durada del fitxer).
  - Es calcula amb el rellotge del mesclador, el mateix que fa servir l'encadenament. Si es fa saltar la pista, `remainingMs` passa a ser la durada del fos.

## 6. Eines

- `tools/prova-motor.mjs`: envia ordres al motor des de la línia d'ordres i en mostra les respostes (Node 22 o posterior, sense paquets). Llegeix el port i el testimoni de `engine.json`.

```text
node tools/prova-motor.mjs [--dades CARPETA] [--carpeta CARPETA_DE_MÚSICA [--quantes N]] [--escolta SEGONS [--mesuradors]]
                           [--encua SEGONS ID[,ID…]] [--talla SEGONS ID[,ID…] [--fos MS]] [--rotacio SEGONS ROTACIO_ID] [ordre …]
```

- `--escolta` mostra els esdeveniments que arriben (sense `engine.status` i, si no es posa `--mesuradors`, sense `meters`).
- `--encua`, `--talla` i `--rotacio` creen un esdeveniment de prova per a avui d'aquí a uns segons, que sona una sola vegada (recurrència `DATES`). El de `--talla` esborra la cua; `--fos` en canvia el fos (per defecte, 1.000 ms).
