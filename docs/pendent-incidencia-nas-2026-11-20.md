# Incidència del 20-11-2026: el NAS es reinicia i hi ha 2 min 24 s de silenci

Nota escrita el 20 de novembre de 2026 des del projecte d'Ona Bages Play, a partir dels registres de producció (registres del motor del 19 i el 20 de novembre, base de dades sencera i registre del Synology, a `D:\apps\OB24h_produccio`). **No s'ha canviat res d'Ona Bages 24h.**

Quan se'n resolgui algun punt, anoteu-ho a `docs/decisions.md` i esborreu-lo d'aquesta llista (o el fitxer sencer quan estigui buit).

## Què va passar

Hores locals. Els rellotges del NAS i de l'ordinador coincideixen a pocs segons.

| Hora | NAS (registre del Synology) | Motor (`motor-2026-11-20.log`) |
|---|---|---|
| 03:25:15 | Descarrega DSM 7.3.2-86009 Update 5 | |
| 03:35:23 | En comença la instal·lació automàtica | |
| 03:37:38 | S'atura | |
| 03:38:09 | | «Anni B Sweet - Motorway» es talla a 0:22: error de lectura del disc (0x8003001E) |
| 03:38:09 → 03:40:33 | Torna a arrencar (03:39:43) | **4.819 pistes saltades** («No es troba el fitxer: O:\musica\…»), una cada 20 ms |
| 03:38:24 | | Incidència «Silenci a la sortida» |
| 03:40:33 | | Torna a sonar música («Manu Guix - A Cavall Del Vent»). Silenci resolt a les 03:40:34 |
| 03:42:48–03:42:59 | Atura i torna a engegar el servei SMB | |
| 03:43:11 | | «A Cavall Del Vent» es talla a 2:37 (0x8003001E); la següent entra 0,6 s després |

**A antena: 2 min 24 s de silenci** (de 03:38:09 a 03:40:33) i dues pistes tallades.

Altres efectes:
- **Incidències:** a la base de dades n'hi ha 4.819 de tipus `FITXER`, d'un total de 4.833. També n'hi ha 2 `ENCALLADA` i 1 `SILENCI`.
- **Correus:** se'n van enviar dos de «200 avisos» (03:40:11 i 03:50:16).
- **Historial:** les pistes saltades **no** hi consten, i l'historial de la nit està bé.

## Per què no hi va haver música d'emergència

Hi ha dues causes, i qualsevol de les dues sola ja hauria provocat el silenci.

1. **La carpeta d'emergència és buida.** El 19-11 a les 18:24:56 el motor ja avisava: «No hi ha cap pista d'emergència a D:\OnaBages24\dades\emergencia: si una pista tarda a obrir-se, hi haurà silenci». L'avís va sortir en un correu juntament amb un «Reinici inesperat», i va passar desapercebut.
2. **El mecanisme d'emergència no cobreix les fallades ràpides.** Les pistes d'emergència només entren quan la pista que toca *triga* a estar a punt: `QueuePlayer`, bloc de `EmergencyPicker`, amb `QueueSettings.EmergencyWait` = 2 s. Aquesta nit, Windows responia de seguida «no es troba el fitxer». La pista fallava en uns 20 ms, se saltava (`queue.Failed` → «Se salta») i l'AutoDJ en posava una altra, que també fallava. `_waitingSince` no arribava mai als 2 s. **Ni amb la carpeta plena no hauria sonat res.**

## Què queda per fer

### Configuració (sense codi; cal fer-ho a l'ordinador de l'emissora)

- **Synology:** a *Tauler de control → Actualització i restauració → Configuració d'actualització*, triar que només avisi (o que només descarregui) i **no** instal·li automàticament. Les actualitzacions s'han de fer a mà, en una hora sense risc i havent-ho previst.
- **Pistes d'emergència:** posar entre 20 i 30 cançons (una o dues hores) a `D:\OnaBages24\dades\emergencia`. Ha de ser un disc local, no el NAS. Ara, si la carpeta és buida, és una incidència greu.
- **Windows Update:** comprovar que no reiniciï l'ordinador sol. És el mateix tipus de problema.

Quan estigui fet, aquest fitxer ja es pot esborrar.

## Què s'ha fet al codi

- **D-196 — Pistes que fallen seguides.** A partir de la tercera fallada seguida, la pista d'emergència entra de seguida (abans calia esperar 2 s, que amb fallades instantànies no es complien mai), i les d'emergència s'encadenen mentre continuï fallant. Una sola incidència greu per a la sèrie, «No es poden fer sonar les pistes…», que es tanca amb el resum quan torna la música. La carpeta d'emergència buida és greu.
- **D-197 — Unitat de música no disponible.** Quan una pista falla o s'encalla (també per un error de lectura a mitja pista, com els 0x8003001E), el motor comprova l'arrel de la seva unitat (`O:\` o `\\nas\musica`) en un fil a part, amb un temps màxim de 3 s. Si no respon:
  - les pistes de la cua d'aquella unitat **no s'obren ni se salten**: s'hi esperen, i l'AutoDJ no en demana més (la cua ja és plena);
  - sonen pistes d'emergència, encadenades;
  - la unitat es torna a comprovar cada 5 s; quan torna, la pista següent de la cua entra **de seguida**, amb el fos de 2 s de sempre (decisió del desenvolupador);
  - una incidència greu per unitat, «La unitat O:\ no respon…», que es tanca amb quanta estona no ha respost;
  - les pistes dels **esdeveniments** no s'esperen: fallen com sempre i es continua amb música d'emergència (decisió del desenvolupador: un programa que sonés tard no és el que toca).

- **D-198 — Error de lectura a mitja pista.** Si la que sona s'acaba per un error de lectura (o s'encalla) i la següent no és a punt, la d'emergència entra en 0,5 s en lloc de 2. Ho va mostrar la prova del D-197: 2,1 s de silenci entre la pista tallada i la d'emergència.

Amb aquests canvis, la nit del 20-11 hauria sonat música d'emergència des de les 03:38:09 (abans d'un segon), n'haurien fallat unes poques pistes en lloc de 4.819, hi hauria hagut dues incidències greus en lloc de 4.833 i, quan el NAS va tornar, la música de la cua hauria entrat de seguida.

**Com provar-ho a l'ordinador de proves:** fer sonar música d'una carpeta compartida d'un altre ordinador, treure'n la compartició i esperar més que una cançó d'emergència (3 o 4 minuts). Comprovar que sonen les d'emergència, que surt «La unitat … no respon» i que la cua no es buida. Tornar a compartir-la i comprovar que la música de la cua entra de seguida i que la incidència es tanca amb la durada.

## Dades de referència

- Registres: `D:\apps\OB24h_produccio\motor-2026-11-19.log`, `motor-2026-11-20.log` i `SYNOLOGY_NAS_syslog_2026-11-20-10_26_23.csv`.
- Base de dades: `D:\apps\OB24h_produccio\onabages24.db`. Les hores de les taules són en UTC: les 03:38 locals són les `02:38Z`.
