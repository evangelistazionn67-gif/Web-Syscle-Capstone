// ADMIN DASHBOARD: what needs attention now, today's numbers, a 14-day chart, top buyers, recent orders and low stock.
(async function () {
  const box = document.getElementById('admin-stats');
  const delta = (cur, prev) => { if (!prev) return cur ? '<small class="sy-up">New activity</small>' : '<small class="sy-muted">No sales yesterday</small>'; const p = Math.round((cur - prev) / prev * 100); return `<small class="${p >= 0 ? 'sy-up' : 'sy-down'}">${p >= 0 ? ico('caret-up-fill') : ico('caret-down-fill')} ${Math.abs(p)}% vs yesterday</small>`; };
  let d, rep = null, buyers = [], unread = 0, trend = null;
  try {
    const r = await Promise.all([api('/api/admin/stats'), api('/api/admin/reports?period=daily').catch(() => null), api('/api/admin/top-buyers?limit=5').catch(() => []), api('/api/admin/messages/unread').catch(() => ({ count: 0 })), api('/api/admin/trend').catch(() => null)]);
    d = r[0]; rep = r[1]; buyers = r[2]; unread = r[3].count; trend = r[4];
  } catch (e) { box.innerHTML = '<div class="sy-empty">Could not load the dashboard numbers.</div>'; return; }
  const o = d.orders, p = d.prods, me = await SY_READY;
  const alerts = [];
  if (o.pendingOrders) alerts.push(`<a class="sy-alert warn" href="admin_transaction_history.html"><span>${ico('bell')}</span><span><b>${o.pendingOrders} new order${o.pendingOrders > 1 ? 's' : ''}</b> waiting to be prepared.</span><span class="go">Open orders</span></a>`);
  if (p.outOfStock) alerts.push(`<a class="sy-alert bad" href="admin_inventory.html"><span>${ico('exclamation-triangle')}</span><span><b>${p.outOfStock} item${p.outOfStock > 1 ? 's are' : ' is'} out of stock.</b> Customers cannot buy them.</span><span class="go">Restock</span></a>`);
  if (p.lowStock) alerts.push(`<a class="sy-alert warn" href="admin_inventory.html"><span>${ico('box-seam')}</span><span><b>${p.lowStock} item${p.lowStock > 1 ? 's are' : ' is'} running low</b> (at or below the low-stock level).</span><span class="go">View</span></a>`);
  if (o.refundRequests) alerts.push(`<a class="sy-alert bad" href="admin_transaction_history.html"><span>${ico('arrow-counterclockwise')}</span><span><b>${o.refundRequests} refund request${o.refundRequests > 1 ? 's' : ''}</b> waiting for your answer.</span><span class="go">Answer</span></a>`);
  if (unread) alerts.push(`<a class="sy-alert info" href="admin_messages.html"><span>${ico('chat-dots')}</span><span><b>${unread} unread message${unread > 1 ? 's' : ''}</b> from customers.</span><span class="go">Reply</span></a>`);
  const series = rep ? fillSeries(rep.series, 'daily', rep.from, rep.to) : [], max = Math.max(1, ...series.map(s => +s.sales));
  const chart = series.some(s => +s.sales > 0) ? `<div class="sy-bars">${series.map(s => `<div class="sy-bar" style="height:${Math.max(4, +s.sales / max * 100)}%" data-tip="${esc(s.label)}: ${peso(s.sales)}"></div>`).join('')}</div><div class="sy-barlbl">${series.map(s => `<span>${esc(s.label.slice(8))}</span>`).join('')}</div>` : '<div class="sy-empty">No sales in the last 14 days yet.</div>';
  const sg = n => (n < 0 ? '-' : '') + peso(Math.abs(n));
  const verdictCls = trend ? (trend.verdict === 'earnings' ? 'ok' : trend.verdict === 'loss' ? 'bad' : 'warn') : 'mute';
  const tmax = trend ? Math.max(1, ...trend.days.map(x => +x.sales), ...trend.forecast.map(x => +x.sales)) : 1;
  const trendCard = trend ? `<div class="sy-card sy-c12 sy-trend"><div class="sy-toolbar" style="margin-bottom:8px"><h3 style="margin:0">Last 14 days and the next 7 days</h3><span class="sy-chip ${verdictCls}">${trend.verdict === 'earnings' ? 'Expected: earnings' : trend.verdict === 'loss' ? 'Expected: loss' : 'Expected: break-even'}</span></div>
      <div class="sy-cards" style="margin-bottom:12px">
        <div class="sy-stat">Today<b>${peso(trend.todaySales)}</b><small class="${trend.todayVsAvgPct == null ? 'sy-muted' : trend.todayVsAvgPct >= 0 ? 'sy-up' : 'sy-down'}">${trend.todayVsAvgPct == null ? 'No 14-day average yet' : (trend.todayVsAvgPct >= 0 ? '+' : '') + trend.todayVsAvgPct + '% vs the 14-day average (' + peso(trend.avg14) + ')'}</small></div>
        <div class="sy-stat">Next 7 days (sales)<b>${peso(trend.next7)}</b><small class="sy-muted">trend ${trend.trendPct >= 0 ? '+' : ''}${trend.trendPct}%</small></div>
        <div class="sy-stat ${trend.next7Profit < 0 ? '' : 'hot'}">Next 7 days (profit)<b>${sg(trend.next7Profit)}</b><small class="sy-muted">${trend.marginPct}% margin ${trend.marginFromCosts ? 'from cost prices' : '(store default)'}, ${peso(trend.expenses)}/day costs</small></div>
      </div>
      <div class="sy-bars sy-bars2">${trend.days.map(x => `<div class="sy-bar" style="height:${Math.max(4, +x.sales / tmax * 100)}%" data-tip="${esc(x.label)}: ${peso(x.sales)} sales, ${sg(x.profit)} profit"></div>`).join('')}${trend.forecast.map(x => `<div class="sy-bar fc" style="height:${Math.max(4, +x.sales / tmax * 100)}%" data-tip="${esc(x.label)} (forecast): ${peso(x.sales)} sales, ${sg(x.profit)} profit"></div>`).join('')}</div>
      <p class="sy-sub" style="margin:8px 0 0">Solid bars are real sales (paid and confirmed orders only). Light bars are the forecast. It is an estimate from your own history${trend.confidence === 'low' ? ', and there is little history yet, so treat it with care' : ''}, not a promise.</p></div>` : '';
  box.innerHTML = `<div class="sy-toolbar"><div><h1>Dashboard</h1><p class="sy-sub" style="margin:0">Hello ${esc(me ? me.username : '')}. Here is how the shop is doing.</p></div>
      <div class="sy-pactions"><a class="sy-btn accent" href="admin_transaction.html">${ico('upc-scan')} Walk-in sale</a><a class="sy-btn ghost" href="admin_inventory.html?add=1">${ico('plus-lg')} Add item</a><a class="sy-btn ghost" href="admin_reports.html">Reports</a></div></div>
    ${alerts.length ? `<div class="sy-alerts">${alerts.join('')}</div>` : `<div class="sy-alerts"><div class="sy-alert info"><span>${ico('check-circle')}</span><span><b>All clear.</b> No orders waiting and stock looks fine.</span></div></div>`}
    <div class="sy-cards">
      <div class="sy-stat hot">Today's sales<b>${peso(o.todaySales)}</b>${delta(+o.todaySales, +o.yesterdaySales)}</div>
      <div class="sy-stat">Orders today<b>${o.todayOrders}</b><small class="sy-muted">${o.openOrders || 0} still open</small></div>
      <div class="sy-stat">This month<b>${peso(o.monthSales || 0)}</b></div>
      <div class="sy-stat dark">All-time sales<b>${peso(o.totalSales)}</b></div>
      <div class="sy-stat">Products<b>${p.products}</b><small class="sy-muted">${d.customers} customers</small></div>
    </div>
    <div class="sy-dash">
      ${trendCard}
      <div class="sy-card sy-c8"><h3>Sales, last 14 days</h3>${chart}</div>
      <div class="sy-card sy-c4"><h3>Top buyers</h3>${buyers.length ? buyers.map((b, i) => `<div class="sy-rank"><span class="n">${i + 1}</span><div class="sy-grow"><b>${esc(b.username)}</b><small>${b.orders} order${b.orders > 1 ? 's' : ''}</small></div><b>${peso(b.spent)}</b></div>`).join('') : '<div class="sy-empty">No buyers yet.</div>'}<p style="margin-top:10px"><a href="admin_promos.html">Reward them with a promo code <i class="bi bi-arrow-right" aria-hidden="true"></i></a></p></div>
      <div class="sy-card sy-c8"><h3>Recent orders</h3><div class="sy-scroll"><table class="sy-table" style="box-shadow:none;border:0;min-width:480px"><tbody>${d.recent.length ? d.recent.map(r => `<tr><td><b>${esc(r.code)}</b></td><td>${esc(r.customer)}</td><td class="r">${peso(r.total)}</td><td>${statusChip(r.status)}</td></tr>`).join('') : '<tr><td class="sy-empty">No orders yet.</td></tr>'}</tbody></table></div><p style="margin-top:10px"><a href="admin_transaction_history.html">Manage all orders <i class="bi bi-arrow-right" aria-hidden="true"></i></a></p></div>
      <div class="sy-card sy-c4"><h3>Needs restocking</h3>${d.low.length ? d.low.map(i => `<div class="sy-row"><a href="admin_inventory.html?cat=${i.categoryId}">${esc(i.name)}</a><b class="${i.stock === 0 ? 'sy-red' : ''}">${i.stock === 0 ? 'Out' : i.stock + ' left'}</b></div>`).join('') : '<div class="sy-empty">Everything is well stocked.</div>'}</div>
    </div>`;
  initHeader(null);
})();
