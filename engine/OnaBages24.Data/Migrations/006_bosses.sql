-- Migració 006: bosses amb nom (fase 3, pas 8c; D-014, D-096).
-- Una bossa és un nom («Generalista A») que correspon ara a una subcategoria («Adult 5»). Els passos de les rotacions
-- poden apuntar a la bossa en lloc de la subcategoria: per alternar les bosses n'hi ha prou amb canviar aquesta taula
-- (a mà o amb l'acció BOSSA del planificador), i les rotacions no es toquen.

CREATE TABLE bossa (
  id               INTEGER PRIMARY KEY,
  nom              TEXT NOT NULL UNIQUE COLLATE NOCASE,
  subcategoria_id  INTEGER REFERENCES subcategoria(id) ON DELETE SET NULL,  -- null = cap (els passos se salten)
  canviada         TEXT,                                                    -- instant UTC de l'últim canvi de subcategoria
  canviada_per     TEXT                                                     -- «a mà», «esdeveniment «Canvi de bosses»»…
);

-- Un pas apunta a una subcategoria (o categoria) o bé a una bossa
ALTER TABLE rotacio_pas ADD COLUMN bossa_id INTEGER REFERENCES bossa(id);
