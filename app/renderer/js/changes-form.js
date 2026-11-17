// @ts-check
// Registre de canvis (fase 5, pas 8c; D-164): els tipus de canvi del filtre i els textos de la finestra, sense tocar la
// pàgina (es proven amb node --test). El registre el desa el motor (changes.list; docs/protocol.md, 4.14).

/**
 * Els tipus de canvi: el text del desplegable i els prefixos de les ordres que hi entren.
 * @type {[string, string, string[]][]}
 */
export const AREAS = [
  ['emissio', 'Emissió (cua, reproductor, AutoDJ, sortida)', ['queue.', 'player.', 'aux.', 'autodj.', 'output.', 'scheduler.pause', 'scheduler.resume']],
  ['esdeveniments', 'Esdeveniments i calendaris', ['scheduler.save', 'scheduler.delete', 'calendar.', 'savedCalendars.', 'validity.']],
  ['pistes', 'Pistes i carpetes', ['library.', 'category.', 'subcategory.', 'tags.', 'folders.']],
  ['rotacions', 'Rotacions i bosses', ['rotations.', 'bags.', 'separation.', 'sweepers.', 'reserve.']],
  ['campanyes', 'Campanyes i temporada', ['campaigns.', 'season.']],
  ['configuracio', 'Configuració i còpies', ['station.', 'silence.', 'emergency.', 'mail.', 'nowplaying.', 'ducking.', 'processor.', 'backups.', 'engine.']],
  ['usuaris', 'Usuaris i sessions', ['users.', 'security.', 'session.', 'restableix-administrador']],
];

/** Els prefixos d'un tipus (buit = tots). @param {string} area */
export function areaPrefixes(area) {
  return AREAS.find(([value]) => value === area)?.[2] ?? [];
}

/** El tipus d'una ordre, per a la columna «Tipus» («Esdeveniments i calendaris»), o «Altres». @param {string} command */
export function areaText(command) {
  // El prefix més llarg que hi encaixa (scheduler.save és d'esdeveniments; scheduler.pause, d'emissió)
  let best = { length: 0, text: 'Altres' };
  for (const [, text, prefixes] of AREAS) {
    for (const prefix of prefixes) {
      if (command.startsWith(prefix) && prefix.length > best.length) best = { length: prefix.length, text: text.replace(/ \(.*\)$/, '') };
    }
  }
  return best.text;
}

/** Qui ho ha fet: el nom, o «Sense usuari» (encara no n'hi havia cap, o des de l'ordinador). @param {{ user: string }} entry */
export function userText(entry) {
  return entry.user || 'Sense usuari';
}

/** «Cap canvi», «1 canvi», «1.234 canvis». @param {number} n */
export function countText(n) {
  if (n === 0) return 'Cap canvi';
  return `${n.toLocaleString('ca-ES')} ${n === 1 ? 'canvi' : 'canvis'}`;
}

/**
 * Els paràmetres en una línia llegible: «name: Butlletí · startTime: 13:00:00 · …» (sense les cometes del JSON).
 * @param {string | null} detail
 */
export function detailText(detail) {
  if (!detail) return '';
  try {
    const params = JSON.parse(detail);
    return Object.entries(params)
      .map(([key, value]) => `${key}: ${typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value)}`)
      .join(' · ');
  } catch {
    return detail;
  }
}
