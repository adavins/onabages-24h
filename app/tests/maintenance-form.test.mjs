// Manteniment de la programació (D-194): textos i recomptes de la pantalla.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { CASES, actionInfo, groupFindings, applySummary, resultText } from '../renderer/js/maintenance-form.js';

const finding = (key, kase, action, name = key) => ({ key, case: kase, subject: 'event', id: 1, name, reason: 'Motiu', action });

test('les propostes s\'agrupen per cas, en l\'ordre de la pantalla, sense els casos buits', () => {
  const groups = groupFindings([
    finding('track:1', 'PISTA_CADUCADA', 'ESBORRA_PISTA'),
    finding('event:2', 'ESDEVENIMENT_ACABAT', 'ESBORRA_ESDEVENIMENT'),
    finding('event:3', 'ESDEVENIMENT_ACABAT', 'ESBORRA_ESDEVENIMENT'),
  ]);
  assert.deepEqual(groups.map((g) => [g.code, g.findings.length]), [['ESDEVENIMENT_ACABAT', 2], ['PISTA_CADUCADA', 1]]);
  assert.equal(groups[0].title, 'Esdeveniments acabats');
});

test('tots els casos del motor tenen títol, icona i explicació', () => {
  assert.equal(CASES.length, 10);
  for (const [code, title, icon, help] of CASES) assert.ok(code && title && icon.startsWith('bi-') && help, code);
});

test('cada acció es diu amb text, icona i color', () => {
  assert.deepEqual(actionInfo('DESACTIVA_PISTA'), { text: 'Es desactiva', icon: 'bi-slash-circle', css: 'text-warning' });
  assert.equal(actionInfo('RES').text, 'RES');
});

test('la confirmació diu què passarà', () => {
  assert.deepEqual(applySummary([
    finding('event:1', 'ESDEVENIMENT_ACABAT', 'ESBORRA_ESDEVENIMENT'),
    finding('event:2', 'ESDEVENIMENT_DUPLICAT', 'ESBORRA_ESDEVENIMENT'),
    finding('track:3', 'PISTA_CADUCADA', 'DESACTIVA_PISTA'),
  ]), ["S'esborraran 2 esdeveniments.", 'Es desactivarà 1 pista.']);
});

test('el resultat diu què s\'ha fet, què no i quina còpia es pot restaurar', () => {
  const text = resultText({
    backup: 'M:\\dades\\copies\\onabages24-abans-del-manteniment-2026-11-19_120000.db',
    done: [{ key: 'event:1', text: 'Ha esborrat l\'esdeveniment «A»' }],
    skipped: [{ key: 'event:2', reason: 'Ja no hi és' }],
    error: null,
  });
  assert.match(text, /^Fet: 1 canvi\./);
  assert.match(text, /1 proposta no s'ha aplicat/);
  assert.match(text, /«onabages24-abans-del-manteniment-2026-11-19_120000\.db»/);
});
