// STOCK MOVEMENT: a log of every change to stock (sales, cancellations, restocks, edits).
(async function () {
  const $ = id => document.getElementById(id); let rows = [], q = '';
  const tone = r => /cancel|restock|new item/i.test(r) ? 'ok' : /deleted|removed/i.test(r) ? 'bad' : /edited/i.test(r) ? 'info' : 'warn';
  async function load() {
    const qs = new URLSearchParams(); [['search', q], ['reason', $('sm-reason').value], ['startDate', $('sm-from').value], ['endDate', $('sm-to').value]].forEach(([k, v]) => { if (v) qs.set(k, v); });
    try { rows = await api('/api/admin/stock-movements?' + qs); } catch (e) { return toast(e.message, true); }
    const inn = rows.filter(r => r.delta > 0).reduce((s, r) => s + r.delta, 0), out = rows.filter(r => r.delta < 0).reduce((s, r) => s - r.delta, 0);
    $('sm-n').textContent = rows.length; $('sm-in').textContent = '+' + inn; $('sm-out').textContent = '-' + out; $('sm-net').textContent = (inn - out > 0 ? '+' : '') + (inn - out);
    $('sm-body').innerHTML = rows.length ? rows.map(r => `<tr><td>${fmtDateTime(r.date)}</td><td><b>${esc(r.product)}</b></td><td><span class="sy-chip ${tone(r.reason)}">${esc(r.reason)}</span></td><td>${esc(r.reference || '-')}</td>
      <td class="r"><b class="${r.delta > 0 ? 'sy-ok' : 'sy-red'}">${r.delta > 0 ? '+' : ''}${r.delta}</b></td><td class="r">${r.stockAfter}</td><td>${esc(r.byUser || (r.reason === 'Online order' ? 'Customer' : '-'))}</td></tr>`).join('') : '<tr><td colspan="7" class="sy-empty">No stock movements match.</td></tr>';
  }
  $('sm-go').addEventListener('click', load); $('sm-reason').addEventListener('change', load); $('sm-from').addEventListener('change', load); $('sm-to').addEventListener('change', load);
  $('sm-reset').addEventListener('click', () => { q = ''; const i = $('main-search-input'); if (i) i.value = ''; $('sm-reason').value = $('sm-from').value = $('sm-to').value = ''; load(); });
  $('sm-csv').addEventListener('click', () => {
    if (!rows.length) return toast('Nothing to export.', true); const e = v => '"' + String(v ?? '').replace(/"/g, '""') + '"';
    const csv = ['Date,Item,Reason,Order,Change,Stock after,By'].concat(rows.map(r => [fmtDateTime(r.date), r.product, r.reason, r.reference || '', r.delta, r.stockAfter, r.byUser || ''].map(e).join(','))).join('\r\n');
    const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' })); a.download = 'stock-movement.csv'; a.click(); URL.revokeObjectURL(a.href);
  });
  initHeader(t => { q = t; clearTimeout(window.__smT); window.__smT = setTimeout(load, 300); }); load();
})();
