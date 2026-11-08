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
| `STREAM` | `{url, durada_seg, fade_ms, reserva_songId?}` |

- **Durada a l'acció:** el motor talla la connexió als `durada_seg` amb un fade i continua amb la resta de l'esdeveniment. No cal programar una espera a part.
- **Connexió anticipada:** el motor connecta uns segons abans (a la preparació anticipada de l'esdeveniment), perquè el butlletí soni a l'hora i no després del temps que es triga a connectar i omplir la memòria intermèdia.
- **Si el stream falla** (no connecta, es talla o arriba en silenci): sona una **pista de reserva** (`reserva_songId`) o es continua amb la resta de l'esdeveniment, i s'envia un avís. RadioDJ ara no ho resol.
- Les dades del stream (nom, títol) es poden passar a l'exportació del títol per a BUTT.

### 2.3. Supervisió i avisos per correu
Ara hi ha un detector de silencis amb avís per correu, però no hi ha manera de detectar que RadioDJ s'ha encallat en una pista sense generar silenci. Hi ha un programa extern que ho intenta mirant MySQL, però no està activat.

Com que el motor sap en tot moment què sona i on és, la supervisió pot ser interna:
- **Pista encallada:** la posició no avança, o la pista ha durat més que la seva durada més un marge.
- **Motor penjat:** un vigilant extern (procés separat o servei de Windows) comprova cada pocs segons que el motor respon. Si no respon, el reinicia i avisa.
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
- Usuaris: n'hi ha prou amb dos rols, **administrador** i **programador**. Al principi tots dos poden fer-ho tot.

### 2.6. Bosses de música que s'alternen
L'administrador alterna periòdicament les bosses generalistes (Ona Bages Adult 1–9). Ara s'han d'editar les rotacions passa per passa per canviar quina subcategoria sona.

**Millora: bosses actives.** Els passos de la rotació apunten a una "bossa" amb nom (per exemple, *Generalista A* i *Generalista B*), i una taula diu a quina subcategoria correspon cada bossa ara mateix (A = Adult 5, B = Adult 9). Per canviar de bosses n'hi ha prou amb canviar aquesta taula, i les rotacions no es toquen. El canvi també es pot programar per data (per exemple, el dia 1 de cada mes).

### 2.7. Integració amb l'Automatitzador
- L'Automatitzador continua substituint fitxers. Es recomana que escrigui primer a un fitxer temporal i el reanomeni en acabar.
- **Opcional:** l'Automatitzador pot avisar el motor quan ha actualitzat un fitxer (una ordre de l'API, com ara `library.fileUpdated {path}`), i el motor en torna a analitzar els punts de seguida.
- Les incidències de l'Automatitzador i les del motor poden anar al mateix sistema d'avisos.

### 2.8. Windows 10
Windows 10 ja no rep actualitzacions de seguretat des d'octubre del 2025, tret que s'hagi contractat l'ampliació (ESU). Abans del canvi, convé comprovar que la versió de .NET que es faci servir hi és compatible i planificar el pas a Windows 11 als PC 24h.

### 2.9. Ordre de desenvolupament
Com que Ona Bages Play és prioritari, les peces que necessita Ona Bages 24h i que també són útils per a Play (solapament entre pistes, anàlisi de punts, biblioteca d'àudio compartida) es poden fer primer a Play. Ona Bages 24h comença quan Play estigui acabat i es prova en un ordinador de proves en paral·lel amb RadioDJ.
