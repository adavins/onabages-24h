-- Migració 003: nou tipus de selecció dels passos de rotació, «la menys reproduïda» (la que ha sonat menys vegades;
-- és el selType 2 de RadioDJ). SQLite no deixa canviar una restricció CHECK: es torna a crear la taula.

CREATE TABLE rotacio_pas_nova (
  id               INTEGER PRIMARY KEY,
  rotacio_id       INTEGER NOT NULL REFERENCES rotacio(id) ON DELETE CASCADE,
  ordre            INTEGER NOT NULL,
  categoria_id     INTEGER REFERENCES categoria(id),
  subcategoria_id  INTEGER REFERENCES subcategoria(id),
  etiqueta_id      INTEGER REFERENCES etiqueta(id),
  seleccio         TEXT NOT NULL DEFAULT 'MENYS_RECENT' CHECK (seleccio IN ('MENYS_RECENT','MENYS_SONADA','ALEATORIA')),
  repeticio        INTEGER NOT NULL DEFAULT 1,
  sweeper          INTEGER NOT NULL DEFAULT 0       -- 1 = trepitjador (sweeper) a l'inici de la pista, com a RadioDJ (fase 3)
);
INSERT INTO rotacio_pas_nova SELECT id, rotacio_id, ordre, categoria_id, subcategoria_id, etiqueta_id, seleccio, repeticio, sweeper FROM rotacio_pas;
DROP TABLE rotacio_pas;
ALTER TABLE rotacio_pas_nova RENAME TO rotacio_pas;
CREATE INDEX rotacio_pas_rotacio ON rotacio_pas(rotacio_id, ordre);
