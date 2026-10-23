(async function () {
    const $ = id => document.getElementById(id);
    let cart = [], pct = 0, methods = [], bundleInfo = { discount: 0, applied: [] };
    const me = await api('/api/auth/me');
    $('cart-dynamic-title').textContent = me.username + '\u2019s Cart';
    $('summary-dynamic-name').textContent = me.username;
    $('purchase-date').textContent = new Date().toLocaleDateString();
    const localISO = d => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
    const today = new Date(), pick = $('pickup-date');
    pick.min = localISO(today); pick.value = localISO(today); pick.max = localISO(new Date(today.getTime() + 30 * 864e5));

    function render() {
        const box = $('cart-items-container'), bd = $('cost-breakdown-container');
        box.innerHTML = cart.length ? '' : '<div class="sy-empty" style="color:#fff">Your cart is empty. <a href="homepage.html" style="color:#ffd23f">Browse parts</a></div>';
        bd.querySelectorAll('.sy-row').forEach(n => n.remove());
        let sub = 0;
        cart.forEach(l => {
            sub += l.price * l.qty;
            const colorStr = l.color ? `<span style="background:#eee;color:#000;padding:2px 5px;border-radius:3px;font-size:12px;margin-left:5px;">${esc(l.color)}</span>` : '';
            box.insertAdjacentHTML('beforeend', `<div class="cart-product-card sy-line" data-id="${l.productId}" data-size="${esc(l.size || '')}" data-color="${esc(l.color || '')}">
        ${l.image ? `<img class="sy-thumb" src="${esc(l.image)}" alt="">` : ''}<div class="sy-grow"><b>${esc(l.name)}${l.size ? ' <span class="sy-sizetag">' + esc(l.size) + '</span>' : ''}${colorStr}</b><small>${esc(l.itemType)} · ${peso(l.price)} each · ${l.stock} in stock</small></div>
        <div class="sy-qty"><button data-d="-1">-</button><span>${l.qty}</span><button data-d="1">+</button></div>
        <b>${peso(l.price * l.qty)}</b><button class="sy-x" data-rm="1">Remove</button></div>`);
            bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>${esc(l.name)}${l.size ? ' [' + esc(l.size) + ']' : ''}${l.color ? ' (' + esc(l.color) + ')' : ''} (${peso(l.price)})</span><span>x ${l.qty}</span></div>`);
        });
        bundleInfo.applied.forEach(b => bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>Bundle: ${esc(b.name)} (${b.percent}% off)</span><span>-${peso(b.discount)}</span></div>`));
        const after = sub - bundleInfo.discount;
        $('total-cost').textContent = 'Total Cost: ' + peso(after - Math.round(after * pct) / 100) + (pct ? ` (${pct}% promo)` : '');
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

    $('promo-apply').addEventListener('click', async () => {
        const code = $('promo-code').value.trim();
        if (!code) { pct = 0; render(); return; }
        try { pct = (await api('/api/promo', 'POST', { code })).percent; toast(`Promo applied: ${pct}% off.`); }
        catch (e) { pct = 0; toast(e.message, true); }
        render();
    });

    $('btn-checkout').addEventListener('click', async () => {
        if (!cart.length) return toast('Cart is empty.', true);
        const pickText = new Date(pick.value + 'T00:00:00').toLocaleDateString();
        try {
            const isG = $('payment-method').value === 'gcash', ref = $('gc-ref').value.replace(/\s/g, '');
            if (isG && !/^[A-Za-z0-9]{8,20}$/.test(ref)) return toast('Pay the GCash number shown, then enter the reference number from your receipt.', true);
            const res = await api('/api/checkout', 'POST', { pickupDate: pick.value, promoCode: $('promo-code').value.trim() || null, paymentMethod: $('payment-method').value, paymentRef: isG ? ref : null });
            $('gc-ref').value = '';
            $('order-number').textContent = 'ORDER NUMBER: ' + res.orderCode;
            const m = methods.find(x => x.code === $('payment-method').value);
            cart = []; pct = 0; render(); refreshCartBadge();
            const pay = m && m.code === 'gcash' ? '<p>Thank you! The store will check your GCash payment and complete your order.</p>' : '<p>Pay at the store when you pick up.</p>';
            showModal(`<div class="sy-done"><div class="sy-check">&#10003;</div><h2>Order placed!</h2><p>Your order number</p><div class="sy-code">${esc(res.orderCode)}</div>
        <p>Total: <b>${peso(res.total)}</b></p><p>Pick-up date: <b>${pickText}</b></p>${pay}
        <div class="sy-pm-actions"><a class="sy-btn ghost" href="account.html">View my orders</a><a class="sy-btn accent" href="homepage.html">Continue shopping</a></div></div>`);
        } catch (e) { toast(e.message, true); await load(); }
    });

    try {
        methods = await api('/api/payment-methods');
        $('payment-method').innerHTML = methods.map(m => `<option value="${esc(m.code)}">${esc(m.label)}</option>`).join('') || '<option value="">No payment method</option>';
        // GCash box: shows the store's number (and QR when the admin turned it on) and asks for the reference number
        let qr = { show: false, image: '' }; try { qr = await api('/api/settings/payment-qr'); } catch (e) { }
        const paintPay = () => {
            const m = methods.find(x => x.code === $('payment-method').value), box = $('gcash-box');
            box.hidden = !(m && m.code === 'gcash');
            if (box.hidden) return;
            $('gc-number').textContent = m.accountNumber || '(ask the store)'; $('gc-name').textContent = m.accountName ? '(' + m.accountName + ')' : '';
            $('gc-qr').hidden = !(qr.show && qr.image); if (qr.image) $('gc-qr').src = qr.image;
        };
        $('payment-method').addEventListener('change', paintPay); paintPay();
        try { const pref = localStorage.getItem('syscle.pay'); if (pref && methods.some(x => x.code === pref)) { $('payment-method').value = pref; paintPay(); } } catch (e) { }
        await load();
    } catch (e) { location.href = 'error.html'; }
    initHeader(null);
})();