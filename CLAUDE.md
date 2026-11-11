# Ona Bages 24h — guia per a Claude Code

Aplicació d'escriptori per a Windows per a l'**emissió continuada les 24 hores** d'una ràdio local. Funciona a l'ordinador de continuïtat (PC 24h) i substitueix RadioDJ i el Programador del RadioDJ: AutoDJ amb rotacions, encadenament de pistes amb solapament, planificador d'esdeveniments a l'hora exacta, campanyes de falques amb certificat d'emissió i supervisió desatesa.

És el projecte germà d'**Ona Bages Play** (programes en directe), amb la mateixa arquitectura i les mateixes maneres de treballar.

**Prioritat absoluta:** l'emissió no es pot aturar mai, i els esdeveniments han de sonar a l'hora.

---

## Fase actual

**Fase 2 — Planificador i puntualitat** (vegeu `docs/pla-de-fases.md`). La fase 0 es va tancar a D-020.
La fase 1 està feta excepte el pas 11 (prova de 48 hores), posposat; la fase 1 no es tanca fins que es faci (D-056).
Els passos de cada fase són a `docs/pla-de-fases.md`; els fets estan marcats amb ✔.
Quan una fase s'acabi, actualitza aquesta línia i afegeix una entrada a `docs/decisions.md`.

## Com treballem

- **Idioma:** respon i escriu la documentació en català. Els identificadors del codi van en anglès; els comentaris i tots els textos de la interfície, en català.
- **El desenvolupador:** treballa sol i té poca experiència amb C#; se sent més còmode amb JavaScript pur i Bootstrap. Per tant:
  - prefereix codi llegible i directe a solucions enginyoses;
  - evita patrons i abstraccions que no calguin ara;
  - quan facis un canvi, explica breument què has fet i per què, en llenguatge planer.
