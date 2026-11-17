-- Migració 012: agenda de bosses (D-170).
-- Una agenda canvia soles les subcategories d'unes bosses amb nom el dia 1 de cada mes, rotant per una llista de
-- subcategories. Cas real: 9 subcategories «Adult 1»…«Adult 9» i dues bosses («Generalista A» i «B»); cada mes en
-- sonen dues de seguides i avancen d'una, i la que continua es queda a la mateixa bossa. Quina toca cada mes es calcula
-- (BagAgenda.Assignment); aquí només es desa la regla i l'últim mes en què s'ha aplicat.

CREATE TABLE agenda_bosses (
  id            INTEGER PRIMARY KEY,
  nom           TEXT NOT NULL UNIQUE COLLATE NOCASE,
  mes_inici     TEXT NOT NULL,                -- 'AAAA-MM': el mes en què sonen les primeres subcategories de la llista
  activa        INTEGER NOT NULL DEFAULT 1,
  aplicada_mes  TEXT                          -- 'AAAA-MM' de l'últim canvi fet (null = encara cap): un sol canvi per mes
);

-- Les bosses de l'agenda, per ordre. Una bossa només pot ser en una agenda.
CREATE TABLE agenda_bossa (
  agenda_id  INTEGER NOT NULL REFERENCES agenda_bosses(id) ON DELETE CASCADE,
  posicio    INTEGER NOT NULL,
  bossa_id   INTEGER NOT NULL UNIQUE REFERENCES bossa(id),
  PRIMARY KEY (agenda_id, posicio)
);

-- Les subcategories per on va rotant, per ordre (després de l'última es torna a la primera).
CREATE TABLE agenda_subcategoria (
  agenda_id        INTEGER NOT NULL REFERENCES agenda_bosses(id) ON DELETE CASCADE,
  posicio          INTEGER NOT NULL,
  subcategoria_id  INTEGER NOT NULL REFERENCES subcategoria(id),
  PRIMARY KEY (agenda_id, posicio),
  UNIQUE (agenda_id, subcategoria_id)
);
