(async function () {
    const $ = id => document.getElementById(id);
    if (!$('bundle-table-body')) return;
    let bundles = [], products = [], boxItems = [];

    async function load() {
        try {
            const [d, p] = await Promise.all([api('/api/admin/bundles'), api('/api/admin/products')]);
            bundles = d.bundles; products = p;
            $('nb-product').innerHTML = products.map(x => `<option value="${x.id}">${esc(x.name)} (${peso(x.price)})</option>`).join('');
            $('bundle-note').textContent = `The system auto-finds parts that haven't sold in ${d.slowDays}+ days.`;

            $('bundle-table-body').innerHTML = bundles.length ? bundles.map(b => `<tr data-id="${b.id}">
        <td style="text-align:left"><b>${esc(b.name)}</b><br><small>${b.auto ? 'Auto' : 'Manual'}</small></td>
        <td style="text-align:left">${b.items.map(i => esc(i.name) + ' x' + i.qty).join('<br>')}</td>
        <td>${peso(b.listPrice)}</td><td>${peso(b.price)}</td>
        <td>${b.discountPercent} %</td>
        <td><span class="sy-chip ${b.isActive && b.inStock ? 'ok' : 'warn'}">${!b.inStock ? 'Out of stock' : b.isActive ? 'Live' : 'Off'}</span></td>
        <td><div class="sy-actions"><button class="sy-btn small" data-toggle="1">${b.isActive ? 'Off' : 'On'}</button><button class="sy-x small" data-del="1">Delete</button></div></td></tr>`).join('')
                : '<tr><td colspan="7" class="sy-empty">No bundles yet.</td></tr>';
        } catch (e) { toast(e.message, true); }
    }

    function renderBox() {
        const box = $('bundle-box');
        if (!boxItems.length) { box.innerHTML = '<i style="color:#888;">No items added yet.</i>'; return; }
        box.innerHTML = boxItems.map((id, i) => {
            const p = products.find(x => x.id === id);
            return `<div style="display:flex; justify-content:space-between; border-bottom:1px solid #eee; padding:5px 0;">
        <span>${esc(p.name)} (${peso(p.price)})</span>
        <button class="sy-x" onclick="removeBoxItem(${i})" style="padding:2px 5px; font-size:12px;">X</button>
      </div>`;
        }).join('');
    }
    window.removeBoxItem = (i) => { boxItems.splice(i, 1); renderBox(); };

    $('btn-add-to-box').addEventListener('click', () => {
        const id = +$('nb-product').value;
        if (boxItems.includes(id)) return toast('Item already in bundle. Use Make BOGO for 2 of the same item.', true);
        boxItems.push(id); renderBox();
    });

    // Sends the bundle to the server. If the same bundle already exists the server says so and we ask before adding another.
    async function saveBundle(list, pct, okMsg, label) {
        try {
            await api('/api/admin/bundles', 'POST', { products: list, discountPercent: pct });
        } catch (e) {
            if (e.status === 409 && e.data && e.data.duplicate) {
                if (!confirm(e.message + '\n\nWould you like to add another one anyway?')) return false;
                try { await api('/api/admin/bundles', 'POST', { products: list, discountPercent: pct, force: true }); }
                catch (e2) { toast(e2.message, true); return false; }
            } else { toast(e.message, true); return false; }
        }
        await load(); toast(okMsg + ' Customers can see it on the shop home page.'); return true;
    }

    // BOGO = 2 of the same item. It is created straight away using the discount shown in the box (50% by default; change it first if you want another %).
    $('btn-bogo').addEventListener('click', async () => {
        const id = +$('nb-product').value; if (!id) return toast('Pick an item first.', true);
        const p = products.find(x => x.id === id);
        if (p && p.stock < 2) return toast('Only ' + p.stock + ' in stock - a BOGO needs at least 2.', true);
        await saveBundle([id, id], +$('nb-pct').value, 'BOGO created (' + $('nb-pct').value + '% off).');
    });

    $('nb-create').addEventListener('click', async () => {
        if (boxItems.length < 2) return toast('Add at least 2 items.', true);
        if (await saveBundle(boxItems.slice(), +$('nb-pct').value, 'Bundle created!')) { boxItems = []; renderBox(); }
    });

    $('bundle-table-body').addEventListener('click', async e => {
        const row = e.target.closest('tr[data-id]'); if (!row) return;
        const id = +row.dataset.id, b = bundles.find(x => x.id === id);
        try {
            if (e.target.dataset.toggle) { await api('/api/admin/bundles/' + id, 'PUT', { isActive: !b.isActive }); await load(); }
            else if (e.target.dataset.del && confirm('Delete?')) { await api('/api/admin/bundles/' + id, 'DELETE'); await load(); }
        } catch (err) { toast(err.message, true); }
    });

    $('nb-generate').addEventListener('click', async () => {
        try { const r = await api('/api/admin/bundles/generate', 'POST', {}); await load(); toast(r.made ? r.made + ' created.' : 'None found.'); } catch (e) { toast(e.message, true); }
    });

    initHeader(null); load();
})();