-- Migració 001: esquema inicial (fase 1, pas 4).
-- Biblioteca de pistes, classificació (D-023, D-026, D-027), historial i configuració.
-- Les hores es desen en UTC i en format ISO 8601 ('2026-11-09T14:00:00.000Z'): així l'ordre és correcte
-- també quan canvia l'horari d'estiu. Les dates de calendari (sense hora) van en format 'YYYY-MM-DD'.

-- Categories i subcategories: organització de la biblioteca i base de les rotacions
CREATE TABLE categoria (
  id     INTEGER PRIMARY KEY,
  nom    TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE subcategoria (
  id            INTEGER PRIMARY KEY,
  categoria_id  INTEGER NOT NULL REFERENCES categoria(id),
  nom           TEXT NOT NULL COLLATE NOCASE,
  carpeta       TEXT,                        -- carpeta vigilada (D-026); null = cap
  renovable     INTEGER NOT NULL DEFAULT 0,  -- 1 = bossa renovable (D-033)
  UNIQUE (categoria_id, nom)
);

-- Pistes. La ruta identifica el fitxer; els fitxers no es copien mai.
CREATE TABLE pista (
  id                  INTEGER PRIMARY KEY,
  ruta                TEXT NOT NULL UNIQUE COLLATE NOCASE, -- Windows no distingeix majúscules
  tipus               TEXT NOT NULL DEFAULT 'MUSICA'
                      CHECK (tipus IN ('MUSICA','SWEEPER','IDENTIFICACIO','PROMO','FALCA','PROGRAMA',
                                       'INFORMATIU','LOCUCIO_HORARIA','SENYAL_HORARI','STREAM','ALTRES')),
  durada_variable     INTEGER NOT NULL DEFAULT 0,          -- 1 = el fitxer es substitueix (D-012, D-023)
  subcategoria_id     INTEGER REFERENCES subcategoria(id),
  artista             TEXT NOT NULL DEFAULT '',
  titol               TEXT NOT NULL DEFAULT '',
  album               TEXT NOT NULL DEFAULT '',
  any_edicio          TEXT NOT NULL DEFAULT '',
  durada_ms           INTEGER,                             -- null = encara no se sap
  -- Punts d'encadenament (D-006), en mil·lisegons des del principi del fitxer; null = encara no s'han calculat
  inici_ms            INTEGER,
  mescla_ms           INTEGER,
  final_ms            INTEGER,
  punts_origen        TEXT CHECK (punts_origen IN ('RADIODJ','ANALITZADOR','MANUAL')),
  -- Vigència i ús
  data_inici          TEXT,                                -- 'YYYY-MM-DD'; null = sense límit
  data_fi             TEXT,
  activa              INTEGER NOT NULL DEFAULT 1,
  estat_fitxer        TEXT NOT NULL DEFAULT 'OK' CHECK (estat_fitxer IN ('OK','FALTA','RETIRADA')),
  prioritat           REAL NOT NULL DEFAULT 50,
  reproduccions       INTEGER NOT NULL DEFAULT 0,
  darrera_reproduccio TEXT,                                -- UTC
  -- Per detectar si el fitxer ha canviat (D-012)
  fitxer_mida         INTEGER,
  fitxer_modificat    TEXT,                                -- UTC
  radiodj_id          INTEGER,                             -- id a RadioDJ, si s'ha importat d'allà
  notes               TEXT,
  creada              TEXT NOT NULL,                       -- UTC
  modificada          TEXT NOT NULL                        -- UTC
);
CREATE INDEX pista_subcategoria ON pista(subcategoria_id);
CREATE INDEX pista_artista ON pista(artista COLLATE NOCASE);
CREATE INDEX pista_titol ON pista(titol COLLATE NOCASE);
CREATE UNIQUE INDEX pista_radiodj ON pista(radiodj_id) WHERE radiodj_id IS NOT NULL;

-- Etiquetes lliures (D-027)
CREATE TABLE etiqueta (
  id   INTEGER PRIMARY KEY,
  nom  TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE pista_etiqueta (
  pista_id     INTEGER NOT NULL REFERENCES pista(id) ON DELETE CASCADE,
  etiqueta_id  INTEGER NOT NULL REFERENCES etiqueta(id) ON DELETE CASCADE,
  PRIMARY KEY (pista_id, etiqueta_id)
);

-- Historial de reproducció (sense límit de 30 dies: D-028). Es copien l'artista i el títol del moment,
-- perquè el certificat d'emissió digui el que va sonar encara que la fitxa canviï després.
CREATE TABLE historial (
  id         INTEGER PRIMARY KEY,
  pista_id   INTEGER REFERENCES pista(id) ON DELETE SET NULL,
  inici      TEXT NOT NULL,                  -- UTC, hora real en què ha començat a sonar
  durada_ms  INTEGER,                        -- quant ha sonat (null = encara sona)
  sencera    INTEGER,                        -- 1 = ha sonat fins al final previst
  origen     TEXT NOT NULL CHECK (origen IN ('AUTODJ','MANUAL','ESDEVENIMENT')), -- D-034
  artista    TEXT NOT NULL,
  titol      TEXT NOT NULL,
  ruta       TEXT NOT NULL
);
CREATE INDEX historial_inici ON historial(inici);
CREATE INDEX historial_pista ON historial(pista_id, inici);

-- Configuració: una fila per clau, amb el valor en JSON
CREATE TABLE configuracio (
  clau   TEXT PRIMARY KEY,
  valor  TEXT NOT NULL
);
