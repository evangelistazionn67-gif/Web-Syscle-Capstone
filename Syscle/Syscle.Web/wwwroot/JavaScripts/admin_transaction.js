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
            return `<div class="pos-dropdown-item"><div class="item-details"><strong>${esc(p.name)}</strong><span>${esc(p.category)} · ${peso(p.price)} · ${p.stock} in stock</span></div>
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
            box.insertAdjacentHTML('beforeend', `<div class="sy-line" data-id="${p.id}" data-size="${esc(l.size)}"><div class="sy-grow"><b>${label}</b><small>${peso(p.price)} each · ${p.stock} in stock</small></div>
        <div class="sy-qty"><button data-d="-1">-</button><span>${l.qty}</span><button data-d="1">+</button></div><b>${peso(p.price * l.qty)}</b><button class="sy-x" data-rm="1">Remove</button></div>`);
            if (bd) bd.insertAdjacentHTML('beforeend', `<div class="sy-row"><span>${esc(p.name)}${l.size ? ' [' + esc(l.size) + ']' : ''} (${peso(p.price)})</span><span>x ${l.qty}</span></div>`);
        });
        if ($('pos-total')) $('pos-total').textContent = 'Total Due: ' + peso(total);
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
    if ($('btn-generate')) $('btn-generate').addEventListener('click', async () => {
        if (!lines.length) return toast('Add at least one item.', true);
        try {
            const res = await api('/api/admin/transactions/walkin', 'POST', { customerName: $('walkin-name').value, paymentMethod: $('pos-payment').value, items: lines.map(l => ({ productId: l.id, qty: l.qty, size: l.size })) });
            $('order-number-text').textContent = 'TRANS-ID: ' + res.orderCode;
            toast(`Transaction ${res.orderCode} recorded: ${peso(res.total)}`);
            lines = []; $('walkin-name').value = ''; await loadProducts(); render();
        } catch (e) { toast(e.message, true); await loadProducts(); }
    });
    initHeader(null);
    try {
        const methods = (await api('/api/admin/payment-methods')).filter(m => m.isOn);
        if ($('pos-payment')) $('pos-payment').innerHTML = methods.map(m => `<option value="${esc(m.code)}">${esc(m.label)}</option>`).join('') || '<option value="">No payment method enabled</option>';
        await loadProducts(); render();
    } catch (e) { toast(e.message, true); }
})();