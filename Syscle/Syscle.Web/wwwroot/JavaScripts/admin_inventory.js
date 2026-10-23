(async function () {
    const $ = id => document.getElementById(id);
    if (!$('inventory-grid')) return;
    let items = [], cats = [], editId = null, editImage = '', q = '', catFilter = '', firstLoad = true;
    let currentVariants = []; // { ColorName, ImagePath, File }

    const tag = s => s === 0 ? 'Out of stock' : s <= 5 ? 'Only ' + s + ' left' : '';

    function render() {
        const rows = items.filter(p => (!catFilter || String(p.categoryId) === catFilter) && (p.name + ' ' + p.category + ' ' + p.itemType).toLowerCase().includes(q));
        $('inventory-count').textContent = rows.length + ' item(s)';
        $('inventory-grid').innerHTML = rows.length ? rows.map(p => `
      <article class="sy-pcard" data-id="${p.id}">
        <div class="sy-pimg">${tag(p.stock) ? `<span class="sy-ptag">${tag(p.stock)}</span>` : ''}${p.image ? `<img src="${esc(p.image)}" alt="${esc(p.name)}">` : '<span class="sy-pnone">No photo</span>'}</div>
        <div class="sy-pprice">${peso(p.price)}</div><div class="sy-pname">${esc(p.name)}</div><div class="sy-pbrand">${esc(p.category)} · ${esc(p.itemType)}</div>
        <div class="sy-pbuy"><div class="sy-qty"><button data-d="-1">-</button><span>${p.stock}</span><button data-d="1">+</button></div>
          <div class="sy-pactions"><button class="sy-btn small" data-edit="1">Edit</button><button class="sy-x" data-del="1">Delete</button></div></div>
      </article>`).join('') : '<div class="sy-empty">No items yet.</div>';
    }

    async function load() {
        try {
            [items, cats] = await Promise.all([api('/api/admin/products'), api('/api/categories')]);
            $('cat-filter').innerHTML = '<option value="">All categories</option>' + cats.map(c => `<option value="${c.id}">${esc(c.name)}</option>`).join('');
            $('cat-filter').value = catFilter; render();
            if (firstLoad) {
                firstLoad = false;
                const sp = new URLSearchParams(location.search);
                if (sp.get('cat')) { catFilter = sp.get('cat'); $('cat-filter').value = catFilter; render(); }
                if (sp.get('add')) openModal(null);
            }
        } catch (e) { toast(e.message, true); }
    }

    function renderVariants() {
        const box = $('variant-container');
        box.innerHTML = currentVariants.map((v, i) => `
      <div style="display:flex; gap:5px; margin-bottom:5px; align-items:center;">
        <input class="sy-input" style="margin:0; flex:1;" placeholder="Color Name" value="${esc(v.ColorName)}" onchange="updateVariantName(${i}, this.value)">
        <input type="file" class="sy-input" style="margin:0; width:150px;" accept="image/*" onchange="updateVariantFile(${i}, this)">
        ${v.ImagePath ? `<img src="${v.ImagePath}" style="height:30px; width:30px; object-fit:cover;">` : ''}
        <button type="button" class="sy-x" onclick="removeVariant(${i})">X</button>
      </div>
    `).join('');
    }

    window.updateVariantName = (i, val) => { currentVariants[i].ColorName = val; };
    window.updateVariantFile = (i, input) => { if (input.files[0]) currentVariants[i].File = input.files[0]; };
    window.removeVariant = (i) => { currentVariants.splice(i, 1); renderVariants(); };

    $('btn-add-variant').addEventListener('click', () => { currentVariants.push({ ColorName: '', ImagePath: '' }); renderVariants(); });

    function openModal(p) {
        if (!cats.length) return toast('Create a category first.', true);
        editId = p ? p.id : null; editImage = p && p.image ? p.image : '';
        currentVariants = p && p.variants ? p.variants.map(v => ({ ColorName: v.colorName, ImagePath: v.image })) : [];
        renderVariants();
        $('item-modal-title').textContent = p ? 'Edit item' : 'Add item';
        $('f-cat').innerHTML = cats.map(c => `<option value="${c.id}">${esc(c.name)}</option>`).join('');
        $('f-cat').value = p ? p.categoryId : (catFilter || cats[0].id);
        $('f-type').value = p ? p.itemType : ''; $('f-name').value = p ? p.name : '';
        $('f-price').value = p ? p.price : ''; $('f-stock').value = p ? p.stock : ''; $('f-sizes').value = p && p.sizes ? p.sizes : '';
        $('f-image').value = '';
        const preview = $('f-preview'); if (preview) { preview.src = editImage || ''; preview.style.display = editImage ? 'block' : 'none'; }
        $('item-modal').style.display = 'flex';
    }

    $('f-image').addEventListener('change', () => { const f = $('f-image').files[0]; const p = $('f-preview'); p.src = f ? URL.createObjectURL(f) : editImage; p.style.display = 'block'; });
    $('cat-filter').addEventListener('change', () => { catFilter = $('cat-filter').value; render(); });
    $('btn-add-item').addEventListener('click', () => openModal(null));
    $('f-cancel').addEventListener('click', () => { $('item-modal').style.display = 'none'; });

    $('f-save').addEventListener('click', async () => {
        const body = {
            categoryId: +$('f-cat').value, itemType: $('f-type').value, name: $('f-name').value,
            price: parseFloat($('f-price').value), stock: parseInt($('f-stock').value), sizes: $('f-sizes').value,
            variants: currentVariants.map(v => ({ ColorName: v.ColorName, ImagePath: v.ImagePath || '' }))
        };
        if (isNaN(body.price) || isNaN(body.stock)) return toast('Enter valid price/stock.', true);
        try {
            const res = await api(editId ? '/api/admin/products/' + editId : '/api/admin/products', editId ? 'PUT' : 'POST', body);
            const prodId = editId || res.id;

            const file = $('f-image').files[0];
            if (file) { const fd = new FormData(); fd.append('image', file); await api('/api/admin/products/' + prodId + '/image', 'POST', fd); }

            for (let i = 0; i < currentVariants.length; i++) {
                if (currentVariants[i].File) {
                    const fd = new FormData(); fd.append('image', currentVariants[i].File);
                    const r = await api('/api/admin/products/' + prodId + '/variant-image', 'POST', fd);
                    currentVariants[i].ImagePath = r.path;
                }
            }
            // Save again to attach variant image paths
            body.variants = currentVariants.map(v => ({ ColorName: v.ColorName, ImagePath: v.ImagePath }));
            await api('/api/admin/products/' + prodId, 'PUT', body);

            $('item-modal').style.display = 'none'; await load(); toast('Saved.');
        } catch (e) { toast(e.message, true); }
    });

    $('inventory-grid').addEventListener('click', async e => {
        const card = e.target.closest('[data-id]'); if (!card) return;
        const id = +card.dataset.id, p = items.find(x => x.id === id);
        try {
            const d = e.target.closest('[data-d]');
            if (d) { await api(`/api/admin/products/${id}/stock`, 'POST', { delta: +d.dataset.d }); await load(); }
            else if (e.target.dataset.edit) openModal(p);
            else if (e.target.dataset.del && confirm(`Delete "${p.name}"?`)) { await api('/api/admin/products/' + id, 'DELETE'); await load(); }
        } catch (err) { toast(err.message, true); }
    });

    initHeader(t => { q = t; render(); }); load();
})();