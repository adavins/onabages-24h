# Ona Bages 24h — Context operatiu i requisits que se'n deriven

*Esborrany v0.1 · 4 de novembre de 2026 · A partir de les respostes de l'emissora*

## 1. Entorn

**Ordinadors i estudis**
- **PC 24h:** ordinador dedicat només a la continuïtat. Hi funcionen RadioDJ, l'Automatitzador i el Programador, i no s'hi fa res més.
- **PC de directe:** és un altre ordinador, amb Ona Bages Play, per als programes en directe i els enregistraments.
- **Taula de mescles:** AEQ Capitol IP 12. Té un canal dedicat al PC 24h, i per això és fàcil entrar en directe i tornar a la continuïtat.

**Recorregut del so**
- **Sortida del PC 24h** (minijack estèreo): va per cable analògic directe a la taula. No hi ha processador entre el PC i la taula; els processadors són a l'antena.
- **Entrada del PC 24h** (minijack estèreo): rep la sortida d'emissió de la taula (o d'un receptor de ràdio, o d'un mesclador Behringer Ultralink). És el senyal que BUTT envia a internet.

**Maquinari típic del PC 24h:** Intel Core i5-13500, placa ASUS B760, 32 GB de DDR5, Windows 10, targeta Sound Blaster Z, AE-5 o la integrada a la placa, i SAI dedicat.

**Persones:** l'administrador i el responsable de la ràdio. Hi accedeixen sobretot en remot (escriptori remot), sense VPN. Ara no hi ha permisos diferenciats.

**Material nou:** entra poca música. L'administrador gestiona la bossa d'actualitat i va alternant periòdicament les bosses de música generalista. Tot està organitzat en carpetes per categories. Els programes els gestiona l'Automatitzador, que els porta del núvol (Google Drive, Dropbox) o d'una carpeta compartida de la xarxa local.

**Transició:** no hi ha data límit; és prioritari acabar Ona Bages Play. Hi ha ordinadors de prova disponibles.

## 2. Requisits que se'n deriven

### 2.1. Una sola sortida i mesclador intern
Tot (música, falques, auxiliars, senyals horaris, streams) surt per **una sola sortida estèreo**. El mesclador intern del motor és imprescindible, i el processador d'àudio (compressor i volum) s'aplica a la sortida, com fa ara el plugin de RadioDJ.