- **Passos petits:** cada canvi ha de compilar i ha d'acabar amb una manera concreta de provar-lo. Per a canvis grans, proposa primer un pla breu.
- **Git:** després de cada pas que funcioni, proposa un missatge de commit en català.
- **Maquinari real:** no pots escoltar àudio, connectar dispositius ni veure la finestra. Quan una tasca ho necessiti, digues exactament què ha de fer el desenvolupador i què ha de comprovar. Sempre que es pugui, crea proves automàtiques (to de prova i anàlisi de talls, mesura de l'hora real d'inici d'un esdeveniment, etc.).
- **Abast:** implementa només el que demana la fase actual. Si veus una millora per a més endavant, anota-la; no la facis.
- **Dependències:** no n'afegeixis cap sense justificar-ho. La llicència ha de permetre la distribució gratuïta (MIT, BSD, Apache; LGPL només enllaçada dinàmicament). Cada dependència nova s'anota a `docs/decisions.md`.
- **Dades reals:** el backup complet de RadioDJ i els fitxers d'àudio no es pugen mai al repositori.

## Arquitectura (no es canvia sense una decisió explícita)

```text
Electron (app/) ── WebSocket 127.0.0.1 + testimoni ── Motor .NET (engine/)
  HTML + Bootstrap + JS pur                            NAudio (WASAPI) · mesclador · planificador · SQLite
```

- **Motor** (`engine/`, C# / .NET 10): procés independent. Té l'àudio, la cua, l'AutoDJ, el **planificador**, la base de dades i tot l'estat. És la **font de veritat**. Si la interfície es tanca o es penja, l'emissió i la programació continuen.
- **Interfície** (`app/`, Electron): només mostra l'estat i envia ordres. **Mai no reprodueix ni processa àudio, ni accedeix a la base de dades.**
- **Àudio:** `OnaBages.Audio` és una còpia del d'Ona Bages Play, feta una sola vegada (D-019). Aquí és **propi**: es canvia lliurement i no s'ha de mantenir el comportament de Play. Mai no es modifica el repositori de Play des d'aquí, amb una sola excepció: si es troba un error que també pot afectar Play, s'anota a `docs/pendent-detectat-a-ona-bages-24h.md` del repositori de Play (una secció per problema: on, què passa, correcció feta aquí i com provar-ho) i es diu al desenvolupador. No s'hi toca cap altre fitxer.
- **Vigilant:** un procés separat comprova que el motor respon i el reinicia si cal (fase 5).

## Regles del motor

Les d'Ona Bages Play continuen vigents:
- El **fil d'àudio** no pot crear objectes a cada crida, bloquejar, esperar disc o xarxa, ni escriure registres. La lectura de fitxers i de streams va en fils separats, amb memòria intermèdia.
- Un error en un flux **no pot afectar els altres** ni fer caure el procés: es converteix en un estat d'error i un esdeveniment.
- Dispositius en mode compartit de WASAPI, identificats per id de Windows i, com a alternativa, pel nom. Mai el «dispositiu predeterminat» per a la sortida d'emissió. Si la sortida configurada no hi és, s'emet per una **sortida d'emergència** (la primera analògica activa) amb avís, mai en silenci (D-038; `docs/disseny-motor.md`, secció 6).

Específiques d'Ona Bages 24h:
- **Una sola sortida** amb mesclador intern (D-005). El processador d'àudio va a la sortida.
- **Puntualitat** (D-008): el planificador prepara cada esdeveniment uns 30 s abans i el dispara amb el rellotge del motor. Mai no es fa servir un temporitzador de la interfície.
- **Esdeveniments atòmics:** una acció que canvia la cua s'aplica sencera, sense que l'AutoDJ hi pugui afegir res a mig fer.
- **Fitxers:** s'obren sense bloquejar-los; si un fitxer canvia o falta, es fa servir l'última versió bona o una pista de reserva, i es genera un avís. **Mai silenci.**
- **Dates i hores:** les recurrències es calculen amb dates de calendari (`DateOnly`), no sumant hores, perquè els canvis d'horari d'estiu no facin saltar un dia.

## Regles de la interfície

- HTML + Bootstrap 5 (tema fosc) + JavaScript pur amb mòduls ES. **Sense React, sense TypeScript, sense empaquetadors.** Es pot fer servir JSDoc i `// @ts-check`.
- Seguretat d'Electron: `contextIsolation: true`, `nodeIntegration: false`. L'accés al sistema passa només per `preload.js`.
- Els estats i els modes es mostren amb **color + icona + text**, mai només amb color. Colors dels modes: vegeu `docs/maquetes/`.
- Les accions que interrompen l'emissió (aturar, tallar la cua, esborrar un esdeveniment) demanen confirmació.
- Ha de funcionar bé per escriptori remot: res que depengui de l'acceleració gràfica.

## Protocol

- Mateix format que Ona Bages Play: missatges JSON amb `"v": 1`; ordres amb `id` i resposta `ok`/`error`; esdeveniments amb `ev`.
- Es crearà `docs/protocol.md` a la fase 1, a partir del d'Ona Bages Play i de `docs/planificador.md` (secció 5). **Qualsevol canvi a les ordres o als esdeveniments s'ha d'anotar allà en el mateix canvi.**

## Dades

- **SQLite** (D-004). Tot l'accés passa per **un sol mòdul del motor** (repositori). Cap altre codi llegeix o escriu la base de dades ni els fitxers de configuració.
- Cada canvi d'esquema porta una **migració** numerada.
- Els fitxers d'àudio no es copien mai: només se'n guarden les rutes.
- L'historial de falques es conserva com a mínim 2 anys (D-011).
- **Aplicació portable, sense instal·lador** (D-044): totes les dades (base de dades, còpies, registres) van a la carpeta `dades\` de l'aplicació, marcada amb `LLEGEIX-ME.txt`. En desenvolupament és `dades/` del repositori, que no es puja a Git (excepte el `LLEGEIX-ME.txt`). No s'escriu res a `%APPDATA%` ni al registre de Windows.

## Estructura prevista

```text
engine/OnaBages24.Engine/   procés del motor: WebSocket, ordres, estat, planificador
engine/OnaBages24.Data/     base de dades SQLite, migracions, importació de RadioDJ
engine/OnaBages24.Tests/    proves automàtiques (xUnit)
engine/OnaBages.Audio/      àudio, copiat d'Ona Bages Play (D-019)
engine/OnaBages.ToneCheck/  eina de proves: to de prova i anàlisi de talls (copiada de Play)
app/main/                   procés principal d'Electron i preload
app/renderer/               interfície (HTML, CSS, JS)
tools/                      eines de desenvolupament i de prova
dades/                      dades de l'aplicació en desenvolupament (fora de Git, D-044)
docs/                       especificació, decisions, maquetes, dades de RadioDJ
```

Confirmada a la fase 1 (D-037); els noms dels projectes es poden canviar amb una decisió.

## Documents de referència

Llegeix la secció corresponent abans de començar cada tasca nova.

- `docs/pla-de-fases.md` — fases, contingut i criteris de tancament.
- `docs/decisions.md` — registre de decisions; afegeix-hi una línia per cada decisió nova.
- `docs/planificador.md` — esdeveniments, modes, accions, esquema de dades, API del motor, migració de RadioDJ, puntualitat, falques i campanyes, pistes de durada variable i recurrències.
- `docs/pantalla-principal-i-encadenament.md` — pantalla d'emissió, encadenament de pistes, configuració actual de RadioDJ, rotacions, informes i utilitats.
- `docs/context-operatiu.md` — ordinadors, recorregut del so, streams, supervisió i avisos, streaming amb BUTT, accés remot, bosses de música i Automatitzador.
- `docs/maquetes/` — maquetes de les pantalles Emissió i Planificador (s'obren al navegador).
- `docs/radiodj/` — esquema i dades de referència de RadioDJ per a la migració.
- `docs/referencia/preparar-motor-ona-bages-play.md` — encàrrec de la fase 0 (ja tancada; només com a història).
- Al repositori de Play: `docs/notes-ona-bages-24h.md` (revisió d'`OnaBages.Audio`, què es pot reaprofitar i prova de fitxers bloquejats) i `docs/protocol.md` (base del protocol d'aquí).

## Projectes relacionats

- **Ona Bages Play** (`adavins/onabages-play`): motor i interfície de directe, a `D:\apps\onabages-play` a l'ordinador del desenvolupador. D'aquí es copia `OnaBages.Audio` (D-019). Només es llegeix: no s'hi fan canvis des d'aquest projecte, excepte anotar a `docs/pendent-detectat-a-ona-bages-24h.md` els errors trobats aquí que també el poden afectar.
- **Programador del RadioDJ** (`adavins/programador`): graella setmanal i informes de falques que aquesta aplicació substitueix.
- **Automatitzador** (`adavins/automatitzador`): actualitza els fitxers dels programes. Continua funcionant; vegeu `docs/context-operatiu.md` (2.7) i `docs/planificador.md` (12).
