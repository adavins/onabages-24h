// @ts-check
// El pla d'una campanya (fase 4, pas 3d-2; D-129): el resum per falca (han sonat, previstos, total i contractats), els
// avisos, un calendari mensual amb els passis de cada dia i la llegenda. Es fa servir a l'editor de la campanya
// («On sona i pla de passis») i al calendari de la campanya. Només mostra el que diu campaigns.plan.

import { el, icon, statusLabel } from './dom.js';
import { DAY_SHORT, DAY_NAMES, monthText } from '../calendar-form.js';
import { planSummary, monthWeeks, dmy } from '../campaign-form.js';

/**
 * El pla, de dalt a baix (D-129): el resum en una taula (per falca: han sonat, previstos i total; i el total respecte
 * als contractats), els avisos, el calendari mensual (a tota l'amplada) i, a sota, la llegenda dels símbols.
 * @param {any} plan  de campaigns.plan
 * @param {any} campaign  la campanya (startDate, endDate, contractedTotal, contractedPerDay, effectiveState)
 * @param {string} today  'AAAA-MM-DD'
 * @param {{ months?: boolean }} [options]  months: amb el calendari mensual (per defecte, sí)
 */
export function planPanel(plan, campaign, today, { months = true } = {}) {
  const s = planSummary(plan, campaign, today);
  const box = el('div', 'cp-plan-panel');

  // Requadre «Resum» (D-130): la taula i, a sota, els avisos
  const summary = [];
  if (campaign.effectiveState !== 'ACTIVA') {
    summary.push(statusLabel('text-body-secondary small d-block', 'bi-info-circle', 'Només les campanyes actives tenen passis previstos.'));
  }
  summary.push(summaryTable(plan, s, campaign));
  if (s.warnings.length > 0) {
    const list = el('ul', 'cp-plan-warnings');
    for (const w of s.warnings) {
      const li = el('li', '');
      li.append(statusLabel('text-warning', 'bi-exclamation-triangle-fill', w));
      list.append(li);
    }
    summary.push(el('h5', 'cp-plan-sub', 'Avisos'), list);
  }
  box.append(section('Resum', 'bi-table', summary));

  if (months) {
    box.append(section('Calendari', 'bi-calendar3', [monthCalendars(s, campaign, today)]));
    box.append(section('Llegenda', 'bi-info-circle', [legend(campaign.contractedPerDay ?? null)]));
  }
  return box;
}

/**
 * Un requadre del pla, amb el títol a dalt (D-130).
 * @param {string} title @param {string} iconName @param {(HTMLElement | string)[]} children
 */
function section(title, iconName, children) {
  const box = el('section', 'cp-plan-box');
  box.setAttribute('aria-label', title);
  const h = el('h4', 'cp-plan-h');
  h.append(icon(iconName), ` ${title}`);
  const body = el('div', 'cp-plan-box-body');
  body.append(...children);
  box.append(h, body);
  return box;
}

/**
 * Taula del resum: una fila per falca (han sonat, previstos i total), la fila del total i, si n'hi ha, els contractats
 * i la diferència.
 * @param {any} plan @param {ReturnType<typeof planSummary>} s @param {any} campaign
 */
function summaryTable(plan, s, campaign) {
  const table = el('table', 'cp-plan-table');
  const head = el('tr', '');
  const th = (/** @type {string} */ text, /** @type {string} */ css = '') => el('th', css, text);
  const playedHead = el('th', 'cp-num');
  playedHead.append(icon('bi-check-circle-fill'), ' Han sonat');
  const plannedHead = el('th', 'cp-num');
  plannedHead.append(icon('bi-calendar-event'), ' Previstos');
  head.append(th('Falca'), playedHead, plannedHead, th('Total', 'cp-num'));
  const thead = el('thead', '');
  thead.append(head);

  const row = (/** @type {string} */ name, /** @type {number} */ played, /** @type {number} */ planned, /** @type {string} */ css = '') => {
    const tr = el('tr', css);
    tr.append(el('td', 'cp-plan-name', name), el('td', 'cp-num', String(played)), el('td', 'cp-num', String(planned)),
      el('td', 'cp-num', String(played + planned)));
    return tr;
  };
  const tbody = el('tbody', '');
  for (const spot of plan.bySpot) tbody.append(row(spot.title ?? `Pista ${spot.trackId}`, spot.played ?? 0, spot.count));
  if (s.empty > 0) tbody.append(row('Buits (cap falca vàlida)', 0, s.empty, 'text-warning'));

  const tfoot = el('tfoot', '');
  tfoot.append(row('Total', s.played, s.planned, 'cp-plan-total'));
  const contracted = campaign.contractedTotal ?? null;
  if (contracted != null) {
    const tr = el('tr', '');
    tr.append(el('td', 'cp-plan-name', 'Contractats'), el('td', '', ''), el('td', '', ''), el('td', 'cp-num', String(contracted)));
    tfoot.append(tr);
    const diff = s.total - contracted;
    const d = el('tr', diff === 0 ? 'text-success' : 'text-warning');
    const label = el('td', 'cp-plan-name');
    label.append(icon(diff === 0 ? 'bi-check-lg' : 'bi-exclamation-triangle-fill'), diff === 0 ? ' Quadra' : diff > 0 ? ' De més' : ' De menys');
    d.append(label, el('td', '', ''), el('td', '', ''), el('td', 'cp-num', String(Math.abs(diff))));
    tfoot.append(d);
  }
  if (campaign.contractedPerDay != null) {
    const tr = el('tr', '');
    tr.append(el('td', 'cp-plan-name', 'Contractats per dia'), el('td', '', ''), el('td', '', ''), el('td', 'cp-num', String(campaign.contractedPerDay)));
    tfoot.append(tr);
  }
  table.append(thead, tbody, tfoot);
  return table;
}

