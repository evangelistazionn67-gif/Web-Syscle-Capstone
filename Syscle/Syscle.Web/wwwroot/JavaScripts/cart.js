(async function () {
    const $ = id => document.getElementById(id);
    let cart = [], pct = 0, methods = [], bundleInfo = { discount: 0, applied: [] }, fee = 100, delivery = false;
    const me = await api('/api/auth/me');
    $('cart-dynamic-title').textContent = 'Your cart';
    $('summary-dynamic-name').textContent = me.username;
    $('purchase-date').textContent = new Date().toLocaleDateString();
    const localISO = d => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
    const today = new Date(), pick = $('pickup-date');
    pick.min = localISO(today); pick.value = localISO(today); pick.max = localISO(new Date(today.getTime() + 30 * 864e5));

    function render() {
        const box = $('cart-items-container'), bd = $('cost-breakdown-container');
        box.innerHTML = cart.length ? '' : '<div class="sy-empty">Your cart is empty.<br><a class="sy-btn accent small" style="margin-top:12px" href="homepage.html">Browse parts</a></div>';
        bd.querySelectorAll('.sy-row').forEach(n => n.remove());
        let sub = 0;
        cart.forEach(l => {
            sub += l.price * l.qty;
            const colorStr = l.color ? `<span style="background:#eee;color:#000;padding:2px 5px;border-radius:3px;font-size:12px;margin-left:5px;">${esc(l.color)}</span>` : '';
            box.insertAdjacentHTML('beforeend', `<div class="cart-product-card sy-line" data-id="${l.productId}" data-size="${esc(l.size || '')}" data-color="${esc(l.color || '')}">
        ${l.image ? `<img class="sy-thumb" src="${esc(l.image)}" alt="">` : ''}<div class="sy-grow"><b>${esc(l.name)}${l.size ? ' <span class="sy-sizetag">' + esc(l.size) + '</span>' : ''}${colorStr}</b><small>${esc(l.itemType)} &middot; ${peso(l.price)} each &middot; ${l.stock} in stock</small></div>
        <div class="sy-qty"><button data-d="-1">-</button><span>${l.qty}</span><button data-d="1">+</button></div>
        <b>${peso(l.price * l.qty)}</b><button class="sy-x" data-rm="1">Remove</button></div>`);
            bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>${esc(l.name)}${l.size ? ' [' + esc(l.size) + ']' : ''}${l.color ? ' (' + esc(l.color) + ')' : ''} (${peso(l.price)})</span><span>x ${l.qty}</span></div>`);
        });
        bundleInfo.applied.forEach(b => bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>Bundle: ${esc(b.name)} (${b.percent}% off)</span><span>-${peso(b.discount)}</span></div>`));
        const after = sub - bundleInfo.discount;
        if (delivery && cart.length) bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>Delivery fee</span><span>${peso(fee)}</span></div>`);
        $('total-cost').textContent = 'Total: ' + peso(after - Math.round(after * pct) / 100 + (delivery && cart.length ? fee : 0)) + (pct ? ` (${pct}% promo)` : '');
    }

    async function load() { cart = await api('/api/cart'); bundleInfo = await api('/api/cart/bundles'); render(); refreshCartBadge(); }

    async function setQty(id, size, color, q) {
        try { await api('/api/cart/' + id, 'PUT', { qty: q, size, color }); await load(); } catch (e) { toast(e.message, true); }
    }

    $('cart-items-container').addEventListener('click', e => {
        const card = e.target.closest('[data-id]'); if (!card) return;
        const id = +card.dataset.id, size = card.dataset.size || '', color = card.dataset.color || '';
        const line = cart.find(l => l.productId === id && (l.size || '') === size && (l.color || '') === color);
        if (e.target.dataset.rm) setQty(id, size, color, 0);
        else if (e.target.dataset.d && line) setQty(id, size, color, line.qty + (+e.target.dataset.d));
    });

    async function applyPromo(quiet) {
        const code = $('promo-code').value.trim();
        if (!code) { pct = 0; render(); return; }
        try { pct = (await api('/api/promo', 'POST', { code })).percent; if (!quiet) toast(`Promo applied: ${pct}% off.`); }
        catch (e) { pct = 0; toast(e.message, true); if (quiet) { try { localStorage.removeItem('sy.claim'); } catch (x) { } } }
        render();
    }
    $('promo-apply').addEventListener('click', () => applyPromo(false));
    $('promo-code').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); applyPromo(false); } });
    // codes the store is giving out right now (the same ones the pop-up shows), and a code the customer already claimed
    try {
        const d = await api('/api/promotions'), box = $('promo-offers');
        if (d.codes && d.codes.length) {
            box.hidden = false; box.innerHTML = '<span>Codes you can use:</span> ' + d.codes.map(c => `<button type="button" class="sy-chip accent" data-code="${esc(c.code)}">${esc(c.code)} &middot; ${c.percent}% off</button>`).join(' ');
            box.addEventListener('click', e => { const b = e.target.closest('[data-code]'); if (b) { $('promo-code').value = b.dataset.code; applyPromo(false); } });
        }
    } catch (e) { }
    try { const cl = JSON.parse(localStorage.getItem('sy.claim') || 'null'); if (cl && cl.code) { $('promo-code').value = cl.code; applyPromo(true); } } catch (e) { }

    // pick-up or home delivery
    try { const si = await api('/api/store-info'); if (si.DeliveryFee !== undefined && si.DeliveryFee !== '' && !isNaN(+si.DeliveryFee)) fee = +si.DeliveryFee; } catch (e) { }
    $('ful-fee').textContent = fee > 0 ? 'Fee ' + peso(fee) : 'Free delivery';
    document.querySelectorAll('input[name=ful]').forEach(r => r.addEventListener('change', () => {
        delivery = document.querySelector('input[name=ful]:checked').value === 'Delivery';
        $('delivery-box').hidden = !delivery; $('date-label').textContent = delivery ? 'Preferred delivery date' : 'Pick-up date'; render();
    }));
    $('btn-checkout').addEventListener('click', async () => {
        const btn = $('btn-checkout'); if (btn.disabled) return;   // one click = one order, even if the button is pressed twice quickly
        if (!cart.length) return toast('Cart is empty.', true);
        if (delivery && ($('del-address').value.trim().length < 10 || !/^[0-9+\- ]{7,20}$/.test($('del-phone').value.trim()))) return toast('Enter your full delivery address and a phone number.', true);
        const pickText = new Date(pick.value + 'T00:00:00').toLocaleDateString();
        try {
            btn.disabled = true;
            const res = await api('/api/checkout', 'POST', { pickupDate: pick.value, promoCode: $('promo-code').value.trim() || null, paymentMethod: $('payment-method').value, fulfillment: delivery ? 'Delivery' : 'Pickup', address: delivery ? $('del-address').value : null, phone: delivery ? $('del-phone').value : null });
            if (res.checkoutUrl) { toast('Opening the secure payment page...'); location.href = res.checkoutUrl; return; }   // the button stays disabled while the browser leaves the page
            btn.disabled = false;
            try { if (($('promo-code').value || '').trim().toUpperCase() === (JSON.parse(localStorage.getItem('sy.claim') || '{}').code || '')) localStorage.removeItem('sy.claim'); } catch (x) { }
            $('order-number').textContent = 'ORDER NUMBER: ' + res.orderCode;
            const m = methods.find(x => x.code === $('payment-method').value);
            cart = []; pct = 0; render(); refreshCartBadge();
            const payNote = res.payError ? '<p class="sy-red">' + esc(res.payError) + ' You can pay from My orders.</p>' : '';
            const pay = payNote + '<p>We sent a confirmation to your email and you can follow each step in My orders. The store checks your items first, then confirms your payment.</p>' + (m && m.code === 'paymongo' ? '' : (delivery ? '<p>Pay the rider when your order arrives.</p>' : '<p>Pay at the store when you pick up.</p>'));
            showModal(`<div class="sy-done"><div class="sy-check"><i class="bi bi-check-lg" aria-hidden="true"></i></div><h2>Order placed!</h2><p>Your order number</p><div class="sy-code">${esc(res.orderCode)}</div>
        <p>Total: <b>${peso(res.total)}</b></p><p>${delivery ? 'Delivery' : 'Pick-up'} date: <b>${pickText}</b></p>${pay}
        <div class="sy-pm-actions"><a class="sy-btn ghost" href="orders.html">Track my order</a><a class="sy-btn accent" href="homepage.html">Continue shopping</a></div></div>`);
        } catch (e) { btn.disabled = false; toast(e.message, true); await load(); }
    });

    try {
        methods = await api('/api/payment-methods');
        // the store's payment methods: Cash (pay at the store) and Online (PayMongo). One that is not set up yet is shown but cannot be picked.
        const usable = m => m.ready;
        $('payment-method').innerHTML = methods.map(m => `<option value="${esc(m.code)}"${usable(m) ? '' : ' disabled'}>${esc(m.label)}</option>`).join('') || '<option value="">No payment method</option>';
        const first = methods.find(usable); if (first) $('payment-method').value = first.code;
        const HINT = { cash: 'Pay in cash at pick-up, or to the rider.', paymongo: 'Pay now on a secure page: scan a QR code made for your total, or use GCash, Maya, a card or other networks.' };
        const ICON = { cash: 'cash-stack', paymongo: 'phone' };
        const paintList = () => {
            $('pay-list').innerHTML = methods.map(m => `<label class="sy-payopt${$('payment-method').value === m.code ? ' on' : ''}${usable(m) ? '' : ' off'}"><input type="radio" name="paypick" value="${esc(m.code)}"${$('payment-method').value === m.code ? ' checked' : ''}${usable(m) ? '' : ' disabled'}>
                <i class="bi bi-${ICON[m.code] || 'wallet2'}" aria-hidden="true"></i><span><b>${esc(m.label)}</b><small>${esc(HINT[m.code] || '')}</small></span>${!m.ready ? '<em class="sy-chip mute">Not available yet</em>' : ''}</label>`).join('');
        };
        $('pay-list').addEventListener('change', e => { if (e.target.name === 'paypick') { $('payment-method').value = e.target.value; $('payment-method').dispatchEvent(new Event('change')); paintList(); } });
        paintList();
        try { const pref = localStorage.getItem('syscle.pay'); if (pref && methods.some(x => x.code === pref && usable(x))) { $('payment-method').value = pref; paintList(); } } catch (e) { }
        await load();
    } catch (e) { location.href = '/Pages/error.html'; }
    initHeader(null);
})();