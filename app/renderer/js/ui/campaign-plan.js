// @ts-check
// El pla d'una campanya (fase 4, pas 3d-2): el que ja ha sonat i el que està previst, per falca, amb els avisos dels
// passis contractats i un calendari mensual amb els passis de cada dia. Es fa servir a l'editor de la campanya
// («On sona i pla de passis») i al calendari de la campanya. Només mostra el que diu campaigns.plan.

import { el, icon, statusLabel } from './dom.js';
import { DAY_SHORT, DAY_NAMES, monthText } from '../calendar-form.js';
import { planSummary, monthWeeks, dmy } from '../campaign-form.js';

/**
 * @param {any} plan  de campaigns.plan
 * @param {any} campaign  la campanya (startDate, endDate, contractedTotal, contractedPerDay, effectiveState)
 * @param {string} today  'AAAA-MM-DD'
 * @param {{ months?: boolean }} [options]  months: amb el calendari mensual (per defecte, sí)
 */
export function planPanel(plan, campaign, today, { months = true } = {}) {
  const s = planSummary(plan, campaign, today);
  const box = el('div', 'cp-plan-panel');
  if (campaign.effectiveState !== 'ACTIVA') {
    box.append(statusLabel('text-body-secondary small d-block', 'bi-info-circle', 'Només les campanyes actives tenen passis previstos.'));
  }

  // Totals: el que ha sonat, el que està previst i els contractats
  const totals = el('div', 'cp-plan-totals');
  totals.append(
    statusLabel('text-success', 'bi-check-circle-fill', `Han sonat ${s.played}`), ' · ',
    statusLabel('text-info', 'bi-calendar-event', `${s.planned} ${s.planned === 1 ? 'previst' : 'previstos'}`), ' · ',
    el('strong', '', `total ${s.total}`));
  if (campaign.contractedTotal != null) totals.append(` de ${campaign.contractedTotal} contractats`);
  box.append(totals);

  // Per falca
  if (plan.bySpot.length > 0 && s.total > 0) {
    const list = el('ul', 'cp-events');
    for (const spot of plan.bySpot) {
      list.append(el('li', '', `${spot.title ?? `Pista ${spot.trackId}`}: ha sonat ${spot.played ?? 0} · ${spot.count} ${spot.count === 1 ? 'previst' : 'previstos'}`));
    }
    box.append(list);
  }
  for (const w of s.warnings) box.append(statusLabel('text-warning small d-block', 'bi-exclamation-triangle-fill', w));

  if (months) box.append(monthCalendars(s, campaign, today));
  return box;
}

/**
 * Un calendari per a cada mes de la campanya: cada dia, quants passis hi ha (els que han sonat, si ja ha passat; els
 * previstos, si encara ha d'arribar), amb avís si no són els contractats per dia o si n'hi ha de buits.
 * @param {ReturnType<typeof planSummary>} s @param {any} campaign @param {string} today
 */
function monthCalendars(s, campaign, today) {
  const wrap = el('div', 'cpm-wrap');
  if (!campaign.startDate || !campaign.endDate) return wrap;
  const perDay = campaign.contractedPerDay ?? null;
  let [year, month] = campaign.startDate.split('-').map(Number);
  const [endYear, endMonth] = campaign.endDate.split('-').map(Number);
  for (let guard = 0; guard < 14 && (year < endYear || (year === endYear && month <= endMonth)); guard++) {
    const table = el('table', 'cpm');
    const caption = el('caption', '', monthText(`${year}-${String(month).padStart(2, '0')}`));
    const head = el('tr', '');
    head.append(...DAY_SHORT.map((d, i) => {
      const th = el('th', '', d);
      th.title = DAY_NAMES[i];
      return th;
    }));
    const thead = el('thead', '');
    thead.append(head);
    const tbody = el('tbody', '');
    for (const week of monthWeeks(year, month)) {
      const tr = el('tr', '');
      for (const date of week) tr.append(dayCell(date, s, campaign, perDay, today));
      tbody.append(tr);
    }
    table.append(caption, thead, tbody);
    wrap.append(table);
    month++;
    if (month > 12) {
      month = 1;
      year++;
    }
  }
  const legend = el('div', 'cpm-legend form-text');
  legend.append(
    statusLabel('', 'bi-check-circle-fill', 'han sonat'), ' · ',
    statusLabel('', 'bi-calendar-event', 'previstos'), ' · ',
    statusLabel('text-warning', 'bi-exclamation-triangle-fill', perDay != null ? `no són els ${perDay} per dia contractats` : 'hi ha passis buits'));
  wrap.append(legend);
  return wrap;
}

/**
 * @param {string | null} date @param {ReturnType<typeof planSummary>} s @param {any} campaign @param {number | null} perDay
 * @param {string} today
 */
function dayCell(date, s, campaign, perDay, today) {
  if (!date) return el('td', 'cpm-blank');
  const dayNumber = String(Number(date.slice(8)));
  if (date < campaign.startDate || date > campaign.endDate) {
    const td = el('td', 'cpm-out', dayNumber);
    td.title = `${dmy(date)}: fora de les dates de la campanya`;
    return td;
  }
  const d = s.byDay.get(date);
  const past = date < today;
  const count = d ? d.played + d.planned : 0;
  const off = (perDay != null && count !== perDay) || (d?.empty ?? 0) > 0;
  const td = el('td', `cpm-day${past ? ' is-past' : ''}${date === today ? ' is-today' : ''}${off ? ' is-off' : ''}`);
  td.append(el('span', 'cpm-num', dayNumber));
  const n = el('span', 'cpm-count');
  // Color + icona + text (CLAUDE.md): la icona diu si han sonat o són previstos; el número, quants
  n.append(icon(off ? 'bi-exclamation-triangle-fill' : past ? 'bi-check-circle-fill' : 'bi-calendar-event'), ` ${count}`);
  td.append(n);
  const lines = [`${dmy(date)}: ${count} ${count === 1 ? 'passi' : 'passis'}${perDay != null ? ` (contractats: ${perDay})` : ''}`];
  for (const item of d?.items ?? []) {
    lines.push(`${item.time} · ${item.title ?? 'buit: cap falca vàlida'}${item.played ? ' (ha sonat)' : ''}`);
  }
  if (past && count === 0) lines.push('No hi va sonar res.');
  td.title = lines.join('\n');
  td.setAttribute('aria-label', lines[0]);
  return td;
}