### 2.2. Connexions a emissions externes (streams)
Ara: pistes amb una URL de streaming (per exemple, els butlletins de **La Xarxa** a l'hora en punt o a i mitja). Es tallen des de l'esdeveniment amb una espera (`Seconds To Wait|180`) i després continuen les accions següents.

A Ona Bages 24h, una acció pròpia:

| `tipus` | `params` |
|---|---|
| `STREAM` | `{url, durada_seg, fade_ms, silenci_seg?, reconnecta?, tornada?, reserva_songId?}` |

- **Durada a l'acció:** el motor talla la connexió als `durada_seg` amb un fade i continua amb la resta de l'esdeveniment. No cal programar una espera a part.
- **Connexió anticipada:** el motor connecta uns segons abans (a la preparació anticipada de l'esdeveniment), perquè el butlletí soni a l'hora i no després del temps que es triga a connectar i omplir la memòria intermèdia.
- **Si el stream està caigut** (no connecta a l'hora, D-030): se salta i sona la **pista següent** de la cua; si no n'hi ha, l'AutoDJ continua amb la rotació. **No cal pista de reserva**: és opcional (`reserva_songId`), per si algun cas concret la necessita. Es genera un avís.
- **Si es talla mentre sona:** el motor prova de reconnectar durant uns segons (configurable, 5 s per defecte, amb la memòria intermèdia que tingui). Si no ho aconsegueix, passa a la pista següent amb un fos i genera un avís.
- **Silenci (opcional, només en alguns streams, D-030):** si el stream té `silenci_seg` (p. ex. 8 s), quan el so es manté per sota d'un llindar (−50 dB per defecte, configurable) durant aquest temps, el motor dona el stream per acabat i passa a la pista següent. Serveix per als butlletins que acaben abans de la durada prevista o que arriben en silenci. No s'activa per defecte, perquè hi ha emissions amb pauses llargues. El nivell es mesura al fil que llegeix el stream, no al fil d'àudio.
- **Reconnexió durant tota la durada (opcional, D-031):** per als programes llargs que es reben per streaming (p. ex. «La Central», de dilluns a divendres de 9 a 10). Si el stream té `reconnecta = 1`:
  - Quan cau (o no connecta a l'hora), sona **música de la rotació** per omplir. El contingut de l'esdeveniment que va darrere del stream **no s'avança**: es reserva per a quan s'acabi la durada prevista.
  - Mentrestant, el motor **prova de reconnectar en segon pla** cada pocs segons (5 s per defecte), tantes vegades com calgui, fins a l'hora prevista de final (`inici + durada_seg`).
  - **No el fa sonar fins que comprova que funciona:** connectat, rebent àudio de manera continuada durant uns segons (5 s per defecte) i, si té `silenci_seg`, sense silenci.
  - **Tornada:** quan el stream es dona per bo, la música de farciment s'acaba amb un fos i el stream torna a sonar en directe. Opció per stream: tornar **de seguida** (per defecte, perquè és un directe i cada minut compta) o **en acabar la cançó** que sona.
  - Si torna a caure, es repeteix el cicle. A l'hora prevista de final, el stream s'acaba (estigui on estigui) i continua la resta de l'esdeveniment.
  - **Avisos i registre:** un avís quan cau i un altre quan es recupera, i el registre d'execució anota cada tall amb la seva durada (p. ex. «La Central: 2 talls, 7 min sense senyal»).
  - Sense aquesta opció, val el comportament anterior: si cau, se salta a la pista següent (adequat per als butlletins curts).
- **Streams a la cua i a les rotacions:** les mateixes regles valen per a una pista de tipus Stream posada a mà a la cua o per l'AutoDJ. La fitxa de la pista té la durada màxima i l'opció de silenci; l'acció `STREAM` pot sobreescriure-les.
- RadioDJ ara no resol cap d'aquests casos.
- Les dades del stream (nom, títol) es poden passar a l'exportació del títol per a BUTT.

### 2.3. Supervisió i avisos per correu
Ara hi ha un detector de silencis amb avís per correu, però no hi ha manera de detectar que RadioDJ s'ha encallat en una pista sense generar silenci. Hi ha un programa extern que ho intenta mirant MySQL, però no està activat.

Com que el motor sap en tot moment què sona i on és, la supervisió pot ser interna:
- **Pista encallada:** la posició no avança, o la pista ha durat més que la seva durada més un marge.
- **Motor penjat:** un vigilant extern comprova cada pocs segons que el motor respon (`/health`, que només diu «ok» si el mesclador funciona). Si no respon, el reinicia. **A l'emissora ja hi ha l'Inicialitzador** (`D:\apps\Inicialitzador`, VB.NET): arrenca tots els programes amb Windows i torna a obrir els que es tanquen, però només mira si el procés existeix, cada minut. Mentrestant arrencarà el motor com un programa més. Quan Ona Bages 24h estigui acabat es decidirà si s'hi afegeix la comprovació de `/health` amb un interval curt o si es reescriu (D-045).
- **Qui avisa:** Ona Bages 24h gestiona les seves incidències i envia ell mateix els avisos per correu (D-045), sense dependre de l'Inicialitzador.
- **Silenci a la sortida:** el motor mesura el nivell que envia a la taula.
- **Silenci a l'entrada** (opcional): el motor pot mesurar també l'entrada de línia, que és la sortida d'emissió de la taula. Detectaria silencis encara que vinguin del directe, i podria substituir el detector actual.
- **Altres avisos:** esdeveniment omès o fet tard, stream que no connecta, fitxer que falta o il·legible, programa no actualitzat (secció 11 del document del planificador), rellotge desajustat i reinici del motor.
- **Enviament:** correu per SMTP configurable, amb un resum agrupat per no rebre'n massa, i un correu de prova des de la configuració. Més endavant es podria afegir Telegram.

### 2.4. Streaming a internet
Es continua amb BUTT. BUTT capta l'**entrada de línia** (la sortida de la taula), no la sortida del PC 24h, perquè el que s'emet per internet ha d'incloure els directes.

Si més endavant es vol que l'aplicació substitueixi BUTT, el codi ja hi és a Ona Bages Play: l'enregistrador capta una entrada amb WASAPI i la codifica a MP3 amb LAME. Caldria afegir-hi l'enviament a Icecast/Shoutcast. Per tant, el codificador intern s'ha de connectar a l'**entrada**, no a la mescla del motor.

### 2.5. Accés remot
- Ara s'hi accedeix amb escriptori remot. La interfície ha de funcionar bé així: res que depengui de l'acceleració gràfica i res que s'aturi si la sessió remota es tanca o es minimitza. El motor és un procés separat i no depèn de la finestra.
- L'edició web des de la xarxa local (secció 8 del document del planificador) necessitarà usuari i contrasenya. Per fer-ho des de fora de l'emissora caldrà una VPN; no s'ha d'obrir mai el port a internet.
- **Usuaris i rols (D-035):**
  - **Administrador:** ho pot fer tot, inclosos la configuració, els usuaris i la restauració de còpies.
  - **Programador:** esdeveniments, campanyes, biblioteca i rotacions.
  - **Operador:** pantalla d'emissió i informes, sense canviar la programació.
- **Inici sense contrasenya:** l'aplicació s'obre sempre sola i comença o reprèn l'emissió, sense demanar res. Mostra sempre la pantalla principal amb la cua, però **en mode de consulta**: per operar-hi (cua, esdeveniments, configuració…) cal iniciar sessió. La sessió es tanca sola després d'un temps sense activitat (configurable). L'emissió no depèn mai de la sessió.
- **Registre de canvis:** qui ha canviat què i quan (esdeveniments, campanyes, rotacions, configuració).

### 2.6. Bosses de música que s'alternen
L'administrador alterna periòdicament les bosses generalistes (Ona Bages Adult 1–9). Ara s'han d'editar les rotacions passa per passa per canviar quina subcategoria sona.

**Millora: bosses actives.** Els passos de la rotació apunten a una "bossa" amb nom (per exemple, *Generalista A* i *Generalista B*), i una taula diu a quina subcategoria correspon cada bossa ara mateix (A = Adult 5, B = Adult 9). Per canviar de bosses n'hi ha prou amb canviar aquesta taula, i les rotacions no es toquen. El canvi també es pot programar per data (per exemple, el dia 1 de cada mes).

**Bosses que es renoven (D-033).** Exemple: la bossa d'actualitat es renova cada cert temps; hi entren cançons i n'hi surten. Ara, a RadioDJ, es fa amb dues bosses (l'actual i la pròxima) i esborrant totes les pistes de la categoria abans d'importar la nova. A Ona Bages 24h, una subcategoria amb carpeta vigilada (D-026) pot ser **bossa renovable**:
- **Canviant els fitxers de la carpeta:** les pistes noves s'importen soles. Les que surten queden **retirades**, sense avís (no és un error): l'AutoDJ no les tria i la fitxa es conserva per a l'historial. Només avisa si una pista retirada era dins d'un esdeveniment o d'una campanya. Si el fitxer torna, la pista es reactiva amb la mateixa fitxa.
- **Assistent «Renova la bossa»:** es tria una carpeta nova (preparada amb temps); mostra quines pistes entren, quines surten i quines es mantenen, i ho aplica tot de cop quan es confirma.

Totes dues maneres conviuen.

### 2.7. Integració amb l'Automatitzador
- L'Automatitzador continua substituint fitxers, sempre abans de l'hora d'emissió, quan el motor encara no els té oberts (D-018).
- **Opcional:** l'Automatitzador pot avisar el motor quan ha actualitzat un fitxer (una ordre de l'API, com ara `library.fileUpdated {path}`), i el motor en torna a analitzar els punts de seguida.
- Les incidències de l'Automatitzador i les del motor poden anar al mateix sistema d'avisos.
- **Més endavant (fase 7, D-036):** comunicació entre els dos programes, sense ajuntar-los. Un **identificador de programa** compartit, i Ona Bages 24h pot dir a l'Automatitzador les pròximes emissions de cada programa, marcades com a **estrena** o **repetició**, perquè programi l'hora d'execució. Les especificacions es detallaran quan toqui.

### 2.8. Windows 10
Windows 10 ja no rep actualitzacions de seguretat des d'octubre del 2025, tret que s'hagi contractat l'ampliació (ESU). Abans del canvi, convé comprovar que la versió de .NET que es faci servir hi és compatible i planificar el pas a Windows 11 als PC 24h.

### 2.9. Ordre de desenvolupament
Ona Bages Play ja està acabat (D-017). Ona Bages 24h en copia el codi d'àudio i després n'és independent (D-019): el solapament entre pistes, l'anàlisi de punts i el mesclador es fan en aquest projecte. Es prova en un ordinador de proves en paral·lel amb RadioDJ.

### 2.10. Fonts externes (més endavant, fase 7, D-036)

**Entrades de línia.** Una o diverses entrades (analògiques o USB), identificades pel dispositiu de Windows. Normalment n'hi ha una, que alimenta el streaming, però alguna pot ser la font d'àudio d'un programa. Es tracten com els streams: acció `ENTRADA` programable als esdeveniments, amb dispositiu, durada, fosos, detecció de silenci i tornada quan hi ha senyal (D-030, D-031). La captura amb WASAPI ja existeix a Ona Bages Play. L'acció `ENTRADA` actual (silenciar l'entrada) es reconvertirà.

**Directes de YouTube (exemple: el ple de l'Ajuntament).** Ara es punxa a mà des del navegador. Dades: el ple ordinari és el segon dimarts de mes a les 19:00, excepte l'agost; sol començar uns minuts tard; al principi hi pot haver silencis; no té hora de final (l'Ajuntament talla el directe); hi pot haver incidències que l'impedeixin.

Proposta d'automatització:
- **Eines externes**, com a programes separats: **yt-dlp** (domini públic) troba el directe del canal i en dona l'adreça d'àudio; **ffmpeg** (LGPL) el converteix per al motor. Caldrà justificar-les a `decisions.md` quan s'implementi.
- **Esdeveniment** amb recurrència mensual (segon dimarts, excepte l'agost; secció 12 del planificador).
- **Plens extraordinaris** (una o dues vegades l'any, qualsevol dia i hora): un esdeveniment puntual (recurrència `DATES`) amb la mateixa acció. Per fer-ho ràpid, l'esdeveniment del ple ordinari tindrà l'opció **«Programa'n un d'extraordinari»**, que en fa una còpia per a una data i una hora concretes, amb la mateixa configuració (canal, espera, locucions).
- **Espera:** des d'una hora configurable abans (p. ex. 18:50) fins a un límit (p. ex. 20:00), el motor comprova cada minut si el canal emet en directe. Mentrestant sona la programació normal.
- **Quan comença:** es comprova que arriba àudio (D-031), pot sonar una locució d'entrada i després el ple, amb un fos.
- **Silencis:** en aquest tipus d'esdeveniment no s'acaba per silenci.
- **Tall o final:** quan es perd la connexió, el motor consulta YouTube. Si el vídeo encara és en directe, és un tall i es reconnecta (D-031). Si ja no ho és, el ple s'ha acabat: locució de sortida i torna l'AutoDJ.
- **Si no comença** abans del límit: s'anul·la, la programació continua i s'envia un avís. Avís previ a la tarda si el canal no té cap directe anunciat.
- **Filtre pel títol** (opcional): si el títol del directe no conté «Ple», es demana confirmació.
- **Botons manuals** «Connecta ara» i «Acaba ara», i una durada màxima de seguretat (p. ex. 4 h).
- **Risc:** quan YouTube canvia, yt-dlp deixa de funcionar fins que s'actualitza. Hi haurà un botó per actualitzar-lo, i la connexió manual sempre és l'alternativa.
