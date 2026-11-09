# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 1, pas 6 (motor, estat i dispositius). Basat en el protocol d'Ona Bages Play (commit `2f1608e`), amb el mateix format.

Qualsevol canvi en aquest protocol s'ha d'anotar aquí **en el mateix commit** que el canvi de codi. Les ordres i els esdeveniments previstos per a les fases següents són a `docs/planificador.md` (secció 5).

## 1. Transport

- WebSocket a `ws://127.0.0.1:<port>/ws?token=<testimoni>`.
- El motor escolta **només** a `127.0.0.1`. Port per defecte: **`47824`** (Ona Bages Play fa servir el `47800`; tots dos poden funcionar alhora).
- El testimoni el genera qui arrenca el motor (`--token`) o, si no se n'hi passa cap, el mateix motor. Un testimoni incorrecte es rebutja amb HTTP `401`.
- El motor publica la informació de connexió al **fitxer d'execució** `<dades>\engine.json`, i l'esborra en aturar-se:

```json
{ "schemaVersion": 1, "pid": 1234, "port": 47824, "token": "…", "startedAt": "2026-11-09T15:51:20.0000000+00:00", "version": "0.1.0" }
```

- `<dades>` és `%ProgramData%\OnaBages24` (comuna a tots els usuaris de l'ordinador) o la carpeta indicada amb `--data-dir`. Hi ha també la base de dades (`onabages24.db`), les còpies de seguretat (`copies\`) i els registres (`logs\`).
- Comprovació de vida sense testimoni: `GET http://127.0.0.1:<port>/health` → `200 ok`.
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

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `version`, `uptime` (s), `clients` | cada segon | fase 1, pas 6 |

## 6. Eines

- `tools/prova-motor.mjs`: envia ordres al motor des de la línia d'ordres i en mostra les respostes (Node 22 o posterior, sense paquets). Llegeix el port i el testimoni de `engine.json`.

```text
node tools/prova-motor.mjs [--dades CARPETA] [ordre …]
```
