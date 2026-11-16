-- Migració 010 (fase 5, pas 4; D-159): un origen nou a l'historial, «EMERGENCIA», per a les pistes d'emergència del
-- disc local que sonen quan la pista que tocava no s'ha pogut obrir a temps. SQLite no deixa canviar una restricció
-- CHECK: es torna a crear la taula, amb les mateixes columnes i índexs.

CREATE TABLE historial_nova (
  id          INTEGER PRIMARY KEY,
  pista_id    INTEGER REFERENCES pista(id) ON DELETE SET NULL,
  inici       TEXT NOT NULL,                  -- UTC, hora real en què ha començat a sonar
  durada_ms   INTEGER,                        -- quant ha sonat (null = encara sona)
  sencera     INTEGER,                        -- 1 = ha sonat fins al final previst
  origen      TEXT NOT NULL CHECK (origen IN ('AUTODJ','MANUAL','ESDEVENIMENT','EMERGENCIA')), -- D-034, D-159
  artista     TEXT NOT NULL,
  titol       TEXT NOT NULL,
  ruta        TEXT NOT NULL,
  campanya_id INTEGER
);
INSERT INTO historial_nova (id, pista_id, inici, durada_ms, sencera, origen, artista, titol, ruta, campanya_id)
  SELECT id, pista_id, inici, durada_ms, sencera, origen, artista, titol, ruta, campanya_id FROM historial;
DROP TABLE historial;
ALTER TABLE historial_nova RENAME TO historial;
CREATE INDEX historial_inici ON historial(inici);
CREATE INDEX historial_pista ON historial(pista_id, inici);
CREATE INDEX historial_campanya ON historial(campanya_id, inici);
