// Proves dels textos de les carpetes vigilades (fase 3, pas 7c-2). Amb «npm test», sense Electron.
// Els avisos de les carpetes ara són incidències del motor (fase 5, pas 1; D-156): es proven a IncidentsTests.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { folderAction } from '../renderer/js/folders-text.js';

test('accions del registre amb text, icona i color', () => {
  assert.equal(folderAction('imported').text, 'Importada');
  assert.equal(folderAction('missing').css, 'text-warning');
  assert.equal(folderAction('desconeguda').text, 'desconeguda');
});
