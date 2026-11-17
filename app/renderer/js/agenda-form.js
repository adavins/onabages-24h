// @ts-check
// Agenda de bosses (D-170): textos, validació i petites operacions de la finestra, sense tocar la pàgina (es proven amb
// node --test). Els mesos van com a textos «2026-12» (docs/protocol.md, 4.16).

const MONTHS = ['gener', 'febrer', 'març', 'abril', 'maig', 'juny', 'juliol', 'agost', 'setembre', 'octubre', 'novembre', 'desembre'];

/** «2026-12» del mes d'una data (hora local). @param {Date} date */
export function monthOf(date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
}

/** «desembre de 2026». @param {string} month  «2026-12» */
export function monthName(month) {
  const [y, m] = month.split('-').map(Number);
  const name = MONTHS[m - 1];
  if (!name) return month;
  return `${name} de ${y}`;
}

/** Curt, per a la taula: «des. 2026». @param {string} month */
export function monthShort(month) {
  const [y, m] = month.split('-').map(Number);
  const name = MONTHS[m - 1];
  return name ? `${name.slice(0, 3)}. ${y}` : month;
}

/** El mes següent: «2026-12» → «2027-01». @param {string} month */
export function nextMonth(month) {
  const [y, m] = month.split('-').map(Number);
  return m === 12 ? `${y + 1}-01` : `${y}-${String(m + 1).padStart(2, '0')}`;
}

/**
 * Per què no es pot desar (o null): el mateix que comprova el motor (BagAgenda.Problem), per avisar abans d'enviar.
 * @param {string} name @param {number[]} bagIds @param {number[]} subcategoryIds @param {string} startMonth
 * @returns {string | null}
 */
export function agendaProblem(name, bagIds, subcategoryIds, startMonth) {
  if (!name.trim()) return 'L\'agenda necessita un nom.';
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(startMonth)) return 'Tria el mes d\'inici.';
  if (bagIds.length === 0) return 'Afegeix com a mínim una bossa.';
  if (new Set(bagIds).size !== bagIds.length) return 'Una bossa hi surt dues vegades.';
  if (new Set(subcategoryIds).size !== subcategoryIds.length) return 'Una subcategoria hi surt dues vegades.';
  if (subcategoryIds.length < bagIds.length)
    return `Amb ${bagIds.length} bosses calen com a mínim ${bagIds.length} subcategories (cada mes en sonen tantes com bosses).`;
  return null;
}

/**
 * «Generalista A: Adult 1 · Generalista B: Adult 2» (sense la categoria, que és la mateixa i allarga el text).
 * @param {{ bag: string | null, subcategory: string | null }[]} assignment
 */
export function assignmentText(assignment) {
  if (assignment.length === 0) return 'encara no ha començat';
  return assignment.map((a) => `${a.bag ?? '?'}: ${shortSubcategory(a.subcategory)}`).join(' · ');
}

/** «Music / Adult 1» → «Adult 1». @param {string | null} name */
export function shortSubcategory(name) {
  if (!name) return '?';
  const i = name.indexOf(' / ');
  return i >= 0 ? name.slice(i + 3) : name;
}

/**
 * Una còpia de la llista amb l'element de la posició <index> mogut <delta> llocs (si no pot, la mateixa ordenació).
 * @template T @param {T[]} list @param {number} index @param {number} delta @returns {T[]}
 */
export function moveItem(list, index, delta) {
  const to = index + delta;
  const copy = [...list];
  if (index < 0 || index >= list.length || to < 0 || to >= list.length) return copy;
  const [item] = copy.splice(index, 1);
  copy.splice(to, 0, item);
  return copy;
}

/**
 * L'avís en canviar a mà una bossa d'una agenda activa: el canvi dura fins al dia 1 del mes que ve, quan l'agenda hi
 * posarà la que li toca. «Generalista A» la canvia l'agenda «Adult»: aquest canvi durarà fins a l'1 de gener de 2027,
 * quan passarà a Adult 3.
 * @param {{ name: string, next: { bagId: number, subcategory: string | null }[] }} agenda
 * @param {{ id: number, name: string }} bag @param {string} month  el mes d'ara («2026-12»)
 */
export function manualBagChangeText(agenda, bag, month) {
  const next = agenda.next.find((a) => a.bagId === bag.id);
  const first = `l'1 de ${monthName(nextMonth(month))}`;
  const until = next
    ? `Aquest canvi durarà fins a ${first}, quan l'agenda hi posarà ${shortSubcategory(next.subcategory)}.`
    : 'L\'agenda encara no ha començat: quan comenci, hi posarà la subcategoria que li toqui.';
  return `«${bag.name}» la canvia l'agenda «${agenda.name}». ${until} Si vols que el canvi duri, canvia l'agenda (Rotacions → Bosses → «Agenda…»).`;
}

/**
 * Les bosses que canvien en desar (per a la confirmació): les de l'assignació d'aquest mes que ara tenen una altra
 * subcategoria.
 * @param {{ bagId: number, bag: string | null, subcategoryId: number, subcategory: string | null }[]} assignment
 * @param {{ id: number, subcategoryId: number | null }[]} bags  les bosses d'ara (bags.list)
 */
export function bagsThatChange(assignment, bags) {
  return assignment.filter((a) => bags.find((b) => b.id === a.bagId)?.subcategoryId !== a.subcategoryId);
}
