-- Migració 013: la mateixa falca en diversos períodes d'una campanya (D-172).
-- Fins ara una pista només podia ser una vegada a la campanya (la clau era campanya + pista). Ara la falca s'identifica
-- per la posició: la mateixa pista hi pot ser més d'una vegada, amb períodes que no se solapin (ho comprova
-- Campaigns.Validate). SQLite no deixa canviar la clau d'una taula: es refà i s'hi copien les dades, numerant les
-- posicions de nou (0, 1, 2…) en l'ordre d'ara.

CREATE TABLE campanya_falca_nova (
  campanya_id INTEGER NOT NULL REFERENCES campanya(id) ON DELETE CASCADE,
  ordre       INTEGER NOT NULL,                        -- la posició a la campanya (0, 1, 2…)
  pista_id    INTEGER NOT NULL REFERENCES pista(id),   -- una pista d'una campanya no es pot esborrar
  data_inici  TEXT,            -- null = les de la campanya
  data_fi     TEXT,
  dies_mask   INTEGER NOT NULL DEFAULT 127,
  pes         INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (campanya_id, ordre)
);

INSERT INTO campanya_falca_nova (campanya_id, ordre, pista_id, data_inici, data_fi, dies_mask, pes)
SELECT campanya_id, ROW_NUMBER() OVER (PARTITION BY campanya_id ORDER BY ordre, pista_id) - 1,
       pista_id, data_inici, data_fi, dies_mask, pes
FROM campanya_falca;

DROP TABLE campanya_falca;
ALTER TABLE campanya_falca_nova RENAME TO campanya_falca;
CREATE INDEX campanya_falca_pista ON campanya_falca(pista_id);
