// Proves dels textos del registre de canvis (fase 5, pas 8c; D-164): app/renderer/js/changes-form.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { areaPrefixes, areaText, userText, countText, detailText } from '../renderer/js/changes-form.js';

test('el tipus de cada ordre, amb el prefix més llarg', () => {
  assert.equal(areaText('scheduler.save'), 'Esdeveniments i calendaris');
  assert.equal(areaText('scheduler.pause'), 'Emissió');
  assert.equal(areaText('library.update'), 'Pistes i carpetes');
  assert.equal(areaText('restableix-administrador'), 'Usuaris i sessions');
  assert.equal(areaText('una.altra'), 'Altres');
  assert.deepEqual(areaPrefixes('campanyes'), ['campaigns.', 'season.']);
  assert.deepEqual(areaPrefixes(''), []);
});

test('usuari, comptes i paràmetres', () => {
  assert.equal(userText({ user: '' }), 'Sense usuari');
  assert.equal(userText({ user: 'joan' }), 'joan');
  assert.equal(countText(0), 'Cap canvi');
  assert.equal(countText(1), '1 canvi');
  assert.equal(countText(1234), '1.234 canvis');
  assert.equal(detailText('{"name":"Butlletí","daysMask":31,"actions":[1]}'), 'name: Butlletí · daysMask: 31 · actions: [1]');
  assert.equal(detailText(null), '');
  assert.equal(detailText('no és json…'), 'no és json…');
});
