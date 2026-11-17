-- Migració 011: usuaris i registre de canvis (fase 5, pas 8; D-035, D-155, D-164).
-- Usuaris: per operar cal iniciar sessió (l'emissió no en depèn mai). La contrasenya es desa com a empremta (PBKDF2):
-- no es pot recuperar. Els usuaris no s'esborren (el registre de canvis hi fa referència): es desactiven.
-- Registre de canvis: qui ha canviat què i quan. Es conserva 2 anys.
-- Totes dues són d'aquest ordinador: restaurar una còpia no les toca (com les incidències).

CREATE TABLE usuari (
  id            INTEGER PRIMARY KEY,
  nom           TEXT NOT NULL UNIQUE COLLATE NOCASE,  -- el nom per iniciar sessió
  nom_complet   TEXT NOT NULL DEFAULT '',
  rol           TEXT NOT NULL CHECK (rol IN ('OPERADOR', 'PROGRAMADOR', 'ADMINISTRADOR')),
  empremta      TEXT NOT NULL,                        -- PBKDF2-SHA256, Base64
  sal           TEXT NOT NULL,                        -- aleatòria per a cada usuari, Base64
  iteracions    INTEGER NOT NULL,
  actiu         INTEGER NOT NULL DEFAULT 1,
  provisional   INTEGER NOT NULL DEFAULT 0,           -- 1 = s'ha de canviar en iniciar sessió (p. ex. després de restablir-la)
  creat         TEXT NOT NULL,                        -- instant UTC
  darrer_acces  TEXT                                  -- instant UTC de l'últim inici de sessió
);

CREATE TABLE registre_canvis (
  id          INTEGER PRIMARY KEY,
  moment      TEXT NOT NULL,                          -- instant UTC
  usuari_id   INTEGER REFERENCES usuari (id),         -- NULL = sense usuari (encara no n'hi havia cap, o el motor mateix)
  usuari_nom  TEXT NOT NULL DEFAULT '',               -- el nom en aquell moment (per si després canvia)
  ordre       TEXT NOT NULL,                          -- l'ordre del protocol (p. ex. 'scheduler.save')
  resum       TEXT NOT NULL,                          -- «Ha desat l'esdeveniment «Butlletí 13 h»»
  detall      TEXT                                    -- els paràmetres (JSON), sense contrasenyes ni imatges
);

CREATE INDEX registre_canvis_moment ON registre_canvis (moment);
CREATE INDEX registre_canvis_usuari ON registre_canvis (usuari_id);
