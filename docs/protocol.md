# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 1, pas 8 (motor, dispositius, sortida, cua, biblioteca i AutoDJ). Basat en el protocol d'Ona Bages Play (commit `2f1608e`), amb el mateix format.

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
| `autodj.set` | `enabled` (`true`/`false`), `rotationId`. Tots dos opcionals: el que no s'envia es queda com està | `{ autodj }`. Es desa a la configuració i es manté en tornar a arrencar. Si la rotació no existeix, `bad_params` | fase 1, pas 8 |
| `rotations.list` | — | `{ rotations: [ … ] }` (vegeu 4.5) | fase 1, pas 8 |

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
               "artist": "The Kooks", "title": "Bad Habit", "type": "music", "origin": "manual", "eventName": null },
  "upcoming": [ { … }, { … } ] }
```

- `current`: la pista que sona (la que ha començat més tard, si dues se solapen), o `null`.
- `upcoming`: les que sonaran, en ordre (la primera pot estar preparant-se).
- `id`: identificador de l'element a la cua (no és el de la pista); `trackId`: el de la pista a la biblioteca (`null` si s'ha afegit per ruta i no hi és).
- `type`: `music`, `sweeper`, `stationId`, `promo`, `commercial`, `program`, `news`, `timeAnnouncement`, `timeSignal`, `stream` o `other`.
- `origin` (D-034): `autodj`, `manual` o `event`.

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
- `selection`: `leastRecent` (la que fa més temps que no sona) o `random`.
- `repeatRule`: s'apliquen els criteris de repetició (no repetir pista ni artista de la cua).
- `sweeper`: sweeper a sobre de la pista (importat de RadioDJ; encara no es fa servir).

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

## 6. Eines

- `tools/prova-motor.mjs`: envia ordres al motor des de la línia d'ordres i en mostra les respostes (Node 22 o posterior, sense paquets). Llegeix el port i el testimoni de `engine.json`.

```text
node tools/prova-motor.mjs [--dades CARPETA] [--carpeta CARPETA_DE_MÚSICA [--quantes N]] [ordre …]
```
