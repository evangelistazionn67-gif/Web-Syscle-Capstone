// MY ORDERS: every order with a step tracker (Pending > Preparing > Ready > Completed) that refreshes by itself.
// A customer can also show the order QR code at the counter, ask for a refund on a paid order, and review the items of a completed order.
(async function () {
  const list = document.getElementById('ord-list'), tabs = document.getElementById('ord-tabs'), alerts = document.getElementById('ord-alerts');
  const me = await (window.SY_READY); if (!me) { location.href = loginUrl(); return; }
  let rows = [], filter = 'active';
  const rule = (k, d) => { const v = +(window.SY_STORE || {})[k]; return v > 0 ? v : d; };
  const PICK = [['Pending', 'Order placed'], ['Preparing', 'Preparing'], ['Ready', 'Ready for pick-up'], ['Completed', 'Completed']];
  const SHIP = [['Pending', 'Order placed'], ['Preparing', 'Preparing'], ['Ready', 'Packed'], ['Out for Delivery', 'Out for delivery'], ['Completed', 'Delivered']];
  const MSG = { Pending: 'Waiting for the store to check your items.', Preparing: 'The store checked your items and is gathering them.', Ready: 'Your order is ready!', 'Out for Delivery': 'Your rider is on the way. Keep your phone nearby.', Completed: 'Thank you for shopping with us!', Cancelled: 'This order was cancelled and the items went back to stock.' };
  const fmtD = d => d ? new Date(d).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' }) : '-';
  const addDays = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };
  function tracker(o) {
    if (o.status === 'Cancelled') return '<div class="sy-banner bad">' + ico('x-circle') + ' This order was cancelled.</div>';
    const steps = o.fulfillment === 'Delivery' ? SHIP : PICK, idx = steps.findIndex(s => s[0] === o.status);
    return `<div class="sy-track" style="grid-template-columns:repeat(${steps.length},1fr)">` + steps.map((s, i) => `<div class="st ${i < idx || o.status === 'Completed' ? 'done' : i === idx ? 'now' : ''}" data-n="${i + 1}">${s[1]}</div>`).join('') + '</div>';
  }
  function refundBanner(o) {
    if (o.refundStatus === 'Requested') return '<div class="sy-banner warn">' + ico('arrow-counterclockwise') + ' Refund requested. The store will answer soon.</div>';
    if (o.refundStatus === 'Refunded') return '<div class="sy-banner ready">' + ico('cash-coin') + ' Refunded.' + (o.refundNote ? ' ' + esc(o.refundNote) : '') + '</div>';
    if (o.refundStatus === 'Rejected') return '<div class="sy-banner bad">' + ico('slash-circle') + ' Refund declined.' + (o.refundNote ? ' ' + esc(o.refundNote) : '') + ' Message the store if you have questions.</div>';
    return '';
  }
  function card(o) {
    const del = o.fulfillment === 'Delivery', online = /online/i.test(o.payment || ''), open = ['Pending', 'Preparing', 'Ready', 'Out for Delivery'].includes(o.status);
    const holdDays = rule('PickupHoldDays', 3), payHours = rule('UnpaidOnlineHours', 24);
    const canRefund = o.paymentStatus === 'Paid' && o.status !== 'Cancelled' && (!o.refundStatus || o.refundStatus === 'None');
    return `<article class="sy-order" id="o${o.id}"><div class="sy-order-h"><div><b>${esc(o.code)}</b><small>Placed ${fmtD(o.date)} &middot; ${del ? 'Delivery' : 'Pick-up'} ${fmtD(o.pickup)} &middot; ${esc(o.payment)}</small></div><div style="display:flex;gap:6px;flex-wrap:wrap">${payChip(o)}${statusChip(o.status)}</div></div>
      ${o.status === 'Ready' && !del ? '<div class="sy-banner ready">' + ico('bag-check') + ' <span>Ready for pick-up! Show order number <b>' + esc(o.code) + '</b> or its QR code at the store.' + (o.readyAt ? ' Please collect it by <b>' + fmtD(addDays(o.readyAt, holdDays)) + '</b>, or the order is cancelled and the items go back on the shelf.' : '') + '</span></div>' : ''}
      ${o.status === 'Out for Delivery' ? '<div class="sy-banner info">' + ico('truck') + ' <span>Out for delivery' + (o.courier ? ' with <b>' + esc(o.courier) + '</b>' : '') + (o.trackingNo ? ' &middot; Tracking no. <b>' + esc(o.trackingNo) + '</b>' : '') + '</span></div>' : ''}
      ${online && o.paymentStatus !== 'Paid' && o.status === 'Pending' ? '<div class="sy-banner warn">' + ico('credit-card') + ' <span>Payment not received yet. Please pay within ' + payHours + ' hours or the order is cancelled.</span> <button class="sy-btn small accent" data-pay="' + o.id + '" type="button">Pay now</button></div>' : ''}
      ${!online && o.paymentStatus !== 'Paid' && open ? '<div class="sy-banner info">' + ico('info-circle') + ' <span>The store records your payment after it checks your items.</span></div>' : ''}
      ${refundBanner(o)}
      ${tracker(o)}<p class="sy-sub" style="margin:8px 0 0;text-align:center">${MSG[o.status] || ''}</p>
      ${del ? `<div class="sy-deliv"><b>Deliver to</b><span>${esc(o.address || '')}</span><span>${ico('telephone')} ${esc(o.deliveryPhone || '')}</span>${o.courier ? '<span>Courier: ' + esc(o.courier) + (o.trackingNo ? ' &middot; ' + esc(o.trackingNo) : '') + '</span>' : ''}</div>` : ''}
      <div class="sy-order-items">${esc(o.items || '')}</div>
      <div class="sy-order-f"><span>Total <b>${peso(o.total)}</b>${+o.deliveryFee > 0 ? ' <small class="sy-muted">(incl. ' + peso(o.deliveryFee) + ' delivery)</small>' : ''}</span>
        <span class="sy-order-btns">${open ? `<button class="sy-btn ghost small" data-qr="${esc(o.code)}" type="button">${ico('qr-code')} Order QR</button>` : ''}
        ${o.status === 'Completed' ? `<button class="sy-btn ghost small" data-review="${o.id}" type="button">${ico('star')} Review items</button>` : ''}
        ${canRefund ? `<button class="sy-btn ghost small" data-refund="${o.id}" type="button">${ico('arrow-counterclockwise')} Request refund</button>` : ''}
        ${o.status === 'Pending' && o.paymentStatus !== 'Paid' ? `<button class="sy-x" data-cancel="${o.id}" type="button">Cancel order</button>` : ''}</span></div></article>`;
  }
  function paint() {
    const f = rows.filter(o => filter === 'all' || (filter === 'active' && ['Pending', 'Preparing', 'Ready', 'Out for Delivery'].includes(o.status)) || (filter === 'done' && o.status === 'Completed') || (filter === 'cancel' && o.status === 'Cancelled'));
    list.innerHTML = f.length ? f.map(card).join('') : `<div class="sy-empty">${rows.length ? 'Nothing here.' : 'No orders yet. <a href="homepage.html">Start shopping</a>'}</div>`;
    const ready = rows.filter(o => o.status === 'Ready' && o.fulfillment !== 'Delivery').length;
    alerts.innerHTML = ready ? `<div class="sy-banner ready">${ico('bag-check')} ${ready} order${ready > 1 ? 's are' : ' is'} ready for pick-up.</div>` : '';
  }
  async function load() { try { rows = await api('/api/orders'); if (!document.querySelector('.sy-ov')) paint(); } catch (e) { list.innerHTML = '<div class="sy-empty">Could not load your orders.</div>'; } }
  tabs.addEventListener('click', e => { const b = e.target.closest('[data-f]'); if (!b) return; filter = b.dataset.f; tabs.querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b)); paint(); });

  function showQr(code) {
    showModal(`<div class="sy-done"><h2>Order QR</h2><p>Show this at the counter. The staff scans it and your order opens right away.</p>
      <img class="sy-qrimg" src="/api/qr?text=${encodeURIComponent(code)}" alt="QR code for order ${esc(code)}" width="220" height="220"><div class="sy-code">${esc(code)}</div></div>`);
  }
  function askRefund(id) {
    const days = rule('RefundWindowDays', 7);
    const m = showModal(`<h2>Request a refund</h2><p class="sy-sub">Tell the store what went wrong. They will check it and answer here and by email. Completed orders can be refunded within ${days} days.</p>
      <label for="rf-reason">Reason</label><textarea id="rf-reason" class="sy-input" rows="4" maxlength="300" placeholder="For example: the part does not fit my bike."></textarea>
      <div class="sy-modal-actions"><button class="sy-btn ghost" type="button" id="rf-no">Not now</button><button class="sy-btn accent" type="button" id="rf-go">Send request</button></div>`);
    m.el.querySelector('#rf-no').onclick = m.close;
    m.el.querySelector('#rf-go').onclick = async () => {
      const reason = m.el.querySelector('#rf-reason').value.trim(); if (reason.length < 5) return toast('Please tell us why (at least 5 characters).', true);
      try { await api('/api/orders/' + id + '/refund', 'POST', { reason }); m.close(); toast('Refund request sent.'); await load(); if (window.syRefreshBell) syRefreshBell(); } catch (e) { toast(e.message, true); }
    };
  }
  async function reviewItems(id) {
    let items = []; try { items = await api('/api/orders/' + id + '/items'); } catch (e) { return toast(e.message, true); }
    const seen = new Set(), uniq = items.filter(i => !seen.has(i.productId) && seen.add(i.productId));
    const m = showModal(`<h2>Review your items</h2><p class="sy-sub">Thanks for shopping with us. Your review helps other riders.</p><div id="rv-list">` + uniq.map(i => i.reviewed > 0
      ? `<div class="sy-rvrow"><b>${esc(i.name)}</b><span class="sy-chip ok">Reviewed</span></div>`
      : `<div class="sy-rvrow" data-p="${i.productId}"><b>${esc(i.name)}</b><div class="sy-rvform"><select class="sy-input" aria-label="Rating"><option value="5">5 stars</option><option value="4">4 stars</option><option value="3">3 stars</option><option value="2">2 stars</option><option value="1">1 star</option></select>
        <input class="sy-input" maxlength="300" placeholder="Write a review..."><button class="sy-btn small" type="button">Post</button></div></div>`).join('') + '</div>');
    m.el.querySelectorAll('.sy-rvrow[data-p]').forEach(row => row.querySelector('button').addEventListener('click', async () => {
      try { await api('/api/products/' + row.dataset.p + '/reviews', 'POST', { rating: +row.querySelector('select').value, comment: row.querySelector('input').value }); row.querySelector('.sy-rvform').outerHTML = '<span class="sy-chip ok">Reviewed</span>'; toast('Review posted.'); } catch (e) { toast(e.message, true); }
    }));
  }
  list.addEventListener('click', async e => {
    const t = e.target.closest('button'); if (!t) return;
    if (t.dataset.pay) { try { const r = await api('/api/orders/' + t.dataset.pay + '/pay', 'POST'); if (r.paid) { toast('Payment received. Thank you!'); await load(); } else location.href = r.checkoutUrl; } catch (err) { toast(err.message, true); } return; }
    if (t.dataset.qr) return showQr(t.dataset.qr);
    if (t.dataset.refund) return askRefund(+t.dataset.refund);
    if (t.dataset.review) return reviewItems(+t.dataset.review);
    const id = t.dataset.cancel; if (!id || !confirm('Cancel this order? The items go back to stock.')) return;
    try { await api('/api/orders/' + id + '/cancel', 'POST'); toast('Order cancelled.'); await load(); if (window.syRefreshBell) syRefreshBell(); } catch (err) { toast(err.message, true); }
  });
  // coming back from the PayMongo or PayPal page: ask the server to confirm the payment
  const qp = new URLSearchParams(location.search);
  if (qp.get('paid')) { try { const v = await api('/api/orders/' + +qp.get('paid') + '/verify-payment', 'POST'); toast(v.paid ? 'Payment received. Thank you!' : 'We are still waiting for the payment to be confirmed.', !v.paid); } catch (e) { } history.replaceState(null, '', location.pathname); }
  else if (qp.get('cancelled')) { toast('Payment was not completed. You can pay from this page.', true); history.replaceState(null, '', location.pathname); }
  await load(); initHeader(null);
  setInterval(() => { if (!document.hidden) load(); }, 15000);
})();
