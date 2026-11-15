// @ts-check
// Treure una pista o una campanya dels esdeveniments (validity.removeFromEvents): des de la finestra d'avisos, per a les
// caducades (D-146), i des del calendari, de tots els esdeveniments (D-151). Els esdeveniments que només la feien sonar
// a ella no es poden quedar sense res: es pregunta a part si s'esborren (scheduler.delete).

import { confirmAction } from './confirm.js';
import { removedText } from '../warnings-form.js';

/**
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {{ subject: 'track' | 'campaign', id: number, name: string, where: string, all?: boolean }} target
 *   where: de quins esdeveniments es treurà, en text («l'esdeveniment «X»», «3 esdeveniments actius»…);
 *   all: també dels desactivats i els acabats
 * @returns {Promise<boolean>}  si s'ha fet (s'ha confirmat i el motor ho ha fet)
 */
export async function removeFromEvents(send, { subject, id, name, where, all = false }) {
  const what = subject === 'campaign' ? `la campanya «${name}»` : `la pista «${name}»`;
  const ok = await confirmAction({
    title: 'Treure-la dels esdeveniments?',
    message: `Es traurà ${what} de ${where}. Els blocs de falques que es quedin buits s'esborraran. ` +
      'L\'historial i la fitxa no es toquen, i no es pot desfer.',
    confirm: 'Treu-la',
  });
  if (!ok) return false;
  let message;
  let done = false;
  try {
    const params = subject === 'campaign' ? { campaignId: id, all } : { trackId: id, all };
    const result = await send('validity.removeFromEvents', params);
    done = true;
    /** @type {{ deleted: string[], failed: { name: string, message: string }[] } | undefined} */
    let events;
    if (result.kept.length > 0) events = await deleteKept(send, result.kept, what);
    message = removedText(result, events);
  } catch (err) {
    message = `No s'ha pogut treure: ${/** @type {Error} */ (err).message}`;
  }
  await confirmAction({ title: 'Treure-la dels esdeveniments', message, confirm: 'D\'acord', info: true });
  return done;
}

/**
 * Els esdeveniments que només la feien sonar a ella: es pregunta si s'esborren. Si no, undefined.
 * @param {(cmd: string, params?: object) => Promise<any>} send
 * @param {{ id: number, name: string }[]} kept @param {string} what
 */
async function deleteKept(send, kept, what) {
  const one = kept.length === 1;
  const names = kept.map((e) => `«${e.name}»`).join(', ');
  const ok = await confirmAction({
    title: one ? 'Esborrar també l\'esdeveniment?' : 'Esborrar també aquests esdeveniments?',
    message: `${one ? 'L\'esdeveniment' : 'Els esdeveniments'} ${names} només ${one ? 'fa' : 'fan'} sonar ${what}, i un esdeveniment no es pot quedar sense res. ` +
      `Si ${one ? 'l\'esborres' : 'els esborres'}, a aquella hora no passarà res (la cua i l'AutoDJ continuen). Si no, ${one ? 'queda' : 'queden'} tal com ${one ? 'era' : 'eren'} i el pots canviar al Planificador. No es pot desfer.`,
    confirm: one ? 'Esborra l\'esdeveniment' : 'Esborra els esdeveniments',
  });
  if (!ok) return undefined;
  /** @type {{ deleted: string[], failed: { name: string, message: string }[] }} */
  const events = { deleted: [], failed: [] };
  for (const ev of kept) {
    try {
      await send('scheduler.delete', { eventId: ev.id });
      events.deleted.push(ev.name);
    } catch (err) {
      events.failed.push({ name: ev.name, message: /** @type {Error} */ (err).message });
    }
  }
  return events;
}
