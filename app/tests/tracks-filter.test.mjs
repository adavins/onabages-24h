// Proves dels filtres i les columnes de la pestanya «Pistes» (D-111): sense pàgina.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  defaultFilters, filtersToParams, filterChips, activeFilterCount, withoutFilter, isFiltered, sortBy, trackStates, dateCell, totalText,
} from '../renderer/js/tracks-filter.js';

test('filtersToParams: per defecte, només el text i l\'ordre', () => {
  assert.deepEqual(filtersToParams(defaultFilters()), { text: '', sort: 'artist', desc: false });
});

test('filtersToParams: la subcategoria mana sobre la categoria, i cada filtre amb els seus camps', () => {
  const p = filtersToParams({
    ...defaultFilters(), categoryId: 1, subcategoryId: 5, state: 'missing', inEvents: 'no', validity: 'overlaps',
    validFrom: '2026-12-01', validTo: '', played: 'range', playedFrom: '2026-01-01', playedTo: '2026-06-30', tagId: 3, type: 'commercial',
  });
  assert.deepEqual(p, {
    text: '', sort: 'artist', desc: false, subcategoryId: 5, state: 'missing', inEvents: false, validity: 'overlaps',
    validFrom: '2026-12-01', playedFrom: '2026-01-01', playedTo: '2026-06-30', tagId: 3, type: 'commercial',
  });
  // Les dates de l'interval només compten amb «overlaps»; «mai» no porta dates
  assert.deepEqual(filtersToParams({ ...defaultFilters(), validity: 'expired', validFrom: '2026-01-01', played: 'never', playedFrom: '2026-01-01' }),
    { text: '', sort: 'artist', desc: false, validity: 'expired', neverPlayed: true });
});

test('filterChips, activeFilterCount i withoutFilter: les pastilles dels filtres de dins', () => {
  const f = { ...defaultFilters(), text: 'nadal', categoryId: 2, inEvents: /** @type {'yes'} */ ('yes'), validity: 'overlaps', validFrom: '2026-12-01', validTo: '2026-12-31', tagId: 7 };
  const chips = filterChips(f, { tagName: () => 'Nadal' });
  assert.deepEqual(chips.map((c) => c.text), ['Dins d\'esdeveniments', 'Vigents del 01/12/2026 al 31/12/2026', 'Etiqueta: Nadal']);
  assert.equal(activeFilterCount(f), 3); // el text i la categoria no compten: es veuen sempre
  const less = withoutFilter(f, 'validity');
  assert.equal(less.validity, '');
  assert.equal(less.validFrom, '');
  assert.equal(withoutFilter(f, 'tagId').tagId, null);
  assert.equal(isFiltered(defaultFilters()), false);
  assert.equal(isFiltered({ ...defaultFilters(), text: ' x ' }), true);
  assert.deepEqual(filterChips({ ...defaultFilters(), played: 'never' }).map((c) => c.text), ['No han sonat mai']);
});

test('sortBy: la mateixa columna gira; les reproduccions comencen per les més sonades', () => {
  const f = defaultFilters();
  assert.deepEqual([sortBy(f, 'artist').sort, sortBy(f, 'artist').desc], ['artist', true]);
  assert.deepEqual([sortBy(f, 'plays').sort, sortBy(f, 'plays').desc], ['plays', true]);
  assert.deepEqual([sortBy(f, 'duration').sort, sortBy(f, 'duration').desc], ['duration', false]);
});

test('trackStates: el fitxer, desactivada i la vigència, amb text', () => {
  const today = '2026-11-20';
  assert.deepEqual(trackStates({ fileState: 'ok', enabled: true }, today), []);
  assert.deepEqual(trackStates({ fileState: 'missing', enabled: false, validUntil: '2026-11-19' }, today).map((s) => s.text), ['Falta el fitxer', 'Desactivada', 'Vençuda']);
  assert.equal(trackStates({ validFrom: '2026-12-01' }, today)[0].title, 'Comença el 01/12/2026');
  assert.deepEqual(trackStates({ validUntil: '2026-11-20' }, today), []); // l'últim dia encara és vigent
});

test('dateCell i totalText', () => {
  assert.equal(dateCell('2026-11-30'), '30/11/2026');
  assert.equal(dateCell(null), '');
  assert.equal(totalText(0), 'Cap pista amb aquests filtres.');
  assert.equal(totalText(1), '1 pista');
  assert.match(totalText(1234), /^1.234 pistes$/);
});
