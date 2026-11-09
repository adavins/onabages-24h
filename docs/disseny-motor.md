# Disseny del motor d'àudio d'Ona Bages 24h

*Notes de disseny de la fase 1, pas 2 (novembre del 2026). S'escriuen abans d'implementar el mesclador, la cua i l'inici a l'hora exacta, i s'actualitzen si la implementació obliga a canviar alguna cosa.*

Referències: D-005 (una sola sortida i mesclador intern), D-006 (encadenament), D-008 i D-021 (puntualitat), D-024 (solapament màxim), D-034 (origen dels elements de la cua) i `planificador.md`, secció 7.

---

## 1. Què es reaprofita d'Ona Bages Play i què és nou

Play té **un reproductor per sortida**: cada reproductor llegeix una pista rere l'altra en una sola memòria intermèdia, i l'encadenament amb fos es fa al fil de lectura (`CrossfadeTail`). Funciona bé per a fosos curts entre dues pistes, però a 24h cal més:
- solapaments llargs (fins al màxim de D-024) entre pistes amb punts de mescla diferents;
- senyals horaris, sweepers i streams que sonen **a sobre** de la música;
- una pista que comença en una **mostra exacta** (l'hora en punt), independentment de quan acabi l'anterior.

Per això el motor d'Ona Bages 24h es basa en un **mesclador amb veus**, i no en el `Player` de Play.

| Peça d'`OnaBages.Audio` | Ús a 24h |
|---|---|
| `WasapiAudioOutput`, `DeviceList`, `DeviceWatcher` | Es fan servir tal qual: sortida per id de dispositiu, mode compartit |
| `AudioFileSource`, `Mp3Seek`, `TrackInfo` | Obrir fitxers, començar en un punt exacte, durades |
| `SampleRingBuffer` | Memòria intermèdia de cada veu (un escriptor, un lector, sense bloquejos) |
| `PeakMeter` | Vúmetres de cada veu i de la sortida |
| `Dynamics`, `Leveler` | Processador d'àudio a la sortida (fase 3) |
| `ToneGenerator`, `ToneAnalyzer`, `ToneCheck` | Proves de talls |
| `StreamConnection`, `Streamer` | Base per als streams (fase 2) |
| `Player`, `PlayerStream`, `Playlist`, `CartWall`, `EditorPlayer`, `Recorder` | No es fan servir per a l'emissió. Es queden a la còpia; potser la preescolta en farà servir alguna |

## 2. Peces noves

```text
                         fil de control del motor (cua, AutoDJ, planificador)
                               │  ordres (sense bloquejar el fil d'àudio)
                               ▼
 fil de lectura ─► [Veu 1] ─┐
 fil de lectura ─► [Veu 2] ─┤
 fil de lectura ─► [Veu 3] ─┼─► Mesclador ─► (processador, fase 3) ─► WasapiAudioOutput
         …         [Veu N] ─┘        │
                                     └─ rellotge de mostres (quantes mostres han sortit)
```

- **Veu (`Voice`):** una font que sona dins del mesclador: una pista, un senyal horari, un stream… Té la seva memòria intermèdia, el seu guany, el seu fos i un **fotograma d'inici** i un **fotograma de final** programats. Les veus es creen totes en arrencar (p. ex. 8) i es reaprofiten: el fil d'àudio no crea mai objectes.
- **Lector (`VoiceReader`):** un fil per veu activa que obre el fitxer, comença al punt d'inici (`sta`, amb el salt exacte de `Mp3Seek`) i omple la memòria intermèdia. Les pistes curtes (menys de 3 minuts) es llegeixen senceres a memòria en preparar-les, perquè el fitxer quedi lliure de seguida (D-018).
- **Mesclador (`Mixer`):** és el que llegeix la sortida de Windows. A cada bloc suma les veus actives, aplica guanys i fosos, compta les mostres que han sortit i actualitza els vúmetres. **És l'únic codi que corre al fil d'àudio.**
- **Rellotge de mostres (`SampleClock`):** relaciona el nombre de mostres que han sortit amb l'hora real. Vegeu la secció 5.
- **Reproductor de la cua (`QueuePlayer`):** al fil de control. Decideix quina pista ve ara, la prepara amb temps i diu a la veu en quin fotograma ha de començar i acabar.

## 3. Fils i regles

| Fil | Què fa | Què no pot fer |
|---|---|---|
| **Àudio** (Windows, prioritat «Pro Audio») | `Mixer.Read`: sumar veus, fosos, comptar, vúmetres | Crear objectes, bloquejar, esperar, accedir al disc o a la xarxa, escriure registres |
| **Lectura** (un per veu activa) | Descodificar el fitxer i omplir la memòria intermèdia | Tocar l'estat de la cua |
| **Control** (un per al motor) | Cua, AutoDJ, planificador, preparació de pistes, ordres del WebSocket | Fer esperar el fil d'àudio |
| **Anàlisi** (`TrackAnalyzer`, prioritat baixa) | Punts de pista i durades en segon pla | Res que afecti l'emissió |

**Comunicació amb el fil d'àudio.** El fil de control no crida mai res que bloquegi el mesclador. Deixa les ordres en camps de la veu (`volatile` / `Interlocked`), com ja fa `PlayerStream` a Play: «comença al fotograma F», «comença un fos de N mostres al fotograma G», «para al fotograma H». El fil d'àudio les recull al bloc següent. Com que tot es programa en **fotogrames**, no importa si el fil de control arriba uns mil·lisegons tard: el so comença a la mostra exacta.

**Errors.** Un error en una veu (fitxer il·legible, stream caigut) la deixa en estat d'error i genera un esdeveniment; les altres veus continuen. Si la memòria intermèdia d'una veu es buida (el disc no ha anat prou ràpid), aquella veu dona silenci i es compta com a tall; no s'espera mai.

## 4. Encadenament de pistes

Per a cada pista, els punts (`TrackCuePoints`, pas 3; D-006): **inici** (`sta`), **mescla** (`xta`), **final** (`end`) i els fosos d'entrada i de sortida.

**Quan comença la pista següent.** Quan la pista A comença a sonar al fotograma `F_A`, el reproductor de la cua calcula on serà el seu punt de mescla:

```text
F_mescla = F_A + (xta_A − sta_A) × freqüència
F_final  = F_A + (end_A − sta_A) × freqüència
Solapament màxim (D-024): si F_final − F_mescla > màxim, F_mescla = F_final − màxim
```

La pista B es programa per començar a `F_mescla`, i la veu de la pista A s'atura a `F_final` (amb el seu fos de sortida si en té). Durant aquest tros, totes dues sonen a la vegada.

**Preparació amb temps.** Uns 20 segons abans de `F_mescla` (com fa ara RadioDJ), el reproductor de la cua obre la pista B en una veu lliure i en comença a omplir la memòria intermèdia. Si la pista B no es pot obrir (fitxer que falta o il·legible), se salta i es prepara la següent, amb un avís. Si la cua es queda buida, l'AutoDJ hi posa pistes. **Mai silenci:** si ni l'AutoDJ pot trobar res, sona una pista o carpeta de reserva de la configuració.

**Previsió de la cua.** L'hora prevista de cada pista surt d'aquest mateix càlcul, fet per endavant amb les durades i els punts. És el que es mostra a la pantalla i el que fa servir la previsió del pròxim esdeveniment.

**Origen de cada element (D-034).** Cada element de la cua porta `origen` = `AUTODJ`, `MANUAL` o `ESDEVENIMENT` (amb l'id), per a «Regenera la cua».

## 5. Rellotge i inici a l'hora exacta

El motor ha de fer sonar una pista a l'hora del rellotge (p. ex. 15:00:00,000), però el mesclador només sap comptar mostres. El **rellotge de mostres** fa de pont:
- A cada bloc, el mesclador apunta quantes mostres han sortit en total (`FramesRendered`). El fil de control, amb un `Stopwatch` d'alta resolució, guarda la parella *(mostres, hora)* de tant en tant.
- Per saber quin fotograma correspon a una hora: `F = F_ara + (hora − ara) × freqüència`, corregit amb la latència de la sortida (100 ms a `WasapiAudioOutput`), perquè el que importa és quan surt el so per l'altaveu, no quan el llegeix el mesclador.
- **Deriva:** el rellotge de la targeta i el de l'ordinador no van exactament igual. La relació es recalcula cada pocs segons; per a un esdeveniment preparat 30 s abans, l'error és de pocs mil·lisegons. L'hora del Windows es manté amb el servei d'hora de la xarxa.

**Esdeveniment TALLA (D-021):**
1. Uns 30 s abans, el planificador prepara la primera pista de l'esdeveniment en una veu lliure (memòria plena).
2. Calcula el fotograma de l'hora exacta i el passa a la veu: comença **a la mostra exacta**.
3. Al mateix fotograma, la veu que sonava comença el fos de sortida (`fade_ms`), que se solapa amb la primera pista. No s'avança ni s'escurça.
4. La resta de pistes de l'esdeveniment entren a la cua darrere, amb l'encadenament normal.

**Senyals horaris (SUPERPOSA):** una veu més que comença a l'hora en punt, a sobre del que soni; si l'esdeveniment ho demana, la música baixa (`ducking_db`) amb un fos curt.

**Mesura (prova de la fase 2):** el criteri és menys de 50 ms. Es mesurarà amb `ToneCheck`: s'enregistra la sortida i es compara l'inici del to amb l'hora prevista.

## 6. Sortida i dispositiu

- Una sola sortida WASAPI en mode compartit. Mai el «dispositiu predeterminat» de Windows: si l'usuari connecta uns auriculars i Windows canvia el predeterminat, l'emissió no ha de canviar de targeta.
- El mesclador treballa a la **freqüència del dispositiu** (la del mesclador de Windows, normalment 48.000 Hz) i en estèreo. Cada lector remostreja el seu fitxer a aquesta freqüència (`AudioFileSource` ja ho fa).

**Quina sortida s'obre (D-038).** Per ordre, la primera que es trobi:
1. La configurada, per **id de dispositiu**.
2. La configurada, pel **nom** (l'id canvia si es reinstal·la el controlador).
3. **Sortida d'emergència:** la primera sortida activa, amb preferència per les **analògiques** (sortida de línia, altaveus), després les digitals (S/PDIF) i, al final, HDMI. Windows indica el tipus de connector de cada sortida (*form factor*). Amb un avís clar a la pantalla i per correu: «S'emet per una sortida d'emergència: …». No es desa com a configurada.

Així el motor **sempre emet** si hi ha alguna sortida, també després de desinstal·lar una targeta o desendollar-ne una d'USB. (Com a referència, RadioDJ agafa la primera sortida que troba, però si la sortida desapareix mentre sona, es penja.)

**Si el dispositiu desapareix mentre sona:** el motor ho detecta (`Stopped` amb error o `DeviceWatcher`) i passa a la sortida d'emergència al cap de pocs segons. Quan la configurada torna, hi torna **de seguida** (amb un tall breu) i genera un avís.

**Canvi de targeta planificat.** Mentre s'emet per la sortida d'emergència, l'avís es manté fins que es resol: o bé torna la targeta configurada, o bé es desa una altra sortida a la configuració. Perquè sigui ràpid, l'avís té el botó **«Fes servir aquesta sortida»**, que desa la sortida d'emergència com a configurada (cal sessió iniciada, D-035). El rellotge de mostres es torna a ajustar, perquè durant el canvi no han sortit mostres.

**Instal·lació nova (D-038).** Si no hi ha cap configuració (primera vegada):
- el motor arrenca sol, tria la sortida com a la sortida d'emergència (la primera analògica) i comença a sonar amb una **rotació de mostra** de sons inclosos al paquet;
- la pantalla mostra «Mode de demostració» (color, icona i text) fins que es configura la sortida i s'importa la biblioteca;
- els sons de mostra han de tenir una llicència que en permeti la distribució (fets per nosaltres, sintètics o de domini públic). Es preparen a la fase 6, amb l'instal·lador.

## 7. Estat que sobreviu a un reinici

Si el motor es reinicia (error, vigilant de la fase 5, actualització), ha de reprendre l'emissió:
- La cua (pistes, origen, posició) es desa a la base de dades cada vegada que canvia.
- En arrencar, el motor torna a obrir la sortida i comença per la pista següent de la cua (no reprèn la pista a mitges). Si la cua és buida, l'AutoDJ arrenca sol.
- L'objectiu és que el silenci d'un reinici sigui de pocs segons.

## 8. Com es prova

Totes aquestes proves funcionen sense targeta de so, amb una sortida falsa (`IAudioOutput` de prova) que demana blocs al mesclador com faria Windows:

| Prova | Què comprova |
|---|---|
| Inici programat | Una veu programada al fotograma F comença exactament a la mostra F |
| Encadenament | La pista B comença a `F_mescla` i la A s'acaba a `F_final`, amb fitxers de to sintètics |
| Solapament màxim | Amb una pista de final molt llarg, el solapament no passa del màxim |
| Sense talls | Una hora de cua simulada sense cap mostra de silenci inesperat (amb `ToneAnalyzer`) |
| Memòria buida | Si un lector va lent, la veu dona silenci i compta el tall, però el mesclador no s'atura |
| Fitxer que falta | Se salta la pista i sona la següent, amb un avís |
| Rellotge | Amb un rellotge simulat, una hora es converteix al fotograma correcte, també amb deriva |
| Cap objecte nou | El mesclador no crea objectes en mesclar (comptant les assignacions de memòria durant la prova) |

Al maquinari real, el desenvolupador escolta encadenaments reals i, a la fase 1, es fa la prova de 48 hores amb `ToneCheck`.

## 9. Valors acordats (D-039)

- **Nombre de veus:** 8 (dues pistes que se solapen, una tercera preparada, senyal horari, sweeper, stream i marge). És un valor de configuració.
- **Mida de la memòria intermèdia per veu:** 5 s, com a Play (`Player.BufferSeconds`), amb les pistes curtes senceres a memòria.
- **Corba dels fosos:** constant en dB, com a Play. Es revisarà escoltant-ho.
- **Sweepers a sobre de la pista** (com RadioDJ amb intros llargues): fase 3. El mesclador ja ho permet; només cal la regla a la cua.
- **Atenuació (*ducking*):** mentre sona un sweeper (o un senyal horari amb `ducking_db`), la música que hi ha a sota **baixa de volum** perquè el sweeper s'escolti bé, i torna a pujar quan s'acaba. Quant baixa (p. ex. −8 dB) i la durada de la baixada i la pujada (p. ex. 300 ms) són valors de configuració. El mesclador ho aplica al fil d'àudio amb un guany per veu, programat en fotogrames com els fosos.
