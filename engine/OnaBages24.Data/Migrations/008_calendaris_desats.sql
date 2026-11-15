-- Migració 008: calendaris desats (fase 4, pas 6a; D-145; docs/planificador.md, secció 10).
-- Una distribució de franges amb nom (com els «calendaris desats» del Programador), que es pot aplicar al calendari
-- d'una altra pista o campanya. No depèn de cap pista ni de cap esdeveniment: només guarda les hores i els dies.

CREATE TABLE calendari_desat (
  id         INTEGER PRIMARY KEY,
  nom        TEXT NOT NULL,
  modificat  TEXT NOT NULL    -- instant UTC de l'últim canvi
);

-- Les franges: una hora de la setmana i els dies en què hi ha de sonar (bit0 = dilluns … bit6 = diumenge).
CREATE TABLE calendari_desat_franja (
  calendari_id INTEGER NOT NULL REFERENCES calendari_desat(id) ON DELETE CASCADE,
  hora         TEXT NOT NULL,   -- 'HH:MM:SS'
  dies_mask    INTEGER NOT NULL CHECK (dies_mask BETWEEN 1 AND 127),
  PRIMARY KEY (calendari_id, hora)
);
