-- Migració 009: incidències (fase 5, pas 1; D-156; D-013).
-- El registre de tot el que ha anat malament a l'emissió: la sortida d'àudio, un fitxer que no s'ha pogut fer sonar, un
-- esdeveniment omès o fet tard, les carpetes vigilades… Les obertes es veuen a la barra d'avisos; totes, a la llista.
-- Es conserven 1 any. Són el registre d'aquest ordinador: restaurar una còpia no les toca (D-152).

CREATE TABLE incidencia (
  id          INTEGER PRIMARY KEY,
  tipus       TEXT NOT NULL,                -- SORTIDA, FITXER, ESDEVENIMENT_OMES, ESDEVENIMENT_ERROR, ESDEVENIMENT_TARD, CARPETA, PAUSA, BUTT, AUTODJ…
  clau        TEXT NOT NULL,                -- per no obrir-ne dues de la mateixa cosa (p. ex. 'carpeta:12')
  gravetat    TEXT NOT NULL CHECK (gravetat IN ('INFO', 'AVIS', 'GREU')),
  text        TEXT NOT NULL,
  detall      TEXT,
  inici       TEXT NOT NULL,                -- instant UTC
  fi          TEXT,                         -- instant UTC; NULL = encara oberta
  recuperada  INTEGER NOT NULL DEFAULT 0    -- 1 = el motor ho ha resolt sol (p. ex. ha saltat la pista que no es podia llegir)
);

CREATE INDEX incidencia_inici ON incidencia (inici);
CREATE INDEX incidencia_oberta ON incidencia (fi) WHERE fi IS NULL;
