// Proves de l'HTML dels informes (fase 4, pas 4b; D-138): app/renderer/js/report-html.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { esc, programmingHtml, certificateHtml, suggestedName, defaultPeriod } from '../renderer/js/report-html.js';

const station = { name: 'Ona Bages', frequency: '90.2 FM', logo: null };
const campaign = {
  kind: 'campaign', name: 'Ferreteria <Black Friday>', advertiser: 'Ferreteria Santpedor', startDate: '2026-11-01', endDate: '2026-11-30',
};

test('esc: els textos de les dades no poden posar etiquetes', () => {
  assert.equal(esc('<b>"A" & \'B\'</b>'), '&lt;b&gt;&quot;A&quot; &amp; &#39;B&#39;&lt;/b&gt;');
  assert.equal(esc(null), '');
});

test('informe de programació: capçalera, falques, graella i cada passi previst', () => {
  const html = programmingHtml({
    subject: campaign, from: '2026-11-01', to: '2026-11-30', total: 2, empty: 1,
    bySpot: [{ trackId: 1, artist: 'Ferreteria', title: 'Falca A', durationMs: 20_000, count: 1 }],
    grid: [{ time: '10:00:00', days: [true, false, false, false, false, false, false] }],
    passes: [
      { at: '2026-11-02T10:00:00.0000000+01:00', eventName: 'Falques', trackId: 1, title: 'Falca A' },
      { at: '2026-11-03T10:00:00.0000000+01:00', eventName: 'Falques', trackId: null, title: null },
    ],
  }, station, '2026-11-14');
  assert.match(html, /Ona Bages/);
  assert.match(html, /90\.2 FM/);
  assert.match(html, /Data de l'informe: 14\/11\/2026/);
  assert.match(html, /Ferreteria &lt;Black Friday&gt;/); // escapat
  assert.doesNotMatch(html, /<Black Friday>/);
  assert.match(html, /Passis previstos: <strong>2<\/strong>/);
  assert.match(html, /Ferreteria - Falca A<\/td><td class="rp-num">0:20<\/td><td class="rp-num">1<\/td>/);
  assert.match(html, /<th>10:00<\/th><td class="rp-on">✔<\/td><td><\/td>/);
  assert.match(html, /<td>2\/11\/2026<\/td><td>dilluns<\/td><td>10:00<\/td><td>Falca A<\/td>/);
  assert.match(html, /cap falca vàlida/);
});

test('certificat: la frase amb el nombre de passis i cada emissió real', () => {
  const html = certificateHtml({
    subject: campaign, from: '2026-11-01', to: '2026-11-14', total: 1,
    bySpot: [{ trackId: 1, artist: 'Ferreteria', title: 'Falca A', count: 1 }],
    emissions: [{ at: '2026-11-02T10:00:04.5000000+01:00', trackId: 1, artist: 'Ferreteria', title: 'Falca A', playedMs: 20_000, complete: true }],
  }, { ...station, logo: 'data:image/png;base64,AAA=' }, '2026-11-14');
  assert.match(html, /<img class="rp-logo" src="data:image\/png;base64,AAA="/);
  assert.match(html, /Ona Bages certifica que la campanya «Ferreteria &lt;Black Friday&gt;» s'ha emès <strong>1 passi<\/strong> entre el 1\/11\/2026 i el 14\/11\/2026/);
  assert.match(html, /<td>2\/11\/2026<\/td><td>10:00:04<\/td><td>Ferreteria<\/td><td>Falca A<\/td><td>Sí<\/td>/);
});

test('suggestedName i defaultPeriod', () => {
  assert.equal(suggestedName('certificate', { subject: { kind: 'track', artist: 'A/B', title: 'Falca: 1' }, from: '2026-11-01', to: '2026-11-30' }),
    'Certificat d\'emissió - AB - Falca 1 - del 2026-11-01 al 2026-11-30.pdf');
  const c = { startDate: '2026-11-01', endDate: '2026-11-30' };
  assert.deepEqual(defaultPeriod('programming', c, '2026-11-14'), { from: '2026-11-01', to: '2026-11-30' });
  assert.deepEqual(defaultPeriod('certificate', c, '2026-11-14'), { from: '2026-11-01', to: '2026-11-14' });
  assert.deepEqual(defaultPeriod('certificate', c, '2026-12-20'), { from: '2026-11-01', to: '2026-11-30' }); // ja acabada
  assert.deepEqual(defaultPeriod('certificate', { startDate: '2026-12-01', endDate: '2026-12-31' }, '2026-11-14'), { from: '2026-12-01', to: '2026-12-01' });
  assert.deepEqual(defaultPeriod('programming', { validFrom: null, validUntil: null }, '2026-11-14'), { from: '2026-11-14', to: '2026-12-11' });
  assert.deepEqual(defaultPeriod('certificate', { validFrom: null, validUntil: null }, '2026-11-14'), { from: '2026-10-16', to: '2026-11-14' });
});
