-- Migració 005: carpetes vigilades i bosses renovables (fase 3, pas 7c; D-026, D-033, D-090).
-- La carpeta (subcategoria.carpeta) i «renovable» ja hi eren des de la 001. Aquí: com queden les pistes noves que
-- s'importen soles des de la carpeta, i el registre de tot el que fa la vigilància (es pot consultar des de la pantalla).

ALTER TABLE subcategoria ADD COLUMN tipus_defecte TEXT;                                -- null = MUSICA
ALTER TABLE subcategoria ADD COLUMN etiquetes_defecte TEXT;                            -- separades per comes; null = cap
ALTER TABLE subcategoria ADD COLUMN durada_variable_defecte INTEGER NOT NULL DEFAULT 0;

-- Registre de les carpetes vigilades: cada pista importada, recuperada, que falta, esborrada (bossa renovable) o
-- actualitzada (fitxer substituït), i els problemes (carpeta que no es troba…). Sense clau forana: es conserva encara
-- que s'esborri la subcategoria o la pista.
CREATE TABLE registre_carpetes (
  id               INTEGER PRIMARY KEY,
  moment           TEXT NOT NULL,                 -- instant UTC
  subcategoria_id  INTEGER,
  subcategoria     TEXT NOT NULL,                 -- «Categoria / Subcategoria» en aquell moment
  ruta             TEXT NOT NULL,                 -- el fitxer (o la carpeta, per als problemes de la carpeta)
  accio            TEXT NOT NULL CHECK (accio IN ('IMPORTADA','RECUPERADA','FALTA','ESBORRADA','ACTUALITZADA','ERROR','CARPETA')),
  detall           TEXT
);
CREATE INDEX registre_carpetes_moment ON registre_carpetes(moment);
