(async function () {
    const $ = id => document.getElementById(id);
    if (!$('bundle-table-body')) return;
    let bundles = [], products = [], boxItems = [], lastId = 0;

    async function load() {
        try {
            const [d, p] = await Promise.all([api('/api/admin/bundles'), api('/api/admin/products')]);
            bundles = d.bundles; products = p;
            $('nb-product').innerHTML = products.map(x => `<option value="${x.id}">${esc(x.name)} (${peso(x.price)})</option>`).join('');
            $('bundle-note').textContent = `The system auto-finds parts that haven't sold in ${d.slowDays}+ days.`;

            $('bundle-table-body').innerHTML = bundles.length ? bundles.map(b => `<tr data-id="${b.id}">
        <td style="text-align:left"><b>${esc(b.name)}</b><br><small>${b.kind === 'Package' ? 'Bike package' : 'Bundle'} &middot; ${b.auto ? 'Auto' : 'Manual'}${b.season ? ' &middot; ' + esc(b.season) : ''}${b.startsAt || b.endsAt ? '<br>' + esc((b.startsAt || 'now') + ' to ' + (b.endsAt || 'no end')) : ''}</small></td>
        <td style="text-align:left">${b.image ? `<img class="sy-thumb" src="${esc(b.image)}" alt="" style="margin-bottom:6px">` : ''}${b.description ? '<small>' + esc(b.description) + '</small><br>' : ''}${b.items.map(i => `<div class="sy-bitem">${i.image ? `<img class="sy-thumb sm" src="${esc(i.image)}" alt="">` : ''}<span>${esc(i.name)} x${i.qty}${i.specs ? `<br><small class="sy-muted">${esc(String(i.specs).split(/\r?\n/).slice(0, 2).join(' | '))}</small>` : ''}${i.fitTags ? `<br><small class="sy-muted">Fits: ${esc(i.fitTags)}</small>` : ''}</span></div>`).join('')}</td>
        <td>${peso(b.listPrice)}</td><td>${peso(b.price)}</td>
        <td>${b.discountPercent} %</td>
        <td><span class="sy-chip ${b.isActive && b.inStock && b.window === 'Live' ? 'ok' : 'warn'}">${!b.inStock ? 'Out of stock' : !b.isActive ? 'Off' : b.window === 'Live' ? 'Live' : b.window}</span></td>
        <td><div class="sy-actions"><button class="sy-btn small" data-toggle="1">${b.isActive ? 'Off' : 'On'}</button><button class="sy-btn small ghost" data-edit="1">Edit text/picture</button><button class="sy-x small" data-del="1">Delete</button></div></td></tr>`).join('')
                : '<tr><td colspan="7" class="sy-empty">No bundles yet.</td></tr>';
        } catch (e) { toast(e.message, true); }
    }

    function renderBox() {
        const box = $('bundle-box');
        if (!boxItems.length) { box.innerHTML = '<i style="color:#888;">No items added yet.</i>'; return; }
        box.innerHTML = boxItems.map((id, i) => {
            const p = products.find(x => x.id === id);
            return `<div style="display:flex; justify-content:space-between; border-bottom:1px solid #eee; padding:5px 0;">
        <span style="display:flex;gap:8px;align-items:flex-start">${p.image ? `<img class="sy-thumb sm" src="${esc(p.image)}" alt="">` : ''}<span><b>${esc(p.name)}</b> (${peso(p.price)})${p.description ? '<br><small class="sy-muted">' + esc(p.description.slice(0, 90)) + '</small>' : '<br><small class="sy-red">No description yet. Add one in Inventory.</small>'}${p.specs ? '<br><small class="sy-muted">' + esc(String(p.specs).split(/\r?\n/).slice(0, 2).join(' | ')) + '</small>' : ''}</span></span>
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

    const extra = () => ({ description: $('nb-desc').value.trim() || null, kind: $('nb-kind').value, season: $('nb-season').value || null, startsAt: $('nb-start').value || null, endsAt: $('nb-end').value || null });
    // Sends the bundle to the server. If the same bundle already exists the server says so and we ask before adding another.
    async function saveBundle(list, pct, okMsg, label) {
        try {
            lastId = (await api('/api/admin/bundles', 'POST', { products: list, discountPercent: pct, ...extra() })).id;
        } catch (e) {
            if (e.status === 409 && e.data && e.data.duplicate) {
                if (!confirm(e.message + '\n\nWould you like to add another one anyway?')) return false;
                try { lastId = (await api('/api/admin/bundles', 'POST', { products: list, discountPercent: pct, force: true, ...extra() })).id; }
                catch (e2) { toast(e2.message, true); return false; }
            } else { toast(e.message, true); return false; }
        }
        const f = $('nb-image').files[0];
        if (f && lastId) { try { const fd = new FormData(); fd.append('image', f); await api('/api/admin/bundles/' + lastId + '/image', 'POST', fd); } catch (e) { toast('The bundle was saved, but the picture failed: ' + e.message, true); } }
        $('nb-image').value = ''; $('nb-desc').value = '';
        await load(); toast(okMsg + ' Customers can see it on the shop home page.', false, 'View as customer', '/Pages/admin_preview.html#bundles'); return true;
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
            else if (e.target.dataset.edit) {
                const m = showModal(`<h2>Edit bundle</h2><label for="eb-desc">Description</label><textarea id="eb-desc" class="sy-input" rows="3" maxlength="500">${esc(b.description || '')}</textarea><label for="eb-img">New picture (optional)</label><input id="eb-img" type="file" accept="image/*"><div class="sy-modal-actions"><button class="sy-btn accent" id="eb-go" type="button">Save</button></div>`);
                m.el.querySelector('#eb-go').onclick = async () => { try { await api('/api/admin/bundles/' + id, 'PUT', { description: m.el.querySelector('#eb-desc').value.trim() || null }); const f = m.el.querySelector('#eb-img').files[0]; if (f) { const fd = new FormData(); fd.append('image', f); await api('/api/admin/bundles/' + id + '/image', 'POST', fd); } m.close(); await load(); toast('Saved.'); } catch (er) { toast(er.message, true); } };
            }
            else if (e.target.dataset.del && confirm('Delete?')) { await api('/api/admin/bundles/' + id, 'DELETE'); await load(); }
        } catch (err) { toast(err.message, true); }
    });

    $('nb-generate').addEventListener('click', async () => {
        try { const r = await api('/api/admin/bundles/generate', 'POST', {}); await load(); toast(r.made ? r.made + ' created.' : 'None found.'); } catch (e) { toast(e.message, true); }
    });

    try { const ss = await api('/api/admin/seasons'); $('nb-season').innerHTML = '<option value="">No season</option>' + ss.map(x => `<option value="${esc(x.name)}">${esc(x.name)}</option>`).join(''); } catch (e) { }
    initHeader(null); load();
})();