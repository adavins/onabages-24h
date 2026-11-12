# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 3, pas 8d (motor, dispositius, sortida i processador de la sortida, cua, gestor de pistes, reproductors auxiliars, biblioteca, notes, preferències de la interfície, AutoDJ i trepitjador, historial, títol per a BUTT, mesuradors i planificador). Basat en el protocol d'Ona Bages Play (commit `2f1608e`), amb el mateix format.

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
| `queue.remove` | `itemId` | `{ queue }`. Només les que encara no han començat; si no, `bad_params`. Si venia d'un esdeveniment, queda al registre com a `NO_EMES` (fase 3, pas 2a) | fase 1, pas 7c |
| `queue.regenerate` | — | `{ removed, queue }`. «Regenera la cua» (D-034): treu les pistes que hi ha posat l'AutoDJ (no la que sona, ni les manuals, ni les dels esdeveniments) i l'AutoDJ les torna a triar amb la rotació actual, continuant pel pas de la primera pista que s'ha tret (D-097) | fase 3, pas 2b |
| `queue.move` | `itemId`, `delta` (−1 = amunt, 1 = avall) | `{ queue }`. La pista que s'estava preparant torna a la cua si cal. No es pot moure el principi de la cua quan la següent comença d'aquí a menys de 3 s, ni quan hi ha un tall programat d'aquí a uns segons: `bad_params` amb el motiu | fase 3, pas 2a |
| `player.playItem` | `itemId`, `fadeMs` (de 0 a 10.000; per defecte 1.000) | `{ skipped, queue }`. «Reprodueix ara» (D-073): la que sona baixa amb un fos i la triada comença de seguida; les d'entremig es treuen de la cua (`skipped`) i, si venien d'un esdeveniment, queden com a `NO_EMES`. Si la pista ja no hi és o hi ha un tall programat d'aquí a uns segons, `bad_params` | fase 3, pas 2a |
| `player.next` | `fadeMs` (de 0 a 10.000; per defecte 1.000) | `{ queue }`. La que sona baixa amb un fos i la següent comença de seguida | fase 1, pas 7c |
| `aux.get` | — | `{ aux, loaded }` (vegeu 4.8). `loaded` des de la fase 3, pas 4a | fase 2, pas 5a |
| `aux.play` | `trackId` (de la biblioteca) **o bé** `path` (un fitxer qualsevol); `player` (1–3, per defecte 1); `duckingDb` (atenuació de la música mentre sona, de −60 a 0; 0 = cap; si no s'envia, la per defecte de `ducking.get`, D-078) | `{ player, item }`. Sona per sobre del que soni, sencer i des del principi; si el reproductor ja sonava, el substitueix. **Sense `trackId` ni `path`** (fase 3, pas 4a): sona la pista carregada amb `aux.load`; si no n'hi ha cap, `bad_params`. Si no queden veus lliures, `bad_params` | fase 2, pas 5a |
| `aux.load` | `player` (1–3, per defecte 1) i `trackId` **o bé** `path` | `{ loaded }` (4.8). Carrega la pista al reproductor **sense fer-la sonar**: sona amb `aux.play` sense pista, i s'hi queda carregada després (es pot tornar a fer sonar). No atura el que hi soni. Si el fitxer no hi és, `bad_params`. Avisa amb `aux.loaded` | fase 3, pas 4a |
| `aux.unload` | `player` (1–3, per defecte 1) | `{ loaded }`. Treu la pista carregada; el que hi soni continua sonant. Avisa amb `aux.loaded` | fase 3, pas 4a |
| `aux.stop` | `player` (per defecte 1), `fadeMs` (de 0 a 10.000; per defecte 500) | `{ stopped, aux }`. `stopped`: fals si no sonava res | fase 2, pas 5a |
| `ducking.get` | — | `{ auxiliary, sweepers }`: l'atenuació per defecte de la música (en dB; 0 = cap) quan un auxiliar o un sweeper no en diuen cap (D-078). Si no s'ha canviat mai, −10 i −8 | fase 3, pas 4b |
| `ducking.set` | `auxiliary`, `sweepers` (de −60 a 0; 0 = sense atenuació). Tots dos opcionals: el que no s'envia es queda com està | `{ auxiliary, sweepers }`. Es desa a la configuració i s'aplica de seguida (el que ja sona o ja està preparat no canvia) | fase 3, pas 4b |
| `processor.get` | — | `{ processor }` (4.9): el processador de la sortida (preajust del compressor i volum) | fase 3, pas 6 |
| `processor.set` | `preset` (un dels de `processor.presets`), `volumeDb` (de −20 a 10; s'arrodoneix a 0,1). Tots dos opcionals | `{ processor }`. S'aplica de seguida (en uns 20 ms, sense clics), es desa a la configuració i s'avisa totes les interfícies amb `processor.changed` | fase 3, pas 6 |
| `notes.get` | — | `{ text }`: el bloc de notes compartit de la pestanya «Notes» (buit si no n'hi ha) | fase 3, pas 4a |
| `ui.get` | — | `{ zoom, collapsed }`: preferències de la interfície desades al motor (D-083). `zoom`: de 0,5 a 1,5 (per defecte 1). `collapsed`: requadres plegats, `{ "aux": true, … }` (per defecte, cap) | fase 3, pas 4c |
| `ui.set` | `zoom` (de 0,5 a 1,5) i `collapsed` (nom del requadre → `true`/`false`; els que no s'envien es queden com estan), tots dos opcionals | `{ zoom, collapsed }`. Es desa a la configuració. No avisa les altres interfícies: cadascuna l'aplica en connectar-se | fase 3, pas 4c |
| `notes.set` | `text` (pot ser buit; com a molt 20.000 caràcters) | `{ text }`. Es desa a la configuració (es conserva en reiniciar) i s'avisa les altres interfícies amb `notes.changed` | fase 3, pas 4a |
| `library.list` | `folder`, `limit` (per defecte 100, com a molt 1.000) | `{ tracks: [ … ] }` (vegeu 4.4): pistes de la carpeta i subcarpetes. De moment, per a les proves | fase 1, pas 7c |
| `library.search` | `text` (paraules que han de sortir totes a l'artista, al títol, a l'àlbum o a la ruta; **sense distingir accents ni majúscules**: «magazin» troba «Magazín»; buit = totes), `categoryId`, `subcategoryId`, `tagId` (filtres, opcionals), `state` (`all`, `missing`: el fitxer falta, `retired`: retirades, `disabled`: desactivades, `unclassified`: sense subcategoria; per defecte `all`), `offset` (per anar carregant; per defecte 0), `limit` (per defecte 50, com a molt 200) | `{ tracks: [ … ], more }` (vegeu 4.4), per artista i títol. `more`: n'hi ha més que les retornades | fase 2, pas 8b; filtres, `offset` i sense accents, fase 3, pas 3a; `state`, fase 3, pas 7a |
| `library.filters` | — | `{ categories: [ { id, name, count, subcategories: [ { id, name, count } ] } ], tags: [ { id, name, count } ], states: { all, missing, retired, disabled, unclassified } }`: les opcions dels filtres de la cerca, amb quantes pistes tenen | fase 3, pas 3a; `states`, pas 7a |
| `library.get` | `trackId` | `{ track }`: la fitxa sencera (4.4) | fase 3, pas 7a |
| `library.update` | `trackId` i els camps que es canvien (els altres es queden igual): `artist`, `title`, `album`, `year`, `type` (4.4), `subcategoryId` (`null` = cap), `tags` (llista de noms; substitueix les d'ara i crea les que no existeixen), `variableDuration`, `validFrom` i `validUntil` (`'AAAA-MM-DD'` o `null`), `enabled`, `notes` | `{ track }` (la fitxa sencera) i `library.changed`. Si un valor no és vàlid, `bad_params` i no es canvia res. El que ja és a la cua no canvia | fase 3, pas 7a |
| `library.relink` | `trackId`, `path` (ruta completa d'un fitxer d'àudio que existeixi) | `{ track }` i `library.changed`. La pista passa a aquest fitxer (`fileState: ok`), amb la durada nova; es conserven la fitxa i l'historial. Si els punts no són manuals, l'analitzador els torna a calcular en segon pla (mentrestant, els d'abans). Si el fitxer ja és d'una altra pista, `database_error` | fase 3, pas 7a |
| `library.delete` | `trackId` | `{ deleted }` i `library.changed`. Esborra la pista de la biblioteca (el fitxer no es toca mai). L'historial la conserva (artista, títol i ruta). Si la fa servir algun esdeveniment, `database_error` amb els noms | fase 3, pas 7a |
| `subcategory.empty` | `subcategoryId`, `dryRun` (`true` = només diu què faria) | `{ subcategoryId, name, tracks, usedByEvents, rotations: [noms] }` i, si no és `dryRun`, també `removed` i `keptForEvents`, i `library.changed`. Esborra de la biblioteca les pistes de la subcategoria que no fa servir cap esdeveniment (els fitxers no es toquen; l'historial les conserva); les altres es queden com són. `rotations`: les rotacions amb algun pas d'aquesta subcategoria (per avisar). Mentre s'importa una carpeta, `bad_params` | fase 3, pas 7b |
| `folders.get` | `subcategoryId` | `{ folder }` (4.11): la carpeta vigilada de la subcategoria i com queden les pistes noves | fase 3, pas 7c |
| `folders.set` | `subcategoryId`, `folder` (ruta completa d'una carpeta que existeixi; `null` = deixar de vigilar-la), `renewable`, i per a les pistes noves `type`, `tags`, `variableDuration` | `{ folder }` i `library.changed`. Es revisa de seguida. No pot ser una unitat sencera ni una carpeta que ja vigila una altra subcategoria (ni que en contingui una, ni que hi sigui a dins) | fase 3, pas 7c |
| `folders.status` | — | `{ folders: [ … ] }` (4.11): com estan les carpetes vigilades a l'última revisió | fase 3, pas 7c |
| `folders.scan` | — | `{ scanning: true }`: revisa les carpetes ara (el resultat arriba amb `folders.changed`) | fase 3, pas 7c |
| `folders.log` | `subcategoryId` (opcional), `limit` (per defecte 100, com a molt 1.000) | `{ entries: [ { id, time, subcategoryId, subcategory, path, action, detail } ] }`, els més recents primer. `action`: `imported`, `recovered`, `missing`, `deleted`, `updated`, `error` o `folder` (problema de la carpeta, o que s'ha resolt) | fase 3, pas 7c |
| `library.waveform` | `trackId`, `fromMs` i `toMs` (el tros; per defecte tota la pista), `buckets` (per defecte 800, com a molt 2.000) | `{ trackId, fromMs, toMs, floorDb, peaks: [ … ] }`: el pic de cada tros, de 0 a 255 en una escala de dB (`floorDb` = −60 dB → 0; 0 dB → 255). Només es descodifica el tros demanat. Si el fitxer falta, `bad_params` | fase 3, pas 7e |
| `library.setCues` | `trackId`, `startMs`, `mixMs`, `endMs` (0 ≤ inici ≤ mescla ≤ final ≤ durada) | `{ track }` (la fitxa sencera, `cueSource: manual`) i `library.changed`. L'analitzador no els tornarà a tocar (D-042). El que ja és a la cua no canvia | fase 3, pas 7e |
| `library.recalculateCues` | `trackId` | `{ track }` (`cueSource: analyzer`) i `library.changed`: torna a calcular els punts amb l'analitzador, també si eren manuals | fase 3, pas 7e |
| `library.missingFolders` | — | `{ folders: [ { folder, count } ] }`: les carpetes on hi ha pistes amb el fitxer que falta, les que més primer (40), per triar l'arrel d'un canvi d'arrel | fase 3, pas 7d |
| `library.rerootPreview` | `from` (el principi de les rutes d'ara), `to` (l'arrel nova: una carpeta que existeixi) | `{ from, to, tracks, found, notFound, conflicts, examples: [ { from, to, exists, conflict } ] }` (10 exemples): què passaria, sense canviar res. Mira si cada fitxer hi és | fase 3, pas 7d |
| `library.reroot` | `from`, `to`, `onlyFound` (per defecte `true`: només les que trobaran el fitxer) | El mateix que `library.rerootPreview` i, a més, `changed`, `changedFound`, `skipped`, `foldersMoved`; i `library.changed`. Tot en una transacció (o canvien totes o cap). La fitxa, els punts i l'historial es conserven; les que xoquen amb una altra pista no es toquen; les carpetes vigilades dins de l'arrel antiga passen a la nova | fase 3, pas 7d |
| `folders.renewPreview` | `subcategoryId`, `folder` (la carpeta nova) | `{ subcategoryId, name, folder, files, keep, enter, leave, leaveInEvents, elsewhere, keepNames, enterNames, leaveNames, leaveInEventsNames }`: què passaria en renovar la bossa (D-091), sense canviar res. Les llistes de noms, com a molt 300 | fase 3, pas 7c-3 |
| `folders.renew` | `subcategoryId`, `folder`, `watch` (`true` = després, la subcategoria vigila aquesta carpeta) | `{ import }` (4.10, amb `renew: true`): es fa en segon pla com una importació. Primer les que es mantenen passen a apuntar a la carpeta nova (`moved`), després s'importen les noves i, només si acaba bé, s'esborren les que surten (`removed`; les dels esdeveniments, `keptForEvents`). Amb una carpeta sense àudio, `failed` i no es toca res | fase 3, pas 7c-3 |
| `library.import` | `folder` (ruta completa d'una carpeta que existeixi), `subcategoryId` i, per a les pistes noves, `type` (per defecte `music`), `tags` (llista) i `variableDuration`; `emptySubcategory` (`true` = en acabar bé, esborra les pistes de la subcategoria que no són a la carpeta) | `{ import }` (4.10). Comença a importar en segon pla els fitxers d'àudio de la carpeta i les subcarpetes; el progrés arriba amb `library.importProgress`. Una sola importació alhora (si n'hi ha una en curs, `bad_params`) | fase 3, pas 7b |
| `library.importStatus` | — | `{ import }`: la importació en curs o l'última (`null` si no se n'ha fet cap des que ha arrencat el motor) | fase 3, pas 7b |
| `library.importCancel` | — | `{ cancelled }`: `false` si no n'hi havia cap en curs. El que ja s'ha importat es queda | fase 3, pas 7b |
| `category.save` | `name`; `categoryId` per canviar-ne el nom (sense, en crea una) | `{ id, name }` i `library.changed` (amb `trackId: null`). Nom repetit: `database_error` | fase 3, pas 7a |
| `subcategory.save` | `name`; `categoryId` per crear-ne una, o `subcategoryId` per canviar-ne el nom | `{ id, name, categoryId, categoryName }` i `library.changed`. Nom repetit dins de la categoria: `database_error` | fase 3, pas 7a |
| `autodj.get` | — | `{ autodj }` (vegeu 4.5) | fase 1, pas 8 |
| `sweepers.set` | `subcategoryId` (un número, o `null` perquè no n'hi hagi cap; obligatori) | `{ autodj }`, amb la categoria nova a `sweepers` (4.5). La categoria de sweepers del trepitjador: d'on surten els sweepers dels passos de rotació amb `sweeper` (com el desplegable de RadioDJ). Es desa i es manté en tornar a arrencar. Les pistes que ja són a la cua es queden amb el sweeper que tenien. Si la subcategoria no existeix, `bad_params`. També la canvia l'acció `SWEEPERS` dels esdeveniments; tots dos avisen amb `autodj.changed` | fase 3, pas 5a |
| `autodj.set` | `enabled` (`true`/`false`), `rotationId`, `minutes` (si s'atura: quant temps, d'1 a 720; per defecte 120). Tots opcionals: el que no s'envia es queda com està. Aturat, es torna a activar sol quan s'acaba el temps o si la cua es queda sense res (D-073) | `{ autodj }`. La rotació es desa i es manté en tornar a arrencar; `enabled` no: el motor sempre arrenca amb l'AutoDJ actiu (D-053). Si la rotació no existeix, `bad_params` | fase 1, pas 8 |
| `rotations.list` | — | `{ rotations: [ … ] }` (vegeu 4.5). Cada pas porta també `categoryId`, `subcategoryId` i `tagId` (fase 3, pas 8a) | fase 1, pas 8 |
| `rotations.save` | `name`, `steps` (en ordre: `{ bagId }` —una bossa amb nom, pas 8c—, `{ subcategoryId }` o `{ categoryId }` —qualsevol subcategoria de la categoria—, i opcionals `tagId`, `selection` (`leastRecent`, `leastPlayed`, `random`), `repeatRule`, `sweeper`); `rotationId` per substituir-ne una (sense, en crea una) | `{ rotation }` i `rotations.changed`. Com a mínim un pas. Nom repetit: `database_error`. Si és l'activa, l'AutoDJ agafa els passos nous de seguida (el que ja és a la cua no canvia) | fase 3, pas 8a |
| `bags.list` | — | `{ bags: [ { id, name, subcategoryId, subcategory, changedAt, changedBy, steps } ] }`: les bosses amb nom (D-096), per nom. `steps`: quants passos de rotació hi apunten | fase 3, pas 8c |
| `bags.save` | `name` i, per crear-ne una, `subcategoryId` (pot ser `null`); `bagId` per canviar-ne el nom | `{ bag }`, `bags.changed` i `rotations.changed`. Nom repetit: `database_error` | fase 3, pas 8c |
| `bags.set` | `bagId`, `subcategoryId` (`null` = cap: els passos que hi apunten se salten) | `{ bag }`, `bags.changed` i `rotations.changed`. Els passos triaran d'aquesta subcategoria a partir de la cançó següent (el que ja és a la cua no canvia). Es desa (es manté si el motor es reinicia) | fase 3, pas 8c |
| `bags.delete` | `bagId` | `{ deleted }`. No es pot si hi apunta algun pas de rotació o alguna acció `BOSSA` (es diu quins): `bad_params` | fase 3, pas 8c |
| `rotations.preview` | `rotationId`, `count` (per defecte 20, com a molt 100) | `{ rotationId, fromStep, separation, items: [ { stepIndex, step, track, relaxed, problem, fallback } ] }`: les properes pistes que triaria la rotació, **sense canviar res**, amb la mateixa tria que l'AutoDJ (comptant la cua i el que ha sonat fa poc). Si és l'activa, comença pel pas següent (`fromStep`); si no, pel primer. `relaxed`: la regla que s'ha hagut d'afluixar (`separació d'artista`, `separació de cançó`, `artista que ja és a la cua`, `cançó que ja és a la cua`) o `null`. `fallback`: no hi havia res a cap pas i surt de la reserva; llavors `step` diu d'on (8d: «Reserva: rotació «…», pas 2 (…)» o «Reserva: tota la biblioteca») | fase 3, pas 8b |
| `separation.get` | — | `{ trackMinutes, artistMinutes }`: la separació real (D-095). Si no s'ha canviat mai, 360 i 60 | fase 3, pas 8b |
| `separation.set` | `trackMinutes`, `artistMinutes` (de 0 a 10.080; 0 = sense temps mínim), tots dos opcionals | `{ trackMinutes, artistMinutes }`. Es desa i val per a les pistes que es triïn a partir d'ara | fase 3, pas 8b |
| `reserve.get` | — | `{ rotationId, rotationName, excludedSubcategories: [ { id, name, categoryName } ] }`: la reserva de l'AutoDJ (D-099). Si cap pas de la rotació activa no troba res, sona la rotació de reserva (`rotationId`, o `null` si no n'hi ha) i, si tampoc, música de tota la biblioteca sense les subcategories «no de reserva» (si només en queden d'aquestes, també: mai silenci). Sempre amb avís a `autodj.problem` | fase 3, pas 8d |
| `reserve.set` | `rotationId` (o `null`: cap), `excludedSubcategoryIds` (llista d'id), tots dos opcionals: el que no s'envia no canvia | Com `reserve.get`, i `rotations.changed`. Es desa. Les pistes de la cua que venien de la reserva es tornen a triar (D-097) | fase 3, pas 8d |
| `rotations.delete` | `rotationId` | `{ deleted }` i `rotations.changed`. No es pot si és la rotació activa de l'AutoDJ, si és la rotació de reserva (8d) o si la posa algun esdeveniment (es diu quins): `bad_params` | fase 3, pas 8a |
| `history.list` | `from`, `to`: una hora ISO 8601 amb zona (`"2026-11-10T12:00:00Z"`) o un dia (`"2026-11-10"`, en hora local; a `to`, el dia hi és inclòs). `search`: text de l'artista o del títol. `limit`: per defecte 100, com a molt 2.000. Tots opcionals | `{ entries: [ … ], more }` (vegeu 4.6): les més recents primer. `more`: n'hi ha més que les retornades | fase 1, pas 9 |
| `nowplaying.get` | — | `{ nowPlaying }` (vegeu 4.6) | fase 1, pas 9 |
| `nowplaying.set` | `enabled`, `path` (ruta completa del fitxer; `null` o `""` = el per defecte), `template` (ha de tenir `$artist$` o `$title$`), `types` (llista de tipus de pista, 4.2). Tots opcionals: el que no s'envia es queda com està | `{ nowPlaying }`. Es desa a la configuració i es torna a escriure el títol de seguida | fase 1, pas 9 |
| `scheduler.list` | — | `{ events: [ … ] }` (vegeu 4.7): tots, també els desactivats i les plantilles | fase 2, pas 3 |
| `scheduler.get` | `eventId` | `{ event }` | fase 2, pas 3 |
| `scheduler.save` | `event` (4.7). Sense `id` (o 0), en crea un de nou; amb `id`, el substitueix sencer (els camps que no s'envien prenen el valor per defecte) | `{ event, warnings }`: l'esdeveniment tal com s'ha desat i els avisos (vegeu 4.7; pas 8c). El planificador el té en compte de seguida. Si no és coherent (p. ex. «cada N setmanes» sense dia de referència), `bad_params` amb el motiu en català | fase 2, pas 3 |
| `scheduler.preview` | `event` (4.7), encara que no s'hagi desat; `limit` (per defecte 10, com a molt 100) | `{ occurrences: [ … ], warnings }` (4.7): les pròximes emissions des d'ara (fins a 400 dies), comptant-hi les dates excloses i els canvis d'hora, i els avisos (pas 8c). No desa res. Si l'esdeveniment no és coherent, `bad_params` amb el motiu en català | fase 2, pas 8b |
| `scheduler.delete` | `eventId` | `{ deleted: <id> }`. El registre d'execució es conserva | fase 2, pas 3 |
| `scheduler.occurrences` | `from`, `to` (com a `history.list`; per defecte, d'ara a 24 h; com a molt 400 dies), `eventId` (només aquest, encara que estigui desactivat), `includeDisabled` (també els desactivats; per defecte, no), `limit` (per defecte 200, com a molt 2.000) | `{ occurrences: [ … ], more }` (4.7), per ordre. Sense `eventId`, només els actius que no són plantilles (amb `includeDisabled`, també els desactivats) | fase 2, pas 3; `includeDisabled`, pas 8a |
| `scheduler.status` | — | `{ paused }`: si els esdeveniments estan aturats, `{ until, by }` (fins quan, hora ISO, i qui); si funcionen, `null` | fase 3, pas 2c |
| `scheduler.pause` | `minutes` (1 a 720) **o bé** `until` (una hora ISO, com a molt d'aquí a 12 h) | `{ paused }`. «Atura els esdeveniments» (D-073, D-077): mentre estan aturats no se'n dispara cap, ni els senyals horaris, i queden al registre com a `OMES_ATURAT`; en reactivar-los, el que ja havia d'haver sonat tampoc no sona. Mai indefinidament: a l'hora tornen a funcionar sols. Es manté si el motor es reinicia | fase 3, pas 2c |
| `scheduler.resume` | — | `{ paused: null }`. Els torna a activar abans d'hora. El que ja havia d'haver sonat no sona tard (`OMES_ATURAT`); només sona el que encara ha d'arribar (D-077) | fase 3, pas 2c |
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

- Previsió (fase 3, pas 1, D-074): `queue` porta `currentEndsAt` (quan s'acaba la que sona; `null` si no sona res) i cada element de `upcoming` porta `plannedAt` (hora prevista d'inici; `null` si no se sap, perquè d'alguna pista d'abans no se sap quant dura). Hores locals amb zona (ISO 8601). Es calculen amb els fotogrames del mesclador, els punts de mescla i el solapament màxim, com l'encadenament; la pista ja programada, amb el seu fotograma exacte. No compten els esdeveniments que encara no s'han disparat: la interfície ho compara amb `scheduler.occurrences`.

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
- `sweeper` (fase 3, pas 5a; D-084): el sweeper que sonarà **a sobre de l'inici** d'aquesta pista (trepitjador), amb els mateixos camps que una pista de la cua (sense `sweeper`), o `null`. El tria l'AutoDJ en posar-la a la cua.
- `durationMs`: quant sonarà, del punt d'inici al final (sense restar el solapament amb la següent), o `null` si no se sap (p. ex. un fitxer que no és a la biblioteca). Des del pas 10c.
- `rotationId` i `rotationStep` (fase 3, pas 8c; D-097): la rotació i el pas (índex des de 0) d'on l'ha triada l'AutoDJ, o `null` (manual, esdeveniment o música de reserva). En tornar a triar la cua, la rotació continua per aquest pas. `rotationStepName`: com era el pas quan s'ha triat (p. ex. «Bossa «Generalista A» (Music / Adult 6)»), o `null`. Si l'AutoDJ l'ha triada de la reserva (D-099), `rotationStep` és `null` i `rotationStepName` diu d'on surt: «Reserva: rotació «…», pas 2 (…)» o «Reserva: tota la biblioteca». La interfície ho mostra a cada pista de la cua («Pas 3 · …»).

### 4.3. Sortida (D-038)

```json
{ "configured": { "id": "{0.0.0.00000000}.{…}", "name": "Altavoces - Realtek (…)" },
  "current": { …dispositiu (4.1)… },
  "emergency": false,
  "problem": null,
  "latencyMs": 119.9,
  "latencyMeasured": true }
```

- `configured`: la desada amb `output.set` (`id` i `name` `null` si no n'hi ha cap).
- `current`: per on s'emet ara (`null` si no hi ha cap sortida).
- `emergency`: s'emet per una sortida d'emergència (no és la configurada). Llavors `problem` ho explica i la interfície ho ha de mostrar fins que es resolgui (D-039).
- `problem`: text en català si hi ha algun problema (sortida d'emergència, cap sortida disponible…), o `null`.
- `latencyMs`: quant tarda el so a sortir per la sortida d'ara, en ms (fase 2, pas 4b). El motor la mesura cada segon amb el rellotge de la targeta (la mediana de les últimes 9 mesures) i la fa servir per tallar a l'hora exacta.
- `latencyMeasured`: `false` si encara no s'ha pogut mesurar (acaba d'obrir la sortida, o la targeta no ho diu): llavors `latencyMs` és la suposada, 100 ms.

### 4.4. Pistes

```json
{ "id": 23977, "path": "O:\\musica\\…", "artist": "…", "title": "…", "type": "music",
  "durationMs": 215400, "cueSource": "analyzer", "fileState": "ok" }
```

- `cueSource`: d'on surten els punts d'encadenament: `radiodj`, `analyzer`, `manual` o `null` (encara no n'hi ha).
- `fileState`: `ok`, `missing` (el fitxer falta) o `retired` (ha sortit d'una bossa renovable).

- Pista (fase 3, pas 3a): també `album`, `lastPlayed` (l'última vegada que ha sonat, hora ISO, o `null`) i `playCount`.
- `enabled` (fase 3, pas 7a): `false` si s'ha desactivat a mà (l'AutoDJ no la tria).
- `type`: `music`, `sweeper`, `stationId`, `promo`, `commercial`, `program`, `news`, `timeAnnouncement`, `timeSignal`, `stream` o `other`.

**Fitxa sencera** (`library.get`, `library.update`, `library.relink`; fase 3, pas 7a): el mateix i, a més:

```json
{ "year": "1998", "variableDuration": false, "subcategoryId": 5,
  "subcategory": { "id": 5, "name": "Adult 5", "categoryId": 1, "categoryName": "Music" },
  "tags": ["català"], "validFrom": null, "validUntil": "2027-01-06", "enabled": true, "notes": null,
  "startMs": 120, "mixMs": 208300, "endMs": 214900, "priority": 50,
  "fileSize": 8615822, "fileModified": "2026-10-02T09:12:00.0000000+00:00", "radioDjId": 23977,
  "usedBy": [ { "id": 12, "name": "Senyal de les 12" } ] }
```

- `usedBy`: els esdeveniments que la fan servir (accions PISTA o AUXILIAR). Mentre n'hi hagi, no es pot esborrar.

### 4.5. AutoDJ i rotacions (D-049)

```json
{ "enabled": true, "rotation": { "id": 1, "name": "OnaBages" }, "nextStep": 3, "problem": null }
```

- `enabled`: l'AutoDJ afegeix pistes quan a la cua n'hi ha menys de 2. En arrencar, el motor l'activa sol (amb la configuració desada o, la primera vegada, amb la primera rotació).
- `rotation`: la rotació activa, o `null` si no n'hi ha cap (llavors sona música de tota la biblioteca, amb avís).
- `nextStep`: índex del pas que es farà servir la pròxima vegada (0 = el primer).
- `problem`: avís en català (els passos sense pistes que se salten, i es manté mentre no en tinguin; la rotació que no troba res…) o `null`.

Rotació:

```json
{ "id": 1, "name": "OnaBages",
  "steps": [ { "category": "Sweepers", "subcategory": "Ona Bages", "tag": "SW - catala",
               "selection": "leastRecent", "repeatRule": false, "sweeper": false } ] }
```

- `subcategory`: `null` si el pas tria de tota la categoria. `tag`: `null` si no filtra per etiqueta.
- `selection`: `leastRecent` (la que fa més temps que no sona), `leastPlayed` (la que ha sonat menys vegades) o `random`.
- `repeatRule`: s'apliquen els criteris de repetició (no repetir pista ni artista de la cua).
- `sweeper`: trepitjador (sweeper) a l'inici de la pista, «A l'inici» de RadioDJ (fase 3, pas 5a; D-084): la pista porta un sweeper de la categoria de sweepers activa.

- Fase 3, pas 2b (D-076): `autodj` porta també `disabledUntil` (si està aturat, fins quan; hora ISO), `notice` (l'avís de quan s'ha tornat a activar sol, per temps o perquè la cua s'acabava; es manté fins al pròxim canvi), `rotationChangedBy` («a mà», «en arrencar el motor», «esdeveniment «Converses»») i `rotationChangedAt` (quan).
- Fase 3, pas 5a (D-084): `autodj` porta també `sweepers`, la categoria de sweepers del trepitjador, o `null` si no n'hi ha cap: `{ "subcategoryId": 55, "name": "Sweepers / Ona Bages", "changedBy": "esdeveniment «Dance90»", "changedAt": "…" }`.

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
- `params` de les accions disponibles ara: `PISTA` `{ songId }` (id de la pista a la biblioteca, el `trackId` de la cua), `AUXILIAR` `{ songId, player, duckingDb }` (`player` 1–3, per defecte 1; `duckingDb` opcional: si no hi és, el de l'esdeveniment), `ROTACIO` `{ rotacioId }`, `AUTODJ` `{ actiu }`, `SWEEPERS` `{ subcategoriaId }` (fase 3, pas 5a: canvia la categoria de sweepers, com `sweepers.set`), `BOSSA` `{ bossaId, subcategoriaId }` (fase 3, pas 8c: canvia la subcategoria d'una bossa amb nom, com `bags.set`). Les altres es poden desar, però encara no fan res (el registre ho diu).
- Accions a les respostes de `scheduler.list`, `scheduler.get` i `scheduler.save` (pas 8a): a més de `type`, `params` i `daysMask`, porten com es mostren: `label` (el títol de la pista amb l'artista, «Rotació OnaBages»…), `detail` (una línia més petita: tipus de pista i durada, reproductor auxiliar…), `missing` (la pista no existeix o el fitxer falta: no sonarà) i `durationMs` (quant sona la pista, si se sap). Són només per mostrar: en desar, el motor no els llegeix.
- Avisos (`warnings` de `scheduler.save` i `scheduler.preview`, pas 8c, D-071): llista de `{ "text": "…", "dates": ["2026-11-16", …] }`, en català i a punt per mostrar. `dates`: els dies dels pròxims 14 en què passa (buit si no depèn del dia). Els calcula el motor amb una simulació dels TALLA i ENCUA actius (més l'esdeveniment que s'edita, encara que estigui desactivat) i les durades de les pistes: programa més llarg que la seva franja, ENCUA que no arribarà a sonar o que es tallarà, contingut més llarg que `maxDurationSeconds`, emissions que no sonaran pel canvi d'hora (fins a un any vista) i pistes que no existeixen, sense fitxer o sense durada. Només hi surten els avisos en què intervé l'esdeveniment (com a afectat o com a causa). Són informatius: no impedeixen desar.
- Modes disponibles: `ENCUA` (el contingut va al davant de la cua; amb `clearQueue`, abans s'esborra la cua), `TALLA`, `SUPERPOSA` i `SISTEMA`.
- `SUPERPOSA` i acció `AUXILIAR` (pas 5b, D-064 i D-065): el fitxer s'obre uns 30 s abans en un reproductor auxiliar i sona a la mostra exacta de l'hora, per sobre del que soni, sense tocar la cua. L'acció `AUXILIAR` també serveix en un `ENCUA` o un `TALLA` (p. ex. senyals horaris i un informatiu a la cua). En un `SUPERPOSA`, les accions `PISTA` no sonen (avís al registre). Una acció `AUXILIAR` per reproductor i esdeveniment. Els esdeveniments amb contingut a la mostra exacta (`TALLA`, `SUPERPOSA` o amb accions `AUXILIAR`) es programen uns 2 s abans i la línia del registre es completa quan comencen a sonar, com el `TALLA`.
- `TALLA` (pas 4, D-061): es programa uns 2 s abans, a la mostra exacta de l'hora. La pista que sona comença el fos (`fadeMs`; si és `null`, 1.000 ms) en aquell instant i la primera pista de l'esdeveniment comença alhora; la resta va darrere. Amb `clearQueue`, abans s'esborra la cua.

Ocurrència (`scheduler.occurrences`):

```json
{ "eventId": 12, "name": "El Magazín", "mode": "TALLA", "enabled": true, "repeat": "CAP", "maxDurationSeconds": 3600,
  "date": "2026-11-20", "time": "10:00:00", "at": "2026-11-20T10:00:00.0000000+01:00", "skippedClockChange": false }
```

- `enabled`, `repeat` i `maxDurationSeconds` (pas 8a) i `clearQueue` (fase 3, pas 1): els de l'esdeveniment, per dibuixar l'emissió a la graella i fer la previsió de la cua sense haver de buscar-lo.

- `date` i `time`: dia i hora locals programats. `at`: l'instant. `skippedClockChange`: aquella hora no existeix pel canvi d'hora i no sonarà (D-057); `at` és llavors el moment del salt.

Línia del registre (`scheduler.log` i `scheduler.fired`):

```json
{ "id": 33, "eventId": 12, "name": "Mitja hora", "scheduledFor": "2026-11-20T09:30:00.0000000+00:00",
  "executedAt": "2026-11-20T09:30:00.0120000+00:00", "result": "OK", "detail": "2 pistes a la cua" }
```

- `result`: `OK`, `OMES_TARD` (s'hi ha arribat més tard que la tolerància, p. ex. amb el motor aturat), `OMES_CONFLICTE` (coincidia amb un TALLA o ENCUA de més prioritat), `OMES_CANVI_HORA`, `NO_EMES` o `ERROR`. `executedAt` és `null` si no s'ha executat. `name` és el nom en aquell moment.
- `NO_EMES`: el contingut d'una emissió (normalment un ENCUA) s'ha esborrat de la cua abans de sonar, perquè un esdeveniment posterior amb `clearQueue` l'ha tret. És una línia nova amb el `scheduledFor` d'aquella emissió; la línia `OK` d'aquella emissió es manté. No compta per als certificats.
- Un `TALLA` (i un `SUPERPOSA` o un esdeveniment amb accions `AUXILIAR`) s'anota en programar-lo, amb `executedAt: null`. Quan la primera pista (o l'auxiliar) comença, la mateixa línia (mateix `id`) es completa: `executedAt` és l'hora en què sona i `detail` acaba amb «ha començat a la mostra prevista» o «ha començat amb +N ms de diferència». Per això `scheduler.fired` arriba dues vegades amb el mateix `id`.

### 4.8. Reproductors auxiliars (D-064)

```json
"aux": [ { "player": 1, "item": { "id": "…", "title": "senyals horaris", … }, "started": true,
           "elapsedMs": 1200, "remainingMs": 4800, "duckingDb": -12 } ]
```

- Només hi surten els reproductors que no estan lliures (els que sonen o esperen la seva hora). N'hi ha 3; el 1 és el dels senyals horaris.
- `item`: com un element de la cua (4.2). `started`: fals si encara espera l'hora (un esdeveniment preparat).
- `duckingDb`: l'atenuació de la música mentre sona (`null` = cap). Un esdeveniment fa servir la de l'acció; si no en té, la de l'esdeveniment; si tampoc, la per defecte (`ducking.get`, D-078). La música baixa en els 50 ms d'abans que comenci l'auxiliar i torna a pujar en 500 ms quan s'acaba.
- Un auxiliar no toca la cua: no surt a `queue.changed` ni a l'historial de la cua.
- `loaded` (fase 3, pas 4a): la pista carregada a mà a cada reproductor, sempre amb una entrada per reproductor: `[ { "player": 1, "item": null }, { "player": 2, "item": { … } }, … ]`. És independent del que sona (`aux`) i del que preparen els esdeveniments.

### 4.9. Processador de la sortida (D-086)

```json
{ "preset": "suau", "presetName": "Suau", "volumeDb": 0, "ceilingDb": -0.3, "minVolumeDb": -20, "maxVolumeDb": 10,
  "presets": [ { "id": "cap", "name": "Sense compressió" }, { "id": "suau", "name": "Suau" },
               { "id": "mitja", "name": "Mitjà" }, { "id": "fort", "name": "Fort" } ] }
```

- S'aplica a la mescla final (música, auxiliars i sweepers), just abans de la targeta. Els mesuradors (`meters`) mostren el so ja processat.
- `preset`: el preajust del compressor. `cap` no comprimeix. La primera vegada, `suau` (com RadioDJ). Els valors de cada preajust són provisionals (es decidiran escoltant-ho).
- `volumeDb`: el volum de la sortida (0 = sense canvi).
- `ceilingDb`: el sostre de seguretat, **sempre actiu** (també amb `cap`): la sortida no passa mai d'aquest nivell, encara que dues pistes fortes se solapin.
- El processador retarda el so 1,5 ms (l'anticipació del sostre).

### 4.10. Importació d'una carpeta (D-088)

```json
{ "folder": "O:\\musica\\BOSSA 9", "subcategory": "Music / Ona Bages Adult 9", "state": "running",
  "total": 289, "done": 120, "added": 115, "alreadyThere": 3, "reactivated": 1, "withoutSound": 0,
  "unreadable": ["O:\\musica\\BOSSA 9\\x.mp3: …"], "unreadableCount": 1,
  "started": "2026-11-12T11:02:00.0000000+00:00", "finished": null, "error": null }
```

- `state`: `listing` (buscant els fitxers; `total` encara és 0), `running`, `done`, `cancelled` (aturada; el que s'ha importat es queda) o `failed` (`error` diu per què).
- `added`: pistes noves, amb els punts calculats. `alreadyThere`: ja eren a la biblioteca (no es toquen). `reactivated`: ja hi eren amb el fitxer que faltava (o retirades) i el tornen a tenir, amb la mateixa fitxa. `withoutSound`: noves però tota silenci (sense punts).
- Una pista nova que coincideix amb una de la mateixa subcategoria que falta (o retirada) amb el **mateix nom de fitxer** no es duplica: aquella es recupera amb el fitxer nou i compta a `reactivated`.
- `emptySubcategory`, `removed`, `keptForEvents`: si s'ha demanat buidar la subcategoria, quantes s'han esborrat i quantes s'han conservat tal com eren perquè les fan servir esdeveniments. Només es buida si la importació acaba bé i la carpeta té àudio; si no, `warning` diu per què no.
- `warning`: un avís que no és un error, o `null`.
- `renew`, `moved` (fase 3, pas 7c-3): és una renovació de la bossa (`folders.renew`), i quantes cançons que continuen han passat a apuntar a la carpeta nova.
- `unreadable`: els que no es poden llegir, amb el motiu (com a molt els 200 primers; el total és `unreadableCount`, i tots surten al registre del motor).
- Es fa en un fil de prioritat baixa, un fitxer cada vegada: l'emissió va primer.

### 4.11. Carpetes vigilades (D-026, D-033, D-090)

Configuració d'una subcategoria (`folders.get`, `folders.set`):

```json
{ "subcategoryId": 23, "name": "Music / Ona Bages Actualitat", "folder": "I:\\Mi unidad\\…\\ACTUAL-1",
  "renewable": true, "type": "music", "tags": [], "variableDuration": false }
```

Estat (`folders.status`, `folders.changed`), una entrada per subcategoria amb carpeta:

```json
{ "subcategoryId": 23, "name": "Music / Ona Bages Actualitat", "folder": "I:\\…\\ACTUAL-1", "renewable": true,
  "lastScan": "2026-11-12T15:02:00.0000000+00:00", "files": 150, "tracks": 150, "missing": 0, "problem": null }
```

- Es revisen cada 2 minuts, en arrencar el motor (als 10 s) i amb `folders.scan`; no mentre s'importa una carpeta des de la pantalla.
- **Fitxer nou**, quan fa més de 30 s que no canvia i es pot obrir: s'importa amb `type`, `tags` i `variableDuration` (o es recupera una pista de la subcategoria que falta amb el mateix nom de fitxer). **Fitxer que torna**: es recupera. **Fitxer substituït** (mida o data): es torna a llegir la durada i es recalculen els punts (excepte els manuals).
- **Fitxer que desapareix** (en dues revisions seguides): amb `renewable`, la pista s'esborra (excepte si la fa servir un esdeveniment, que queda com a «el fitxer falta»); sense, queda com a «el fitxer falta».
- `problem`: si la carpeta no es troba, no s'hi pot accedir o **surt buida** amb pistes que hi eren, es diu aquí i **mai no s'esborra cap pista**. A la segona revisió seguida, les seves pistes queden com a «el fitxer falta» (l'AutoDJ no les tria i no es poden posar a la cua); quan la carpeta torna, es recuperen totes. `missing`: pistes de la carpeta amb el fitxer que falta.
- Una pista d'aquesta carpeta que és d'una altra subcategoria no es toca.

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `version`, `uptime` (s), `clients` | cada segon | fase 1, pas 6 |
| `queue.changed` | `queue` (4.2) | quan s'afegeix, es treu, comença o s'acaba una pista | fase 1, pas 7c |
| `player.trackStarted` | `item` (element de la cua), `time` (UTC; quan el motor la comença a mesclar), `heardAt` (hora local amb zona en què surt per la sortida, comptant-hi la latència; `null` si no hi ha sortida) | quan una pista comença a sonar | fase 1, pas 7c; `heardAt`, fase 2, pas 4b |
| `player.trackEnded` | `item`, `playedMs`, `complete` (ha arribat al final previst) | quan una pista s'acaba | fase 1, pas 7c |
| `player.trackFailed` | `item`, `message` | quan una pista no es pot fer sonar (fitxer que falta o il·legible) i se salta | fase 1, pas 7c |
| `aux.started` | `player`, `item`, `time`, `heardAt` (com a `player.trackStarted`), `aux` (4.8) | quan un auxiliar comença a sonar | fase 2, pas 5a |
| `aux.ended` | `player`, `item`, `playedMs`, `complete` (fals si s'ha aturat o l'ha substituït un altre), `aux` (4.8) | quan un auxiliar s'acaba | fase 2, pas 5a |
| `aux.failed` | `player`, `item`, `message` | quan el fitxer d'un auxiliar no es pot fer sonar (la música continua) | fase 2, pas 5a |
| `aux.loaded` | `player` (el que ha canviat), `loaded` (4.8) | quan es carrega o es treu la pista d'un reproductor | fase 3, pas 4a |
| `notes.changed` | `text` | quan algú desa les notes | fase 3, pas 4a |
| `bags.changed` | — | quan es crea, es reanomena, s'esborra o canvia de subcategoria una bossa (també amb l'acció `BOSSA` d'un esdeveniment); va acompanyat de `rotations.changed` | fase 3, pas 8c |
| `rotations.changed` | — | quan es desa o s'esborra una rotació (les interfícies tornen a demanar `rotations.list`) | fase 3, pas 8a |
| `folders.changed` | `folders` (4.11) | després de cada revisió de les carpetes vigilades (si ha canviat alguna pista, també `library.changed`) | fase 3, pas 7c |
| `library.importProgress` | `import` (4.10) | unes 2 vegades per segon mentre s'importa, i una última en acabar | fase 3, pas 7b |
| `library.changed` | `trackId` (la pista editada, canviada de fitxer o esborrada), o `null` si han canviat les categories o s'ha acabat una importació | quan es canvia la biblioteca des d'una ordre (les interfícies tornen a demanar el que mostren) | fase 3, pas 7a |
| `processor.changed` | `processor` (4.9) | quan canvia el preajust o el volum del processador de la sortida | fase 3, pas 6 |
| `output.changed` | `output` (4.3) | quan canvia la sortida, l'estat d'emergència o el problema | fase 1, pas 7c |
| `autodj.changed` | `autodj` (4.5) | quan s'activa o es desactiva, canvia la rotació o canvia l'avís (les pistes que afegeix surten a `queue.changed`) | fase 1, pas 8 |
| `meters` | `output` (nivells de la sortida), `reductionDb` (quant abaixa el processador), `position` (la pista que sona); vegeu 5.1 | unes 20 vegades per segon, només si hi ha algun client connectat | fase 1, pas 10a |
| `scheduler.fired` | `entry` (línia del registre, 4.7) | quan el planificador executa o omet una emissió, i quan comença la primera pista d'un TALLA o l'auxiliar d'un SUPERPOSA (la mateixa línia, completada) | fase 2, passos 3 a 5 |
| `scheduler.paused` | `paused` (com a `scheduler.status`) | quan s'aturen o es reactiven els esdeveniments (també quan tornen a funcionar sols) | fase 3, pas 2c |
| `scheduler.changed` | — | quan es desa o s'esborra un esdeveniment (la interfície torna a demanar el que mostra) | fase 2, pas 3 |

### 5.1. Mesuradors i posició

```json
{ "v": 1, "ev": "meters", "output": [-12.3, -11.8], "reductionDb": 2.4,
  "position": { "itemId": "6f0c…", "elapsedMs": 61500, "remainingMs": 120000, "mixInMs": 110000, "durationMs": 181500 } }
```

- `output`: `[esquerre, dret]` en **dBFS**, el pic més alt de la sortida des de l'esdeveniment anterior (uns 50 ms). `-90` vol dir silenci. És la mescla del motor, abans d'adaptar-la a la targeta. La interfície fa la caiguda suau, la retenció del pic i l'indicador de saturació. Des de la fase 3, pas 6, és el so després del processador de la sortida (4.9).
- `reductionDb` (fase 3, pas 6): l'abaixada més gran del processador (compressor i sostre) des de l'esdeveniment anterior, en dB (≥ 0, amb un decimal). `0` = no comprimeix.
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
