# Dades de referència de RadioDJ

Extretes del backup de la base de dades de RadioDJ 1.8.2 del 8 de gener de 2026. Serveixen per dissenyar i provar la **migració** cap a Ona Bages 24h.

| Fitxer | Contingut |
|---|---|
| `esquema.sql` | Estructura de les 15 taules (sense dades) |
| `dades-referencia.sql` | Dades de `events`, `events_categories`, `rotations`, `rotations_list`, `category`, `subcategory` i `genre` |

**No s'hi inclou** la biblioteca de pistes (`songs`, ~5.000 files) ni l'historial. Per provar la migració completa, demana el backup sencer al desenvolupador i desa'l **fora del repositori** (no s'ha de pujar a Git).

## Claus per interpretar les dades

- `events.type`: `1` = esdeveniment setmanal de la graella (336, un per franja de 30 minuts; 142 buits); `2` = amb màscara de dies i hores (senyals de les :30); `4` = el que arrenca el servidor d'streaming.
- `events.day`: `&1`…`&6` = dilluns–dissabte, `&0` = diumenge.
- `events.data`: ordres separades per `\r\n`, en format `Ordre|arguments`. Les pistes es carreguen amb `Load Track By ID|…|Top`, és a dir, al principi de la cua: **l'ordre escrit és l'invers de l'ordre en què sonen**.
- `songs.cue_times` (a la taula de pistes): `&sta=` inici, `&xta=` punt de mescla, `&end=` final, `&fin=` fade d'entrada, `&fou=` fade de sortida, en segons.
- `rotations_list.selType`: `0` = la menys reproduïda recentment, `5` = aleatòria.

La interpretació completa i els passos de migració són a `docs/planificador.md` (secció 6) i a `docs/pantalla-principal-i-encadenament.md`.
