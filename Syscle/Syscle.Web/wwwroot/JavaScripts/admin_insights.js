// PRODUCT INSIGHTS: fast/slow-moving products, a simple sales forecast and restock suggestions.
(async function () {
  const $ = id => document.getElementById(id);
  let movers = null, filter = '';
  const chip = s => `<span class="sy-chip ${s === 'Fast' ? 'ok' : s === 'Slow' ? 'warn' : s === 'New' ? 'info' : 'mute'}">${s === 'Fast' ? 'Fast-moving' : s === 'Slow' ? 'Slow-moving' : s}</span>`;
  function paintMovers() {
    const m = movers, rows = m.items.filter(x => !filter || x.status === filter);
    $('in-cards').innerHTML = `<div class="sy-stat">Fast-moving<b>${m.fast}</b><small>top sellers, last ${m.days} days</small></div><div class="sy-stat">Slow-moving<b>${m.slow}</b><small>in stock, barely selling</small></div><div class="sy-stat">Products<b>${m.items.length}</b></div>`;
    $('in-body').innerHTML = rows.length ? rows.map(x => `<tr data-id="${x.id}"><td><b>${esc(x.name)}</b><br><small class="sy-muted">${esc(x.itemType)}</small></td><td>${chip(x.status)}</td><td class="r">${x.sold}</td><td class="r">${x.stock}</td><td class="r">${x.daysLeft == null ? '-' : x.daysLeft}</td><td>${x.lastSold ? fmtDate(x.lastSold) : 'Never'}${x.status === 'Slow' ? `<br><small class="sy-muted">${x.idleDays} days ago</small>` : ''}</td>
      <td>${x.status === 'Slow' || x.status === 'New' ? `<div class="sy-actions"><button class="sy-btn small ${x.featured ? '' : 'ghost'}" data-feat="1">${x.featured ? '<i class="bi bi-star-fill" aria-hidden="true"></i> Featured' : '<i class="bi bi-star" aria-hidden="true"></i> Feature'}</button><a class="sy-btn small" href="admin_bundles.html">Make a bundle</a></div>` : '<span class="sy-muted">-</span>'}</td></tr>`).join('') : '<tr><td colspan="7" class="sy-empty">Nothing to show.</td></tr>';
  }
  async function loadMovers() { try { movers = await api('/api/admin/movers?days=' + $('in-days').value); paintMovers(); } catch (e) { $('in-body').innerHTML = '<tr><td colspan="7" class="sy-empty">' + esc(e.message) + '</td></tr>'; } }
  $('in-days').addEventListener('change', loadMovers);
  $('in-seg').addEventListener('click', e => { const b = e.target.closest('[data-s]'); if (!b) return; filter = b.dataset.s; $('in-seg').querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b)); if (movers) paintMovers(); });
  $('in-body').addEventListener('click', async e => {
    const b = e.target.closest('[data-feat]'); if (!b) return; const id = +b.closest('tr').dataset.id, it = movers.items.find(x => x.id === id);
    try { await api(`/api/admin/products/${id}/featured`, 'PUT', { featured: !it.featured }); it.featured = !it.featured; paintMovers(); toast(it.featured ? 'Featured on the shop homepage.' : 'Removed from Featured.'); } catch (err) { toast(err.message, true); }
  });
  async function loadForecast() {
    try {
      const f = await api('/api/admin/forecast'), conf = { low: 'Low (not much sales history yet)', fair: 'Fair', good: 'Good' }[f.confidence];
      $('fc-note').textContent = `An estimate from the weekday pattern of the last 8 weeks, adjusted by the recent trend. Confidence: ${conf}.`;
      $('fc-cards').innerHTML = `<div class="sy-stat hot">Next 7 days<b>${peso(f.next7)}</b></div><div class="sy-stat">Next 30 days<b>${peso(f.next30)}</b></div><div class="sy-stat">Average per day<b>${peso(f.avgDaily)}</b></div><div class="sy-stat">Trend<b class="${f.trendPct >= 0 ? 'sy-up' : 'sy-down'}">${f.trendPct >= 0 ? '+' : ''}${f.trendPct}%</b><small>last 4 weeks vs the 4 before</small></div>`;
      const all = [...f.history.map(h => ({ ...h, fc: 0 })), ...f.forecast.map(h => ({ ...h, fc: 1 }))], max = Math.max(1, ...all.map(x => +x.sales));
      $('fc-chart').innerHTML = `<div class="sy-bars">${all.map(x => `<div class="sy-bar" style="height:${Math.max(4, +x.sales / max * 100)}%;${x.fc ? 'opacity:.45;background:repeating-linear-gradient(45deg,var(--accent),var(--accent) 4px,transparent 4px,transparent 8px)' : ''}" data-tip="${esc(new Date(x.label + 'T00:00').toLocaleDateString(undefined, { month: 'short', day: 'numeric' }))}${x.fc ? ' (forecast)' : ''}: ${peso(x.sales)}"></div>`).join('')}</div><p class="sy-sub" style="margin:6px 0 0">Solid bars = actual sales (last 28 days). Striped bars = forecast (next 14 days).</p>`;
      $('fc-restock').innerHTML = f.restock.length ? f.restock.map(r => `<tr><td><b>${esc(r.name)}</b></td><td class="r">${r.perDay}</td><td class="r">${r.stock}</td><td class="r"><span class="sy-chip ${r.daysLeft <= 7 ? 'bad' : r.daysLeft <= 14 ? 'warn' : 'ok'}">${r.daysLeft} days</span></td><td class="r">${r.suggest > 0 ? '<b>' + r.suggest + '</b>' : '<span class="sy-muted">enough stock</span>'}</td></tr>`).join('') : '<tr><td colspan="5" class="sy-empty">No sales yet, so there is nothing to base a suggestion on.</td></tr>';
    } catch (e) { $('fc-note').textContent = e.message; }
  }
  initHeader(null); loadMovers(); loadForecast();
})();
