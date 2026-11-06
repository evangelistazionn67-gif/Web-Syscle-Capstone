// LISTS AND RULES: dropdown lists (item types, size sets, colors, bike types), promo seasons, store rules and the activity log.
(async function () {
  const $ = id => document.getElementById(id);
  const tabs = $('ls-tabs'), done = {};
  const RULES = [
    ['AdminIdleSeconds', 'Admin auto sign-out (seconds)', 'The admin portal signs out after this many seconds without a click or key press. Use 30 for a demo, 300 for daily use.', 30, 3600],
    ['LowStockThreshold', 'Low-stock level (pieces)', 'An item at or below this number is shown as "Low".', 0, 1000],
    ['UnpaidOnlineHours', 'Hold an unpaid online order (hours)', 'After this time an online order that was never paid is cancelled and its items go back to stock.', 1, 168],
    ['PickupHoldDays', 'Hold a ready pick-up order (days)', 'A Ready pick-up order that nobody collects is cancelled after this many days and its items go back to stock.', 1, 30],
    ['RefundWindowDays', 'Refund window (days)', 'How many days after completion a customer may ask for a refund.', 0, 90],
    ['DailyExpenses', 'Daily running costs (PHP)', 'Used by the dashboard to estimate profit or loss.', 0, 10000000],
    ['DefaultMarginPct', 'Default profit margin (%)', 'Used when items have no cost price yet.', 1, 90],
    ['PromoPopup', 'Promo pop-up for customers (1 = on, 0 = off)', 'Shows live public promo codes and bundle deals when a customer opens the shop.', 0, 1],
    ['MaxFeatured', 'Most featured items', 'How many items can be featured on the shop home page.', 1, 50]
  ];
  const KINDS = [['ItemType', 'Item types', 'Wheel, Tire, Saddle...'], ['BikeType', 'Bike types', 'Mountain, Road...'], ['Color', 'Colors', 'Black, Red...'], ['SizeSet', 'Size sets', 'Sizes, comma separated']];

  async function lists() {
    const rows = await api('/api/admin/lookups');
    $('t-lists').innerHTML = '<div class="sy-grid2">' + KINDS.map(([k, label, ph]) => `<div class="sy-card"><h3>${label}</h3>
      <div class="sy-chips">${rows.filter(r => r.kind === k).map(r => `<span class="sy-chip info">${esc(r.value)}${r.extra ? ' <small>(' + esc(r.extra) + ')</small>' : ''} <button type="button" class="sy-chipx" data-del="${r.id}" aria-label="Remove ${esc(r.value)}">${ico('x-lg')}</button></span>`).join('') || '<span class="sy-muted">None yet.</span>'}</div>
      <div class="sy-pactions" style="margin-top:12px" data-kind="${k}"><input class="sy-input" maxlength="60" placeholder="${ph}" aria-label="New ${label}" ${k === 'SizeSet' ? 'data-name' : 'data-val'}>${k === 'SizeSet' ? '<input class="sy-input" maxlength="200" placeholder="26,27.5,29" data-ext aria-label="Sizes">' : ''}<button class="sy-btn small" type="button" data-add="1">Add</button></div></div>`).join('') + '</div>';
  }
  async function seasons() {
    const rows = await api('/api/admin/seasons');
    $('t-seasons').innerHTML = `<p class="sy-sub">Seasonal promo codes and bundles only work while their season is live. The dates repeat every year and are checked automatically, so nobody has to switch codes on or off by hand. Yearly dates are MM-DD (for example 12-01); "Easter" seasons use days before or after Easter Sunday; monthly seasons use a day of the month.</p>
      <div class="sy-scroll"><table class="sy-table"><thead><tr><th>Season</th><th>Type</th><th>Starts</th><th>Ends</th><th>Next window</th><th>On</th><th></th></tr></thead><tbody>${rows.map(s => `<tr data-n="${esc(s.name)}"><td><b>${esc(s.name)}</b> ${s.live ? '<span class="sy-chip ok">Live now</span>' : ''}</td><td>${esc(s.kind)}</td>
        <td><input class="sy-input" style="width:90px" value="${esc(s.startMD)}" data-s aria-label="Start"></td><td><input class="sy-input" style="width:90px" value="${esc(s.endMD)}" data-e aria-label="End"></td><td>${esc(s.start)} to ${esc(s.end)}</td>
        <td><input type="checkbox" data-on ${s.isOn ? 'checked' : ''} aria-label="On"></td><td><button class="sy-btn small" data-save="1">Save</button> <button class="sy-x small" data-rm="1">Delete</button></td></tr>`).join('')}</tbody></table></div>
      <div class="sy-card" style="margin-top:14px"><h3>Add a season</h3><div class="sy-pactions" id="ns"><input class="sy-input" id="ns-name" maxlength="30" placeholder="Name, e.g. Tour de Bike"><select id="ns-kind" class="sy-input"><option value="yearly">Yearly (MM-DD)</option><option value="monthly">Monthly (day 1-31)</option><option value="easter">Around Easter (days)</option></select>
        <input id="ns-s" class="sy-input" style="width:100px" placeholder="Start"><input id="ns-e" class="sy-input" style="width:100px" placeholder="End"><button class="sy-btn" id="ns-add" type="button">Add season</button></div></div>`;
  }
  async function rules() {
    const v = await api('/api/admin/rules');
    $('t-rules').innerHTML = `<div class="sy-card"><div class="sy-rules">${RULES.map(([k, l, h, mn, mx]) => `<div><label for="r-${k}">${l}</label><input id="r-${k}" class="sy-input" type="number" min="${mn}" max="${mx}" value="${esc(v[k])}"><small class="sy-muted">${h}</small></div>`).join('')}</div>
      <div class="sy-modal-actions"><button class="sy-btn accent" id="r-save" type="button">Save rules</button></div></div>`;
  }
  async function audit() {
    const rows = await api('/api/admin/audit');
    $('t-audit').innerHTML = `<p class="sy-sub">The last 200 sensitive actions: sign-ins to admin tools, payments, refunds, rule changes. It cannot be edited from the website.</p><div class="sy-scroll"><table class="sy-table"><thead><tr><th>When</th><th>Who</th><th>Action</th><th>Detail</th></tr></thead><tbody>${rows.map(r => `<tr><td>${fmtDateTime(r.date)}</td><td>${esc(r.byUser || 'system')}</td><td>${esc(r.action)}</td><td>${esc(r.detail || '')}</td></tr>`).join('') || '<tr><td colspan="4" class="sy-empty">Nothing yet.</td></tr>'}</tbody></table></div>`;
  }
  const LOAD = { lists, seasons, rules, audit };
  async function show(t) {
    tabs.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.t === t));
    document.querySelectorAll('.sy-tabsec').forEach(s => s.hidden = s.id !== 't-' + t);
    try { await LOAD[t](); } catch (e) { toast(e.message, true); }
  }
  tabs.addEventListener('click', e => { const b = e.target.closest('[data-t]'); if (b) show(b.dataset.t); });
  document.body.addEventListener('click', async e => {
    const b = e.target.closest('button'); if (!b) return;
    try {
      if (b.dataset.del) { await api('/api/admin/lookups/' + b.dataset.del, 'DELETE'); await lists(); }
      else if (b.dataset.add) {
        const box = b.parentElement, kind = box.dataset.kind, name = box.querySelector('[data-val],[data-name]').value.trim(), ext = box.querySelector('[data-ext]');
        if (!name) return toast('Type a name first.', true);
        await api('/api/admin/lookups', 'POST', { kind, value: name, extra: ext ? ext.value.trim() : null }); await lists(); toast('Added.');
      }
      else if (b.dataset.save) { const tr = b.closest('tr'); await api('/api/admin/seasons/' + encodeURIComponent(tr.dataset.n), 'PUT', { isOn: tr.querySelector('[data-on]').checked, startMD: tr.querySelector('[data-s]').value.trim(), endMD: tr.querySelector('[data-e]').value.trim() }); await seasons(); toast('Season saved.'); }
      else if (b.dataset.rm) { const n = b.closest('tr').dataset.n; if (confirm('Delete the season ' + n + '? Codes tagged with it will stop working.')) { await api('/api/admin/seasons/' + encodeURIComponent(n), 'DELETE'); await seasons(); } }
      else if (b.id === 'ns-add') { await api('/api/admin/seasons', 'POST', { name: $('ns-name').value.trim(), kind: $('ns-kind').value, startMD: $('ns-s').value.trim(), endMD: $('ns-e').value.trim() }); await seasons(); toast('Season added.'); }
      else if (b.id === 'r-save') { const body = {}; RULES.forEach(([k]) => { body[k] = $('r-' + k).value; }); await api('/api/admin/rules', 'PUT', body); toast('Rules saved.'); }
    } catch (err) { toast(err.message, true); }
  });
  initHeader(null); show('lists');
})();
