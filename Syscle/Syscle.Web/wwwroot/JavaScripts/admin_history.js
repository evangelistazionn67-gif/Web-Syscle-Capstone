// TRANSACTIONS HISTORY: opens on TODAY. The quick buttons switch to ALL DATES (all / completed / pending / cancelled).
(async function () {
    const $ = id => document.getElementById(id);
    if (!$('transaction-table-body')) return;
    const chip = s => `<span class="sy-chip ${s === 'Completed' ? 'ok' : s === 'Cancelled' ? 'bad' : 'warn'}">${esc(s)}</span>`;
    // local date as yyyy-mm-dd (NOT toISOString, which is UTC and shows yesterday in the Philippines before 8 AM)
    const localISO = (d = new Date()) => d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
    let rows = [];

    function setActive(key) {
        document.querySelectorAll('#quick-bar [data-quick]').forEach(b => {
            const on = b.dataset.quick === key; b.classList.toggle('ghost', !on);
        });
    }
    function currentKey() {
        const s = $('f-start-date').value, e = $('f-end-date').value, st = $('f-status').value, t = localISO();
        if (s === t && e === t && !st) return 'today';
        if (!s && !e) return st || 'all';
        return '';
    }

    async function load() {
        // only send the filters that are filled in (the ALL DATES buttons leave the dates empty)
        const qs = new URLSearchParams();
        [['search', $('f-search').value.trim()], ['status', $('f-status').value], ['startDate', $('f-start-date').value], ['endDate', $('f-end-date').value]].forEach(([k, v]) => { if (v) qs.set(k, v); });
        setActive(currentKey());
        const s = $('f-start-date').value, e = $('f-end-date').value;
        $('range-note').textContent = !s && !e ? 'Showing ALL dates.' : (s === e ? 'Showing ' + new Date(s + 'T00:00').toLocaleDateString() + '.' : 'Showing ' + (s ? new Date(s + 'T00:00').toLocaleDateString() : 'the beginning') + ' to ' + (e ? new Date(e + 'T00:00').toLocaleDateString() : 'today and later') + '.');
        try {
            const d = await api('/api/admin/transactions?' + qs);
            rows = d.rows;
            $('val-total').textContent = d.summary.total;
            $('val-completed').textContent = d.summary.completed;
            $('val-pending').textContent = d.summary.pending;
            $('val-cancelled').textContent = d.summary.cancelled;
            $('transaction-table-body').innerHTML = d.rows.length ? d.rows.map(o => `<tr data-id="${o.id}">
        <td>${esc(o.code)}</td><td>${fmtDateTime(o.date)}</td><td>${esc(o.customer)}${o.userId ? '<br><small>C-' + String(o.userId).padStart(6, '0') + '</small>' : ''}</td><td style="text-align:left">${esc(o.items)}</td>
        <td>${peso(o.total)}</td><td>${esc(o.payment)}${o.paymentRef ? '<br><small>Ref: ' + esc(o.paymentRef) + '</small>' : ''}</td><td>${o.pickup ? fmtDate(o.pickup) : '-'}</td><td>${chip(o.status)}</td>
        <td>${o.status === 'Pending' ? '<div class="sy-actions"><button class="sy-btn small" data-s="Completed">Complete</button><button class="sy-x small" data-s="Cancelled">Cancel</button></div>' : '-'}</td></tr>`).join('')
                : '<tr><td colspan="9" class="sy-empty">No transactions found for this view. Use the ALL DATES buttons to look further back.</td></tr>';
        } catch (err) { toast(err.message, true); }
    }

    function quick(key) {
        $('f-search').value = '';
        if (key === 'today') { $('f-start-date').value = $('f-end-date').value = localISO(); $('f-status').value = ''; }
        else { $('f-start-date').value = ''; $('f-end-date').value = ''; $('f-status').value = key === 'all' ? '' : key; }
        load();
    }
    $('quick-bar').addEventListener('click', e => { const k = e.target.dataset.quick; if (k) quick(k); });

    $('f-status').addEventListener('change', load);
    $('f-start-date').addEventListener('change', load);
    $('f-end-date').addEventListener('change', load);
    $('btn-filter').addEventListener('click', load);
    $('f-search').addEventListener('keydown', e => { if (e.key === 'Enter') load(); });
    $('btn-reset').addEventListener('click', () => quick('today'));

    $('btn-export').addEventListener('click', () => {
        if (!rows.length) return toast('Nothing to export.', true);
        const q = v => '"' + String(v ?? '').replace(/"/g, '""') + '"';
        const csv = ['Order,Date,Customer,Items,Total,Payment,Reference,Pickup,Status'].concat(rows.map(o => [o.code, fmtDateTime(o.date), o.customer, o.items, o.total, o.payment, o.paymentRef || '', o.pickup ? fmtDate(o.pickup) : '', o.status].map(q).join(','))).join('\r\n');
        const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' }));
        a.download = 'transactions-' + localISO() + '.csv'; a.click(); URL.revokeObjectURL(a.href);
    });

    $('transaction-table-body').addEventListener('click', async e => {
        const s = e.target.dataset.s, row = e.target.closest('tr[data-id]'); if (!s || !row) return;
        if (s === 'Cancelled' && !confirm('Cancel this transaction and return its items to stock?')) return;
        try { await api(`/api/admin/transactions/${row.dataset.id}/status`, 'PUT', { status: s }); await load(); toast('Updated.'); } catch (err) { toast(err.message, true); }
    });

    initHeader(t => { $('f-search').value = t; load(); });
    quick('today');
})();
