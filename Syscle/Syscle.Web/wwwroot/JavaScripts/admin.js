
(async function () {
  const grid = document.querySelector('.category-grid'), modal = document.getElementById('add-category-modal');
  grid.classList.add('sy-admingrid');
  let cats = [], counts = {}, pendingCat = null;
  const picInput = document.createElement('input');
  picInput.type = 'file'; picInput.accept = 'image/*'; picInput.style.display = 'none'; document.body.appendChild(picInput);
  picInput.addEventListener('change', async () => {
    const f = picInput.files[0]; if (!f || !pendingCat) return;
    const fd = new FormData(); fd.append('image', f);
    try { await api('/api/admin/categories/' + pendingCat + '/image', 'POST', fd); await load(); toast('Picture saved. Customers see it now.'); }
    catch (e) { toast(e.message, true); }
    picInput.value = '';
  });
  async function load() {
    try {
      const [c, p] = await Promise.all([api('/api/categories'), api('/api/admin/products')]);
      cats = c; counts = {}; p.forEach(x => { counts[x.categoryId] = (counts[x.categoryId] || 0) + 1; });
      render();
    } catch (e) { toast(e.message, true); }
  }
  function render() {
    grid.innerHTML = '';
    cats.forEach(cat => {
      const card = document.createElement('div');
      card.className = 'category-card default-category';
      if (cat.image) card.style.backgroundImage = 'url(' + JSON.stringify(cat.image) + ')';
      const h = document.createElement('h3'); h.textContent = cat.name;
      const del = document.createElement('div'); del.className = 'delete-category-btn'; del.innerHTML = '<i class="bi bi-x-lg" aria-hidden="true"></i>'; del.title = 'Delete category';
      del.addEventListener('click', async e => {
        e.stopPropagation();
        if (!confirm(`Delete the "${cat.name}" category and every item inside it? Past orders are kept.`)) return;
        try { await api('/api/admin/categories/' + cat.id, 'DELETE'); await load(); toast('Category deleted.'); } catch (err) { toast(err.message, true); }
      });
      const bar = document.createElement('div'); bar.className = 'sy-catbar';
      bar.innerHTML = `<span class="sy-catbtn" data-pic="1">${cat.image ? 'Change picture' : 'Add picture'}</span><a class="sy-catbtn" href="admin_inventory.html?cat=${cat.id}">Items (${counts[cat.id] || 0})</a><a class="sy-catbtn pri" href="admin_inventory.html?cat=${cat.id}&add=1">+ Add item</a>`;
      bar.addEventListener('click', e => { e.stopPropagation(); if (e.target.closest('[data-pic]')) { pendingCat = cat.id; picInput.click(); } });
      card.append(h, del, bar); grid.appendChild(card);
    });
    const add = document.createElement('div');
    add.className = 'category-card add-category'; add.innerHTML = '<h3>+ Add Bike Category</h3>';
    add.addEventListener('click', () => { modal.style.display = 'flex'; });
    grid.appendChild(add);
  }
  const addBtn = document.getElementById('btn-add-category');
  if (addBtn) addBtn.addEventListener('click', () => { modal.style.display = 'flex'; });
  document.getElementById('close-modal-btn').addEventListener('click', () => { modal.style.display = 'none'; });
  document.getElementById('btn-save-category').addEventListener('click', async () => {
    const name = document.getElementById('new-category-name').value.trim(), file = document.getElementById('new-category-file').files[0];
    if (!name) return toast('Please enter a category title.', true);
    const fd = new FormData(); fd.append('name', name); if (file) fd.append('image', file);
    try {
      await api('/api/admin/categories', 'POST', fd);
      document.getElementById('new-category-name').value = ''; document.getElementById('new-category-file').value = '';
      modal.style.display = 'none'; await load(); toast('Category added. Now add items to it.');
    } catch (e) { toast(e.message, true); }
  });
  initHeader(null); load();
})();
