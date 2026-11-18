# Captures del manual

Eines per fer (i refer) les captures de `docs/manual-usuari.html` amb **dades de mostra**: una emissora «Ràdio de Mostra»,
pistes inventades amb noms neutres (àudios sintètics), dues bosses, una rotació, dues campanyes i uns quants
esdeveniments. Res de la música, els anunciants ni la programació de debò. Com les de Play.

- Fan servir una **carpeta de dades a part** (`ONABAGES_DATA_DIR`): la carpeta `dades` de sempre no es toca.
- Tot el que generen va a `%TEMP%\OnaBages24Captures` (àudios, dades de mostra i captures), fora del repositori.
- Els àudios són tons fluixos (uns −30 dB) i el volum de sortida es posa a −20 dB: no se sent gairebé res. Si es vol, es
  pot silenciar l'altaveu mentre es fan les captures.
- Sense dependències: només Node (el que ja hi ha) i l'Electron de `app\node_modules`.

## Passos

Amb **Ona Bages 24h tancat i el motor aturat** (el motor de mostra fa servir el mateix port, 47824), des d'aquesta carpeta:

```powershell
node genera-audios.mjs   # 1. àudios WAV sintètics (uns 130 MB, només la primera vegada)
node captures.mjs        # 2. obre l'aplicació amb les dades de mostra, la deixa sonar un minut i fa les captures (uns 2 min)
node incrusta.mjs        # 3. les posa dins del manual
```

Mentre treballa el pas 2 s'obre la finestra de l'aplicació: no l'has de tocar. En acabar, atura el motor de mostra i la
tanca sola.

Entre el pas 2 i el 3, **mira les captures** a `%TEMP%\OnaBages24Captures\captures`. Si alguna no està bé, no les
incrustis. El pas 2 diu si hi ha hagut errors a la consola de la interfície.

## Afegir una captura

1. A `captures.mjs`, obre la pantalla o el diàleg (fent clic als botons pel seu `id`) i fes `await shot('nom')`.
2. A `incrusta.mjs`, afegeix `nom: 'text alternatiu'`.
3. Al manual, posa-hi `<img src="data:image/jpeg;base64," alt="text alternatiu">` on hagi d'anar.