/** La llegenda dels símbols del calendari mensual, un per línia. @param {number | null} perDay */
function legend(perDay) {
  const list = el('ul', 'cp-plan-legend');
  /** @param {HTMLElement | string} sample @param {string} text */
  const item = (sample, text) => {
    const li = el('li', '');
    li.append(sample, el('span', '', text));
    return li;
  };
  const sample = (/** @type {string} */ css, /** @type {string} */ iconName, /** @type {string} */ text) => {
    const span = el('span', `cpm-sample ${css}`);
    span.append(icon(iconName), ` ${text}`);
    return span;
  };
  list.append(
    item(sample('', 'bi-check-circle-fill', '2'), 'Dia que ja ha passat: els passis que han sonat de debò.'),
    item(sample('', 'bi-calendar-event', '2'), 'Avui o més endavant: els passis previstos.'),
    item(sample('text-warning', 'bi-exclamation-triangle-fill', '1'), perDay != null
      ? `No són els ${perDay} passis per dia contractats, o algun passi queda buit.`
      : 'Algun passi queda buit (aquell dia no hi ha cap falca vàlida).'),
    item(el('span', 'cpm-sample cpm-sample-today', '14'), 'Avui.'),
    item(el('span', 'cpm-sample cpm-sample-out', '30'), 'Fora de les dates de la campanya.'),
  );
  const help = el('li', 'form-text');
  help.textContent = 'Passa el ratolí per sobre d\'un dia per veure l\'hora i la falca de cada passi.';
  list.append(help);
  return list;
}

/**
 * El mes que es veu del calendari de cada campanya ('AAAA-MM'), perquè es mantingui quan la vista es torna a dibuixar
 * (p. ex. en marcar una casella). Només mentre l'aplicació és oberta.
 * @type {Map<number, string>}
 */
const shownMonth = new Map();

/** 'AAAA-MM' més (o menys) n mesos. @param {string} ym @param {number} n */
function addMonths(ym, n) {
  const [y, m] = ym.split('-').map(Number);
  const total = y * 12 + (m - 1) + n;
  return `${Math.floor(total / 12)}-${String((total % 12) + 1).padStart(2, '0')}`;
}

/**
 * El calendari mensual de la campanya (D-131): un sol mes, amb botons per anar al mes anterior i al següent (només
 * dins de les dates de la campanya). Per defecte, el mes d'avui si és dins de la campanya; si no, el primer (o l'últim,
 * si ja s'ha acabat). Cada dia, quants passis hi ha (els que han sonat, si ja ha passat; els previstos, si encara ha
 * d'arribar), amb avís si no són els contractats per dia o si n'hi ha de buits.
 * @param {ReturnType<typeof planSummary>} s @param {any} campaign @param {string} today
 */
function monthCalendars(s, campaign, today) {
  const wrap = el('div', 'cpm-wrap');
  if (!campaign.startDate || !campaign.endDate) return wrap;
  const first = campaign.startDate.slice(0, 7);
  const last = campaign.endDate.slice(0, 7);
  const now = today.slice(0, 7);
  const fallback = now < first ? first : now > last ? last : now;
  const saved = shownMonth.get(campaign.id);
  let current = saved && saved >= first && saved <= last ? saved : fallback;

  const draw = () => {
    shownMonth.set(campaign.id, current);
    const nav = el('div', 'cpm-nav');
    const button = (/** @type {number} */ step, /** @type {string} */ iconName, /** @type {string} */ text) => {
      const b = /** @type {HTMLButtonElement} */ (el('button', 'btn btn-sm btn-outline-secondary'));
      b.type = 'button';
      b.title = text;
      b.setAttribute('aria-label', text);
      b.append(icon(iconName));
      const target = addMonths(current, step);
      b.disabled = target < first || target > last;
      b.addEventListener('click', () => {
        current = target;
        draw();
      });
      return b;
    };
    const title = el('span', 'cpm-title', monthText(current));
    title.setAttribute('aria-live', 'polite');
    nav.append(button(-1, 'bi-chevron-left', 'Mes anterior'), title, button(1, 'bi-chevron-right', 'Mes següent'));
    if (first !== last) {
      // Quin mes és dels de la campanya (p. ex. «2 de 3»)
      const index = (Number(current.slice(0, 4)) - Number(first.slice(0, 4))) * 12 + Number(current.slice(5)) - Number(first.slice(5)) + 1;
      const count = (Number(last.slice(0, 4)) - Number(first.slice(0, 4))) * 12 + Number(last.slice(5)) - Number(first.slice(5)) + 1;
      nav.append(el('span', 'cpm-position form-text m-0', `${index} de ${count}`));
    }

    const [year, month] = current.split('-').map(Number);
    const table = el('table', 'cpm');
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
      for (const date of week) tr.append(dayCell(date, s, campaign, campaign.contractedPerDay ?? null, today));
      tbody.append(tr);
    }
    table.append(thead, tbody);
    wrap.replaceChildren(nav, table);
  };
  draw();
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
