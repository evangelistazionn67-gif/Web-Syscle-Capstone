// REPORTS: sales per day / month / year, compared with the previous period of the same length, plus per-item and breakdown tables.
(async function () {
  const $ = id => document.getElementById(id), body = $('rp-body');
  let period = 'daily', data = null;
  const pct = (c, p) => { c = +c; p = +p; if (!p) return c ? '<span class="sy-up">new</span>' : '<span class="sy-muted">-</span>'; const v = Math.round((c - p) / p * 100); return `<span class="${v >= 0 ? 'sy-up' : 'sy-down'}">${v >= 0 ? '<i class="bi bi-caret-up-fill" aria-hidden="true"></i>' : '<i class="bi bi-caret-down-fill" aria-hidden="true"></i>'} ${Math.abs(v)}%</span>`; };
  const hbars = (rows, label, val, fmt) => { const m = Math.max(1, ...rows.map(r => +r[val])); return rows.length ? rows.map(r => `<div class="sy-hbar"><span title="${esc(r[label])}">${esc(r[label])}</span><div class="tr"><i style="width:${+r[val] / m * 100}%"></i></div><b>${fmt(r[val])}</b></div>`).join('') : '<div class="sy-empty">No data.</div>'; };
  const wkStart = l => { const [y, w] = l.split('-W').map(Number); const d = new Date(y, 0, 4); d.setDate(d.getDate() - ((d.getDay() + 6) % 7) + (w - 1) * 7); return d; };
  const fmtLabel = l => period === 'weekly' ? 'Week of ' + wkStart(l).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) : period === 'daily' ? new Date(l + 'T00:00').toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) : period === 'monthly' ? new Date(l + '-01T00:00').toLocaleDateString(undefined, { month: 'short', year: '2-digit' }) : l;
  function paint() {
    const d = data, c = d.current, p = d.previous, avg = x => x.orders ? x.sales / x.orders : 0;
    $('rp-range').textContent = `Showing ${d.from} to ${d.to}. Compared with ${d.prevFrom} to the day before ${d.from}.`;
    const ser = fillSeries(d.series, period, d.from, d.to), max = Math.max(1, ...ser.map(s => +s.sales));
    const chart = ser.some(s => +s.sales > 0) ? `<div class="sy-bars">${ser.map(s => `<div class="sy-bar" style="height:${Math.max(4, +s.sales / max * 100)}%" data-tip="${esc(fmtLabel(s.label))}: ${peso(s.sales)} (${s.orders} orders)"></div>`).join('')}</div><div class="sy-barlbl">${ser.length <= 31 ? ser.map(s => `<span>${esc(fmtLabel(s.label))}</span>`).join('') : ''}</div>` : '<div class="sy-empty">No sales in this range.</div>';
    const totalItems = d.byItem.reduce((s, r) => s + +r.sales, 0) || 1;
    body.innerHTML = `<div class="sy-cards">
        <div class="sy-stat hot">Sales<b>${peso(c.sales)}</b><small>${pct(c.sales, p.sales)} vs previous</small></div>
        <div class="sy-stat">Orders<b>${c.orders}</b><small>${pct(c.orders, p.orders)} vs previous</small></div>
        <div class="sy-stat">Items sold<b>${c.items}</b><small>${pct(c.items, p.items)} vs previous</small></div>
        <div class="sy-stat">Average order<b>${peso(avg(c))}</b><small>${pct(avg(c), avg(p))} vs previous</small></div></div>
      <div class="sy-card"><h3>Sales per ${period === 'daily' ? 'day' : period === 'weekly' ? 'week' : period === 'monthly' ? 'month' : 'year'}</h3>${chart}</div>
      <div class="sy-card"><h3>Comparison: this period vs previous period</h3><div class="sy-scroll"><table class="sy-table" style="box-shadow:none;border:0"><thead><tr><th>Measure</th><th class="r">This period</th><th class="r">Previous period</th><th class="r">Change</th></tr></thead><tbody>
        <tr><td>Sales</td><td class="r">${peso(c.sales)}</td><td class="r">${peso(p.sales)}</td><td class="r">${pct(c.sales, p.sales)}</td></tr>
        <tr><td>Orders</td><td class="r">${c.orders}</td><td class="r">${p.orders}</td><td class="r">${pct(c.orders, p.orders)}</td></tr>
        <tr><td>Items sold</td><td class="r">${c.items}</td><td class="r">${p.items}</td><td class="r">${pct(c.items, p.items)}</td></tr>
        <tr><td>Average order value</td><td class="r">${peso(avg(c))}</td><td class="r">${peso(avg(p))}</td><td class="r">${pct(avg(c), avg(p))}</td></tr></tbody></table></div></div>
      <div class="sy-dash">
        <div class="sy-card sy-c8"><h3>Sales per item</h3><div class="sy-scroll"><table class="sy-table" style="box-shadow:none;border:0;min-width:480px"><thead><tr><th>Item</th><th class="r">Qty</th><th class="r">Sales</th><th>Share</th></tr></thead><tbody>${d.byItem.length ? d.byItem.map(r => `<tr><td>${esc(r.name)}</td><td class="r">${r.qty}</td><td class="r">${peso(r.sales)}</td><td style="min-width:110px"><div class="sy-hbar" style="grid-template-columns:1fr auto;padding:0"><div class="tr"><i style="width:${+r.sales / totalItems * 100}%"></i></div><small>${Math.round(+r.sales / totalItems * 100)}%</small></div></td></tr>`).join('') : '<tr><td colspan="4" class="sy-empty">No items sold.</td></tr>'}</tbody></table></div></div>
        <div class="sy-card sy-c4"><h3>Top buyers</h3>${d.buyers.length ? d.buyers.map((b, i) => `<div class="sy-rank"><span class="n">${i + 1}</span><div class="sy-grow"><b>${esc(b.username)}</b><small>${b.orders} order${b.orders > 1 ? 's' : ''}</small></div><b>${peso(b.spent)}</b></div>`).join('') : '<div class="sy-empty">No buyers.</div>'}</div>
        <div class="sy-card sy-c4"><h3>By status</h3>${hbars(d.byStatus, 'status', 'count', v => v)}</div>
        <div class="sy-card sy-c4"><h3>By payment method</h3>${hbars(d.byPayment, 'method', 'total', peso)}</div>
        <div class="sy-card sy-c4"><h3>Online vs walk-in</h3>${hbars(d.byChannel, 'channel', 'total', peso)}<h3 style="margin-top:16px">Promo codes used</h3>${d.promos.length ? d.promos.map(r => `<div class="sy-row"><span><b>${esc(r.code)}</b> x ${r.uses}</span><span>-${peso(r.given)}</span></div>`).join('') : '<div class="sy-empty">No promo codes used.</div>'}</div>
      </div>`;
  }
  async function load() {
    const qs = new URLSearchParams({ period }); if ($('rp-from').value) qs.set('from', $('rp-from').value); if ($('rp-to').value) qs.set('to', $('rp-to').value);
    try { data = await api('/api/admin/reports?' + qs); paint(); } catch (e) { body.innerHTML = '<div class="sy-empty">' + esc(e.message) + '</div>'; }
  }
  $('rp-seg').addEventListener('click', e => { const b = e.target.closest('[data-p]'); if (!b) return; period = b.dataset.p; $('rp-seg').querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b)); $('rp-from').value = $('rp-to').value = ''; load(); });
  $('rp-go').addEventListener('click', load);
  $('rp-reset').addEventListener('click', () => { $('rp-from').value = $('rp-to').value = ''; load(); });
  $('rp-print').addEventListener('click', () => window.print());
  $('rp-csv').addEventListener('click', () => {
    if (!data) return; const q = v => '"' + String(v ?? '').replace(/"/g, '""') + '"', L = [];
    L.push(['Period', period, data.from, data.to].map(q).join(',')); L.push(''); L.push('Label,Sales,Orders'); data.series.forEach(s => L.push([s.label, s.sales, s.orders].map(q).join(',')));
    L.push(''); L.push('Item,Qty,Sales'); data.byItem.forEach(r => L.push([r.name, r.qty, r.sales].map(q).join(',')));
    L.push(''); L.push('Measure,This period,Previous period'); [['Sales', 'sales'], ['Orders', 'orders'], ['Items', 'items']].forEach(([n, k]) => L.push([n, data.current[k], data.previous[k]].map(q).join(',')));
    const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + L.join('\r\n')], { type: 'text/csv' })); a.download = `sales-report-${period}-${data.from}.csv`; a.click(); URL.revokeObjectURL(a.href);
  });
  load(); initHeader(null);
})();
