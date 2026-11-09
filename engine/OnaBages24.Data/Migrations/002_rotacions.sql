-- Migració 002: rotacions de l'AutoDJ (fase 1, pas 8; D-014, D-027).
-- Una rotació és un cicle ordenat de passos que l'AutoDJ va repetint. Cada pas tria una pista
-- d'una categoria o subcategoria (i, si cal, amb una etiqueta), com a RadioDJ.

CREATE TABLE rotacio (
  id          INTEGER PRIMARY KEY,
  nom         TEXT NOT NULL UNIQUE COLLATE NOCASE,
  radiodj_id  INTEGER                              -- id a RadioDJ, si s'ha importat d'allà
);

CREATE TABLE rotacio_pas (
  id               INTEGER PRIMARY KEY,
  rotacio_id       INTEGER NOT NULL REFERENCES rotacio(id) ON DELETE CASCADE,
  ordre            INTEGER NOT NULL,                -- 0, 1, 2… (ordre dins del cicle)
  categoria_id     INTEGER REFERENCES categoria(id),    -- si no hi ha subcategoria, qualsevol de la categoria
  subcategoria_id  INTEGER REFERENCES subcategoria(id),
  etiqueta_id      INTEGER REFERENCES etiqueta(id),     -- null = sense filtre d'etiqueta
  seleccio         TEXT NOT NULL DEFAULT 'MENYS_RECENT' CHECK (seleccio IN ('MENYS_RECENT','ALEATORIA')),
  repeticio        INTEGER NOT NULL DEFAULT 1,      -- 1 = s'apliquen els criteris de repetició (música)
  sweeper          INTEGER NOT NULL DEFAULT 0       -- 1 = sweeper a sobre de la pista (es farà servir a la fase 3)
);
CREATE INDEX rotacio_pas_rotacio ON rotacio_pas(rotacio_id, ordre);
