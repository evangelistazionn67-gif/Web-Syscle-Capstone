(async function () {
    const $ = id => document.getElementById(id);
    if (!$('transaction-items-container')) return; // Guard
    let products = [], lines = [];
    const sizesOf = p => (p.sizes || '').split(',').map(s => s.trim()).filter(Boolean);
    const used = id => lines.filter(l => l.id === id).reduce((a, l) => a + l.qty, 0);
    if ($('current-date')) $('current-date').textContent = 'Date: ' + new Date().toLocaleDateString();
    async function loadProducts() { products = await api('/api/admin/products'); }
    function renderResults(q) {
        const list = products.filter(p => p.stock > 0 && (p.name + ' ' + p.itemType + ' ' + p.category).toLowerCase().includes(q)).slice(0, 8);
        $('pos-search-results').innerHTML = list.length ? list.map(p => {
            const sz = sizesOf(p);
            return `<div class="pos-dropdown-item"><div class="item-details"><strong>${esc(p.name)}</strong><span>${esc(p.category)} &middot; ${peso(p.price)} &middot; ${p.stock} in stock</span></div>
        ${sz.length ? `<select class="pos-size"><option value="">Size</option>${sz.map(s => `<option>${esc(s)}</option>`).join('')}</select>` : ''}<button class="add-pos-btn" data-id="${p.id}">+</button></div>`;
        }).join('') : '<div class="sy-empty">No matching items in stock.</div>';
        $('pos-search-results').classList.add('open');
    }
    function render() {
        const box = $('transaction-items-container'), bd = $('cost-breakdown-container');
        box.innerHTML = lines.length ? '' : '<div class="sy-empty" style="color:#fff">Search for an item above to add it.</div>';
        if (bd) bd.querySelectorAll('.sy-row').forEach(n => n.remove());
        let total = 0;
        lines.forEach(l => {
            const p = products.find(x => x.id === l.id); total += p.price * l.qty;
            const label = esc(p.name) + (l.size ? ' <span class="sy-sizetag">' + esc(l.size) + '</span>' : '');
            box.insertAdjacentHTML('beforeend', `<div class="sy-line" data-id="${p.id}" data-size="${esc(l.size)}"><div class="sy-grow"><b>${label}</b><small>${peso(p.price)} each &middot; ${p.stock} in stock</small></div>
        <div class="sy-qty"><button data-d="-1">-</button><span>${l.qty}</span><button data-d="1">+</button></div><b>${peso(p.price * l.qty)}</b><button class="sy-x" data-rm="1">Remove</button></div>`);
            if (bd) bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>${esc(p.name)}${l.size ? ' [' + esc(l.size) + ']' : ''} (${peso(p.price)})</span><span>x ${l.qty}</span></div>`);
        });
        if ($('pos-total')) $('pos-total').textContent = 'Total Due: ' + peso(total);
        if ($('pos-bar-total')) $('pos-bar-total').textContent = peso(total);
    }
    const search = $('pos-item-search');
    if (search) {
        search.addEventListener('input', () => renderResults(search.value.trim().toLowerCase()));
        search.addEventListener('focus', () => renderResults(search.value.trim().toLowerCase()));
    }
    document.addEventListener('click', e => { if (!e.target.closest('.pos-search-wrapper') && $('pos-search-results')) $('pos-search-results').classList.remove('open'); });
    if ($('pos-search-results')) $('pos-search-results').addEventListener('click', e => {
        const btn = e.target.closest('.add-pos-btn'); if (!btn) return;
        const id = +btn.dataset.id, p = products.find(x => x.id === id), sel = btn.parentElement.querySelector('.pos-size'), size = sel ? sel.value : '';
        if (sel && !size) return toast('Choose a size first.', true);
        if (used(id) >= p.stock) return toast('No more stock for that item.', true);
        const l = lines.find(x => x.id === id && x.size === size);
        if (l) l.qty++; else lines.push({ id, size, qty: 1 });
        render();
    });
    if ($('transaction-items-container')) $('transaction-items-container').addEventListener('click', e => {
        const card = e.target.closest('[data-id]'); if (!card) return;
        const id = +card.dataset.id, size = card.dataset.size || '', l = lines.find(x => x.id === id && x.size === size), p = products.find(x => x.id === id);
        if (e.target.dataset.rm) lines = lines.filter(x => x !== l);
        else if (e.target.dataset.d) {
            const n = l.qty + (+e.target.dataset.d);
            if (n < 1) lines = lines.filter(x => x !== l);
            else if (n - l.qty > 0 && used(id) >= p.stock) toast('No more stock for that item.', true);
            else l.qty = n;
        }
        render();
    });
    // add an item by scanning the QR label (or barcode) printed from "QR labels". Every scan adds one piece.
    async function addByCode(code) {
        let p; try { p = await api('/api/admin/products/by-code/' + encodeURIComponent(code)); } catch (e) { throw new Error(e.message); }
        const full = products.find(x => x.id === p.id) || p; if (!products.some(x => x.id === p.id)) products.push(full);
        if (full.stock <= 0) throw new Error(full.name + ' is sold out.');
        const sz = sizesOf(full); let size = '';
        if (sz.length) { size = await new Promise(res => { const m = showModal(`<h2>${esc(full.name)}</h2><p class="sy-sub">Choose the size.</p><select id="sc-size" class="sy-input">${sz.map(x => `<option>${esc(x)}</option>`).join('')}</select><div class="sy-modal-actions"><button class="sy-btn accent" type="button" id="sc-ok">Add</button></div>`, () => res('')); m.el.querySelector('#sc-ok').onclick = () => { const v = m.el.querySelector('#sc-size').value; m.close(); res(v); }; }); if (!size) throw new Error('No size chosen.'); }
        if (used(full.id) >= full.stock) throw new Error('No more stock for ' + full.name + '.');
        const l = lines.find(x => x.id === full.id && x.size === size); if (l) l.qty++; else lines.push({ id: full.id, size, qty: 1 });
        render(); return 'Added ' + full.name + (size ? ' (' + size + ')' : '') + '. Scan the next item or close this box.';
    }
    if ($('btn-scan-item')) $('btn-scan-item').addEventListener('click', () => SyScan.open({ title: 'Scan items', hint: 'Scan the QR label on each item. Use a USB scanner, the camera, or type the SKU.', keepOpen: true, onCode: addByCode }));

    // receipt: prints by itself right after a sale (one click: "Record sale"), through a hidden page so no new window is needed
    let lastSale = 0, autoPrint = true, qrReady = false;
    function printReceipt(id) {
        const old = document.getElementById('rc-frame'); if (old) old.remove();
        const f = document.createElement('iframe'); f.id = 'rc-frame'; f.title = 'Receipt'; f.setAttribute('aria-hidden', 'true');
        f.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0;pointer-events:none';
        f.src = '/Pages/receipt.html?order=' + id + '&a=1&embed=1&print=1'; document.body.appendChild(f);
    }
    if ($('btn-reprint')) $('btn-reprint').addEventListener('click', () => { if (lastSale) printReceipt(lastSale); });
    try { autoPrint = (await api('/api/admin/store-info')).info.AutoPrintReceipt !== '0'; } catch (e) { }

    // two checks before the sale is saved: the items first, then the money
    if ($('pos-bar-btn')) $('pos-bar-btn').addEventListener('click', () => $('btn-generate').click());
    if ($('btn-generate')) $('btn-generate').addEventListener('click', () => {
        if (!lines.length) return toast('Add at least one item.', true);
        const method = $('pos-payment').selectedOptions[0] ? $('pos-payment').selectedOptions[0].textContent : '';
        const total = lines.reduce((a, l) => a + products.find(x => x.id === l.id).price * l.qty, 0);
        const isQr = $('pos-payment').value === 'qrph';
        const m = showModal(`<h2>Confirm this sale</h2><p class="sy-sub">Step 1: check the items. Step 2: confirm the money. The sale is saved only after both.</p>
          <ul class="sy-checklist">${lines.map(l => { const p = products.find(x => x.id === l.id); return `<li><label><input type="checkbox" class="ck-item"> ${esc(p.name)}${l.size ? ' (' + esc(l.size) + ')' : ''} x ${l.qty} <b>${peso(p.price * l.qty)}</b></label></li>`; }).join('')}</ul>
          <div class="sy-bigamt">${peso(total)}</div>
          ${isQr ? `<p class="sy-sub">Next, a QR code appears. The customer scans it and pays by GCash, Maya or bank app.</p><input type="checkbox" id="ck-pay" checked hidden>` : `<label class="sy-check-line"><input type="checkbox" id="ck-pay" disabled> I received ${peso(total)} by ${esc(method)}</label>`}
          <div class="sy-modal-actions"><button class="sy-btn ghost" type="button" id="ws-no">Back</button><button class="sy-btn accent" type="button" id="ws-go" disabled>${isQr ? 'Show payment QR' : 'Record sale'}</button></div>`);
        const items = [...m.el.querySelectorAll('.ck-item')], pay = m.el.querySelector('#ck-pay'), go = m.el.querySelector('#ws-go');
        const sync = () => { const ok = items.every(x => x.checked); pay.disabled = !ok && !isQr; if (!isQr && !ok) pay.checked = false; else if (isQr) pay.checked = true; go.disabled = !(ok && pay.checked); };
        items.forEach(x => x.addEventListener('change', sync)); pay.addEventListener('change', sync); m.el.querySelector('#ws-no').onclick = m.close;
        go.onclick = async () => {
            go.disabled = true;
            try {
                const res = await api('/api/admin/transactions/walkin', 'POST', { customerName: $('walkin-name').value, paymentMethod: $('pos-payment').value, items: lines.map(l => ({ productId: l.id, qty: l.qty, size: l.size })) });
                m.close(); $('order-number-text').textContent = 'TRANS-ID: ' + res.orderCode;
                if (res.qr) {
                    lines = []; $('walkin-name').value = ''; await loadProducts(); render();
                    SyQr.show(res.orderId, async ok => { await loadProducts(); render(); if (ok) { lastSale = res.orderId; if ($('btn-reprint')) $('btn-reprint').hidden = false; toast(`Sale ${res.orderCode} paid by QR: ${peso(res.total)}`); if (autoPrint) printReceipt(res.orderId); } else toast('Sale ' + res.orderCode + ' was cancelled.', true); }, true);
                    return;
                }
                toast(`Sale ${res.orderCode} recorded: ${peso(res.total)}`);
                lastSale = res.orderId; if ($('btn-reprint')) $('btn-reprint').hidden = false; if (autoPrint) printReceipt(res.orderId);
                lines = []; $('walkin-name').value = ''; await loadProducts(); render();
            } catch (e) { toast(e.message, true); go.disabled = false; await loadProducts(); }
        };
    });
    initHeader(null);
    try {
        const methods = (await api('/api/admin/payment-methods')).filter(m => m.isOn && m.code !== 'paymongo' && m.code !== 'paypal');   // online methods need the customer's own device, so only counter methods are offered here
        if ($('pos-payment')) $('pos-payment').innerHTML = methods.map(m => `<option value="${esc(m.code)}">${esc(m.label)}</option>`).join('') || '<option value="">No payment method enabled</option>';
        try { qrReady = (await api('/api/admin/qr/status')).ready; } catch (e) { }
        if (!qrReady && $('pos-payment')) [...$('pos-payment').options].forEach(o => { if (o.value === 'qrph') o.remove(); });
        await loadProducts(); render();
    } catch (e) { toast(e.message, true); }
})();