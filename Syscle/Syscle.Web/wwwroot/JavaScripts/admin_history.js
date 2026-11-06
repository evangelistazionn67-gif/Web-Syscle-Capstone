// TRANSACTIONS HISTORY: opens on TODAY. The quick buttons switch to ALL DATES (all / completed / pending / cancelled).
(async function () {
    const $ = id => document.getElementById(id);
    if (!$('transaction-table-body')) return;
    const chip = statusChip, ORDER = ['Pending', 'Preparing', 'Ready', 'Completed'], SHIP = ['Pending', 'Preparing', 'Ready', 'Out for Delivery', 'Completed'];
    const mini = (s, del) => { const L = del ? SHIP : ORDER; return s === 'Cancelled' ? '' : '<div class="sy-mini" style="margin-top:6px">' + L.map((x, i) => `<i class="${i < L.indexOf(s) || s === 'Completed' ? 'done' : i === L.indexOf(s) ? 'on' : ''}"></i>`).join('') + '</div>'; };
    const NEXT = (o) => o.status === 'Pending' ? ['Preparing', 'Start preparing'] : o.status === 'Preparing' ? ['Ready', o.fulfillment === 'Delivery' ? 'Mark packed' : 'Mark ready'] : o.status === 'Ready' ? (o.fulfillment === 'Delivery' ? ['Out for Delivery', 'Out for delivery'] : ['Completed', 'Complete']) : o.status === 'Out for Delivery' ? ['Completed', 'Mark delivered'] : null;
    const OPEN = ['Pending', 'Preparing', 'Ready', 'Out for Delivery'];
    const isOnlinePay = o => /online/i.test(o.payment || '');
    // the order of work: 1) confirm the items, 2) record the money, 3) move the order forward. Money is never recorded before the items are confirmed.
    const actions = o => {
        if (o.refundStatus === 'Requested') return `<div class="sy-actions"><button class="sy-btn small accent" data-refund="1">${ico('arrow-counterclockwise')} Answer refund</button></div>`;
        if (!OPEN.includes(o.status)) return o.refundStatus === 'Refunded' ? '<span class="sy-chip mute">Refunded</span>' : '-';
        const b = [];
        if (!o.itemsConfirmed) b.push(`<button class="sy-btn small accent" data-act="items">${ico('clipboard-check')} Confirm items</button>`);
        else if (o.paymentStatus !== 'Paid') b.push(isOnlinePay(o) ? '<span class="sy-chip warn">Waiting for online payment</span>' : `<button class="sy-btn small accent" data-act="pay">${ico('cash-coin')} Payment received</button>`);
        const n = o.itemsConfirmed ? NEXT(o) : null;
        if (n && !(n[0] === 'Completed' && o.paymentStatus !== 'Paid')) b.push(`<button class="sy-btn small${b.length ? ' ghost' : ' accent'}" data-s="${n[0]}">${n[1]}</button>`);
        if (o.paymentStatus !== 'Paid') b.push('<button class="sy-x small" data-s="Cancelled">Cancel</button>');
        else b.push('<small class="sy-muted">Paid orders are cancelled through a refund.</small>');
        return '<div class="sy-actions">' + b.join('') + '</div>';
    };
    const stepChips = o => o.status === 'Cancelled' ? '' : `<div class="sy-steps"><span class="sy-chip ${o.itemsConfirmed ? 'ok' : 'warn'}">${o.itemsConfirmed ? 'Items confirmed' : 'Items not checked'}</span><span class="sy-chip ${o.paymentStatus === 'Paid' ? 'ok' : 'mute'}">${o.paymentStatus === 'Paid' ? 'Payment recorded' : 'Not paid yet'}</span>${o.refundStatus && o.refundStatus !== 'None' ? '<span class="sy-chip ' + (o.refundStatus === 'Refunded' ? 'mute' : o.refundStatus === 'Requested' ? 'bad' : 'warn') + '">Refund ' + o.refundStatus.toLowerCase() + '</span>' : ''}</div>`;
    let onlyRefund = false;
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
            rows = d.rows; if (onlyRefund) d.rows = rows = rows.filter(o => o.refundStatus === 'Requested');
            const rq = (await api('/api/admin/stats').catch(() => null)); if ($('val-refund') && rq) $('val-refund').textContent = rq.orders.refundRequests || 0;
            $('val-total').textContent = d.summary.total;
            $('val-completed').textContent = d.summary.completed;
            $('val-pending').textContent = d.summary.pending; $('val-preparing').textContent = d.summary.preparing || 0; $('val-ready').textContent = d.summary.ready || 0;
            $('val-cancelled').textContent = d.summary.cancelled;
            $('transaction-table-body').innerHTML = d.rows.length ? d.rows.map(o => `<tr data-id="${o.id}">
        <td>${esc(o.code)}</td><td>${fmtDate(o.date)}<br><small class="sy-muted">${new Date(o.date).toLocaleTimeString([], {hour: '2-digit', minute: '2-digit'})}</small></td><td>${esc(o.customer)}${o.userId ? '<br><small>C-' + String(o.userId).padStart(6, '0') + '</small>' : ''}</td><td>${esc(o.items)}</td>
        <td>${peso(o.total)}</td><td>${esc(o.payment)} ${payChip(o)}${o.paymentRef && !/^(cs|pp|test)_/.test(o.paymentRef) ? '<br><small>Ref: ' + esc(o.paymentRef) + '</small>' : ''}</td><td>${o.fulfillment === 'Delivery' ? '<span class="sy-chip info">Delivery</span><br><small>' + esc(o.address || '') + '<br>' + ico('telephone') + ' ' + esc(o.deliveryPhone || '') + '</small><br>' : ''}${o.pickup ? fmtDate(o.pickup) : '-'}</td><td>${chip(o.status)}${mini(o.status, o.fulfillment === 'Delivery')}${stepChips(o)}${o.courier ? '<br><small>' + esc(o.courier) + (o.trackingNo ? ' &middot; ' + esc(o.trackingNo) : '') + '</small>' : ''}</td>
        <td>${actions(o)}</td></tr>`).join('')
                : '<tr><td colspan="9" class="sy-empty">No transactions found for this view. Use the ALL DATES buttons to look further back.</td></tr>';
        } catch (err) { toast(err.message, true); }
    }

    function quick(key) {
        $('f-search').value = '';
        onlyRefund = key === 'refunds';
        if (key === 'refunds') { $('f-start-date').value = ''; $('f-end-date').value = ''; $('f-status').value = ''; }
        else if (key === 'today') { $('f-start-date').value = $('f-end-date').value = localISO(); $('f-status').value = ''; }
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
        const csv = ['Order,Date,Customer,Items,Total,Payment,Reference,Pickup,Status,Items confirmed,Payment status,Refund'].concat(rows.map(o => [o.code, fmtDateTime(o.date), o.customer, o.items, o.total, o.payment, o.paymentRef || '', o.pickup ? fmtDate(o.pickup) : '', o.status, o.itemsConfirmed ? 'Yes' : 'No', o.paymentStatus, o.refundStatus || 'None'].map(q).join(','))).join('\r\n');
        const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' }));
        a.download = 'transactions-' + localISO() + '.csv'; a.click(); URL.revokeObjectURL(a.href);
    });

    $('transaction-table-body').addEventListener('click', async e => {
        const btn = e.target.closest('button'), row = e.target.closest('tr[data-id]'); if (!btn || !row) return;
        const o = rows.find(x => x.id === +row.dataset.id), s = btn.dataset.s;
        if (btn.dataset.act === 'items') return confirmItems(o);
        if (btn.dataset.act === 'pay') return confirmPayment(o);
        if (btn.dataset.refund) return answerRefund(o);
        if (!s) return;
        if (s === 'Cancelled' && !confirm('Cancel this transaction and return its items to stock?')) return;
        const send = async extra => { try { await api(`/api/admin/transactions/${row.dataset.id}/status`, 'PUT', Object.assign({ status: s }, extra || {})); await load(); toast('Order moved to ' + s + '. The customer was notified.'); } catch (err) { toast(err.message, true); } };
        if (s === 'Out for Delivery') {
            const m = showModal(`<div class="sy-done" style="text-align:left"><h2>Send out for delivery</h2><p class="sy-sub">The customer sees these details on their order page.</p>
              <label for="dv-courier">Rider or courier name</label><input id="dv-courier" class="sy-input" maxlength="60" placeholder="e.g. Kuya Mark / LBC / J&T">
              <label for="dv-track">Tracking number or plate no. (optional)</label><input id="dv-track" class="sy-input" maxlength="60">
              <div class="sy-modal-actions"><button class="sy-btn accent" id="dv-go" type="button">Send out</button></div></div>`);
            m.el.querySelector('#dv-go').addEventListener('click', () => { const c = m.el.querySelector('#dv-courier').value.trim(); if (!c) return toast('Enter the rider or courier name.', true); const t = m.el.querySelector('#dv-track').value.trim(); m.close(); send({ courier: c, trackingNo: t }); });
            return;
        }
        await send();
    });

    function confirmItems(o) {
        const m = showModal(`<h2>Confirm the items</h2><p class="sy-sub">Order <b>${esc(o.code)}</b> for ${esc(o.customer)}. Check each item is here and is the right one. Payment is recorded only after this.</p>
          <ul class="sy-checklist">${String(o.items || '').split(/, (?=[^,]*? x\d+(?:,|$))/).map(t => `<li><label><input type="checkbox"> ${esc(t)}</label></li>`).join('')}</ul>
          <div class="sy-modal-actions"><button class="sy-btn ghost" type="button" id="ci-no">Not yet</button><button class="sy-btn accent" type="button" id="ci-go" disabled>Confirm items</button></div>`);
        const boxes = [...m.el.querySelectorAll('input[type=checkbox]')], go = m.el.querySelector('#ci-go');
        boxes.forEach(b => b.addEventListener('change', () => { go.disabled = !boxes.every(x => x.checked); }));
        m.el.querySelector('#ci-no').onclick = m.close;
        go.onclick = async () => { try { await api(`/api/admin/transactions/${o.id}/confirm-items`, 'POST'); m.close(); toast('Items confirmed.'); await load(); } catch (e) { toast(e.message, true); } };
    }
    function confirmPayment(o) {
        const ref = o.paymentRef && !/^(cs|pp|test)_/.test(o.paymentRef) ? o.paymentRef : '';
        const m = showModal(`<h2>Record the payment</h2><p class="sy-sub">Order <b>${esc(o.code)}</b> &middot; ${esc(o.payment)}</p><div class="sy-bigamt">${peso(o.total)}</div>
          ${ref ? `<p>The customer sent reference number <b>${esc(ref)}</b>. Check it in your ${esc(o.payment)} app first.</p>` : '<p>Collect this amount from the customer.</p>'}
          <label class="sy-check-line"><input type="checkbox" id="cp-ok"> I received ${peso(o.total)}</label>
          <div class="sy-modal-actions"><button class="sy-btn ghost" type="button" id="cp-no">Not yet</button><button class="sy-btn accent" type="button" id="cp-go" disabled>Record payment</button></div>`);
        const ok = m.el.querySelector('#cp-ok'), go = m.el.querySelector('#cp-go'); ok.addEventListener('change', () => { go.disabled = !ok.checked; });
        m.el.querySelector('#cp-no').onclick = m.close;
        go.onclick = async () => { try { await api(`/api/admin/transactions/${o.id}/confirm-payment`, 'POST', { ref }); m.close(); toast('Payment recorded.'); await load(); } catch (e) { toast(e.message, true); } };
    }
    function answerRefund(o) {
        const m = showModal(`<h2>Refund request</h2><p class="sy-sub">Order <b>${esc(o.code)}</b> &middot; ${esc(o.customer)} &middot; ${peso(o.total)} &middot; ${esc(o.payment)}</p>
          <p><b>Reason:</b> ${esc(o.refundReason || '-')}</p>
          <label for="rd-note">Message to the customer (optional)</label><input id="rd-note" class="sy-input" maxlength="300">
          <label class="sy-check-line"><input type="checkbox" id="rd-restock" checked> Put the items back in stock</label>
          <p class="sy-sub">Online payments (PayMongo) are refunded through the provider when approved. Cash refunds are handed back by you.</p>
          <div class="sy-modal-actions"><button class="sy-x" type="button" id="rd-no">Decline</button><button class="sy-btn accent" type="button" id="rd-yes">Approve refund</button></div>`);
        const send = ok => async () => { try { const r = await api(`/api/admin/transactions/${o.id}/refund-decision`, 'POST', { approve: ok, note: m.el.querySelector('#rd-note').value.trim() || null, restock: m.el.querySelector('#rd-restock').checked }); m.close(); toast(ok ? (r && r.message ? r.message : 'Refund approved.') : 'Refund declined.'); await load(); } catch (e) { toast(e.message, true); } };
        m.el.querySelector('#rd-yes').onclick = send(true); m.el.querySelector('#rd-no').onclick = send(false);
    }
    // scan an order QR code (shown in the customer's "My orders") to open that order here
    const scanBtn = $('btn-scan'); if (scanBtn) scanBtn.addEventListener('click', () => SyScan.open({ title: 'Scan an order QR', hint: 'Scan the QR the customer shows, or type the order number (for example ORD-000012).', onCode: async code => {
        const c = code.toUpperCase(); if (!/^(ORD|WLK)-\d{6}$/.test(c)) throw new Error('That is not an order code.');
        const hit = await api('/api/admin/orders/by-code/' + encodeURIComponent(c)); $('f-search').value = c; quick('all'); $('f-search').value = c; await load(); toast('Order ' + c + ' opened: ' + (hit.status || ''));
    } }));
    initHeader(t => { $('f-search').value = t; load(); });
    quick('today');
})();
