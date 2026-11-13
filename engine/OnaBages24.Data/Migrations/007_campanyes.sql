-- Migració 007: campanyes de falques (fase 4, pas 3a; D-011, D-029, D-122; docs/planificador.md, secció 10).
-- Una campanya és l'element que es programa (acció CAMPANYA), amb diverses falques. El pla de passis (quina falca sona a
-- cada passi) el calcula el motor al pas 3b.

CREATE TABLE campanya (
  id          INTEGER PRIMARY KEY,
  nom         TEXT NOT NULL,
  anunciant   TEXT,
  data_inici  TEXT NOT NULL,   -- 'YYYY-MM-DD'
  data_fi     TEXT NOT NULL,
  seleccio    TEXT NOT NULL DEFAULT 'ALTERNA' CHECK (seleccio IN ('ALTERNA','PES','SEQUENCIA')),
  estat       TEXT NOT NULL DEFAULT 'ESBORRANY' CHECK (estat IN ('ESBORRANY','ACTIVA','ACABADA')),
  passis_contractats_total INTEGER,   -- opcional; només per avisar
  passis_contractats_dia   INTEGER,
  notes       TEXT,
  modificat   TEXT NOT NULL    -- instant UTC de l'últim canvi
);

-- Les falques de la campanya. Les dates i els dies propis restringeixen els de la campanya; el pes és per a PES i
-- l'ordre, per a SEQUENCIA (A, B, C, A, B, C…) i per mostrar-les.
CREATE TABLE campanya_falca (
  campanya_id INTEGER NOT NULL REFERENCES campanya(id) ON DELETE CASCADE,
  pista_id    INTEGER NOT NULL REFERENCES pista(id),   -- una pista d'una campanya no es pot esborrar
  ordre       INTEGER NOT NULL DEFAULT 0,
  data_inici  TEXT,            -- null = les de la campanya
  data_fi     TEXT,
  dies_mask   INTEGER NOT NULL DEFAULT 127,
  pes         INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (campanya_id, pista_id)
);
CREATE INDEX campanya_falca_pista ON campanya_falca(pista_id);

-- Pla de passis (D-029): quina falca toca a cada passi previst. S'omple al pas 3b.
CREATE TABLE campanya_passi (
  campanya_id     INTEGER NOT NULL REFERENCES campanya(id) ON DELETE CASCADE,
  esdeveniment_id INTEGER NOT NULL,
  programat_per   TEXT NOT NULL,       -- data i hora previstes del passi (UTC)
  pista_id        INTEGER,             -- null = cap falca vàlida aquell dia
  PRIMARY KEY (campanya_id, esdeveniment_id, programat_per)
);

-- Cada emissió d'una falca queda lligada a la campanya que l'ha fet sonar (certificat d'emissió, pas 4). Sense clau
-- forana: l'historial es conserva com a mínim 2 anys (D-011).
ALTER TABLE historial ADD COLUMN campanya_id INTEGER;
CREATE INDEX historial_campanya ON historial(campanya_id, inici);
