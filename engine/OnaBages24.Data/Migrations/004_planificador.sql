-- Migració 004: planificador (fase 2, pas 1; docs/planificador.md, secció 3).
-- Un esdeveniment diu QUAN (hora, recurrència, repetició dins del dia) i COM (mode) es fa una llista ordenada
-- d'accions. Les accions són en l'ordre real d'emissió. Els tipus d'acció no tenen restricció CHECK (la llista
-- creixerà a cada fase): els valida el codi en desar.

CREATE TABLE categoria_esdeveniment (
  id     INTEGER PRIMARY KEY,
  nom    TEXT NOT NULL UNIQUE COLLATE NOCASE,
  color  TEXT                                     -- p. ex. '#5a2d14'; null = el del mode
);

CREATE TABLE esdeveniment (
  id              INTEGER PRIMARY KEY,
  nom             TEXT NOT NULL,
  mode            TEXT NOT NULL CHECK (mode IN ('TALLA','ENCUA','SUPERPOSA','SISTEMA')),
  hora_inici      TEXT NOT NULL,                  -- hora local 'HH:MM:SS'
  -- Quins dies (secció 12)
  recurrencia     TEXT NOT NULL DEFAULT 'SETMANAL'
                  CHECK (recurrencia IN ('SETMANAL','CADA_N_SETMANES','MENSUAL_ORDINAL','DIES_MES','DATES')),
  dies_mask       INTEGER NOT NULL DEFAULT 127,   -- bit0 = dilluns … bit6 = diumenge
  interval_setm   INTEGER,                        -- CADA_N_SETMANES: 2 = cada 15 dies
  data_ancora     TEXT,                           -- CADA_N_SETMANES: un dia en què segur que toca ('YYYY-MM-DD')
  ordinals        TEXT,                           -- MENSUAL_ORDINAL: '1,3' o 'last'
  dies_mes        TEXT,                           -- DIES_MES: '1,15,last'
  data_inici      TEXT,                           -- 'YYYY-MM-DD'; null = sense límit
  data_fi         TEXT,
  -- Repetició dins del dia
  repeticio       TEXT NOT NULL DEFAULT 'CAP' CHECK (repeticio IN ('CAP','CADA_HORA','CADA_N_MIN')),
  interval_min    INTEGER,                        -- CADA_N_MIN
  hores_mask      INTEGER,                        -- CADA_HORA: bit0 = 00 h … bit23 = 23 h; null = totes
  prioritat       INTEGER NOT NULL DEFAULT 50,
  tolerancia_seg  INTEGER NOT NULL DEFAULT 60,    -- si s'arriba tard més d'aquest temps, s'omet (D-057)
  fade_ms         INTEGER,
  marge_seg       INTEGER,
  ducking_db      REAL,
  durada_max      INTEGER,                        -- segons; durada màxima prevista del contingut (D-028)
  color           TEXT,
  categoria_id    INTEGER REFERENCES categoria_esdeveniment(id) ON DELETE SET NULL,
  neteja_cua      INTEGER NOT NULL DEFAULT 0,     -- 1 = esborra la cua abans d'inserir el contingut (D-022)
  bloc_falques    INTEGER NOT NULL DEFAULT 0,     -- 1 = bloc de falques (D-029, fase 4)
  es_plantilla    INTEGER NOT NULL DEFAULT 0,     -- 1 = no es dispara mai: es fa servir amb l'acció PLANTILLA
  actiu           INTEGER NOT NULL DEFAULT 1,
  notes           TEXT,
  modificat       TEXT NOT NULL                   -- instant UTC
);

-- Llista ordenada d'accions (en ordre real d'execució i emissió)
CREATE TABLE accio (
  id               INTEGER PRIMARY KEY,
  esdeveniment_id  INTEGER NOT NULL REFERENCES esdeveniment(id) ON DELETE CASCADE,
  ordre            INTEGER NOT NULL,              -- 0, 1, 2…
  tipus            TEXT NOT NULL,                 -- PISTA, ROTACIO, AUTODJ… (planificador.md, secció 4)
  params           TEXT NOT NULL DEFAULT '{}',    -- objecte JSON
  dies_mask        INTEGER                        -- null = tots els dies de l'esdeveniment; si no, només aquests (D-028)
);
CREATE INDEX accio_esdeveniment ON accio(esdeveniment_id, ordre);

-- Dates soltes (recurrencia = 'DATES') i excepcions (dates en què no s'emet)
CREATE TABLE esdeveniment_data (
  esdeveniment_id  INTEGER NOT NULL REFERENCES esdeveniment(id) ON DELETE CASCADE,
  data             TEXT NOT NULL,                 -- 'YYYY-MM-DD'
  tipus            TEXT NOT NULL CHECK (tipus IN ('INCLOU','EXCLOU')),
  PRIMARY KEY (esdeveniment_id, data)
);

-- Registre d'execució (auditoria i informes). No té clau forana: s'ha de conservar encara que l'esdeveniment s'esborri.
CREATE TABLE registre_execucio (
  id               INTEGER PRIMARY KEY,
  esdeveniment_id  INTEGER,
  nom              TEXT NOT NULL,                 -- nom de l'esdeveniment en aquell moment
  programat_per    TEXT NOT NULL,                 -- instant UTC previst
  executat_a       TEXT,                          -- instant UTC real (null si no s'ha executat)
  resultat         TEXT NOT NULL,                 -- OK | OMES_TARD | OMES_CONFLICTE | OMES_CANVI_HORA | NO_EMES | ERROR
  detall           TEXT
);
CREATE INDEX registre_execucio_programat ON registre_execucio(programat_per);
CREATE INDEX registre_execucio_esdeveniment ON registre_execucio(esdeveniment_id, programat_per);
