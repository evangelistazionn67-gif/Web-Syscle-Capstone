// CUSTOMER HOME: bike-type tiles, then a shelf of every part with filter buttons, sorting and an in-stock switch.
(async function () {
  const grid = document.getElementById('cat-grid'), shelf = document.getElementById('shelf-products'), chips = document.getElementById('shelf-chips'), count = document.getElementById('shelf-count');
  const sortEl = document.getElementById('shelf-sort'), stockEl = document.getElementById('shelf-instock'), input = document.getElementById('main-search-input');
  const start = new URLSearchParams(location.search).get('q') || '';
  let cats = [], items = [], q = start.toLowerCase(), filter = '', mode = '', inStock = false;
  if (input && start) input.value = start;
  function renderCats() {
    grid.innerHTML = '';
    const list = cats.filter(c => c.name.toLowerCase().includes(q));
    if (!list.length) { grid.innerHTML = '<div class="sy-empty">No categories found.</div>'; return; }
    list.forEach(cat => {
      const card = document.createElement('div');
      card.className = 'category-card';
      if (cat.image) card.style.backgroundImage = 'url(' + JSON.stringify(cat.image) + ')';
      const h = document.createElement('h3'); h.textContent = cat.name;
      card.appendChild(h);
      card.addEventListener('click', () => { location.href = 'category-view.html?cat=' + encodeURIComponent(cat.name); });
      grid.appendChild(card);
    });
  }
  function renderShelf() {
    chips.innerHTML = [''].concat(cats.map(c => c.name)).map(n => `<button class="sy-fbtn${filter === n ? ' on' : ''}" data-cat="${esc(n)}">${n ? esc(n) : 'All'}</button>`).join('');
    const list = sortItems(items.filter(p => (!filter || p.category === filter) && (!inStock || p.stock > 0) && (p.name + ' ' + p.itemType + ' ' + p.category).toLowerCase().includes(q)), mode);
    count.textContent = '(' + list.length + ')';
    shelf.innerHTML = list.map(productCard).join('') || '<div class="sy-empty">No parts found. Try another search or filter.</div>';
  }
  function bundleCard(b) {
    const bogo = b.items.length === 1 && b.items[0].qty === 2 && b.discountPercent === 50;
    const pics = b.items.flatMap(i => Array.from({ length: i.qty }, () => i));
    return `<article class="sy-bcard"><div class="sy-bsave">${bogo ? 'Buy 1 Get 1' : 'Save ' + b.discountPercent + '%'}${b.kind === 'Package' ? ' &middot; Bike package' : ''}${b.season ? ' &middot; ' + esc(b.season) : ''}</div>
      <div class="sy-bimgs">${pics.map(i => `<div class="sy-bimg">${i.image ? `<img src="${esc(i.image)}" alt="${esc(i.name)}" loading="lazy">` : ''}</div>`).join('<span class="sy-bplus">+</span>')}</div>
      ${b.image ? `<img class="sy-bcover" src="${esc(b.image)}" alt="${esc(b.name)}" loading="lazy">` : ''}
      ${b.description ? `<p class="sy-bdesc">${esc(b.description)}</p>` : ''}
      <div class="sy-bnames">${b.items.map(i => esc(i.name) + (i.qty > 1 ? ' x ' + i.qty : '')).join('<br>')}</div>
      <details class="sy-bdet"><summary>Details and what it fits</summary>${b.items.map(i => `<div class="sy-bdi"><b>${esc(i.name)}</b>${i.description ? `<p>${esc(i.description)}</p>` : ''}${i.specs ? specTable(i.specs) : ''}${i.fitTags ? `<p class="sy-muted">Fits: ${esc(i.fitTags)}</p>` : ''}</div>`).join('')}</details>
      <div class="sy-bprice"><s>${peso(b.listPrice)}</s> <b>${peso(b.price)}</b><small>You save ${peso(b.saving)}</small></div>
      <button class="sy-btn accent" data-addbundle="${b.id}">Add bundle to cart</button></article>`;
  }
  const bundleBox = document.getElementById('bundle-list');
  let bundleData = [];
  async function addBundle(id, sizes) {
    if (!window.SY_ME) { location.href = loginUrl(); return false; }
    try { await api('/api/cart/bundle', 'POST', { bundleId: id, qty: 1, sizes }); toast('Bundle added. The discount shows in your cart.', false, 'View cart', '/Pages/cart.html'); refreshCartBadge(); return true; }
    catch (err) { toast(err.message, true); return false; }
  }
  bundleBox.addEventListener('click', e => {
    const id = e.target.dataset.addbundle; if (!id) return;
    const b = bundleData.find(x => x.id === +id), sized = b ? b.items.filter(i => i.sizes) : [];
    if (!sized.length) { addBundle(+id, {}); return; }
    const m = showModal(`<div class="sy-done"><h2>Choose your sizes</h2>${sized.map(i => `<p><b>${esc(i.name)}</b><br><select data-pid="${i.id}" class="sy-input"><option value="">Choose a size</option>${i.sizes.split(',').map(z => `<option>${esc(z.trim())}</option>`).join('')}</select></p>`).join('')}<div class="sy-pm-actions"><button class="sy-btn accent" id="bz-go" type="button">Add bundle to cart</button></div></div>`);
    m.el.querySelector('#bz-go').addEventListener('click', async () => {
      const sizes = {}; m.el.querySelectorAll('select[data-pid]').forEach(z => { sizes[z.dataset.pid] = z.value; });
      if (await addBundle(+id, sizes)) m.close();
    });
  });
  chips.addEventListener('click', e => { const b = e.target.closest('[data-cat]'); if (b) { filter = b.dataset.cat; renderShelf(); } });
  if (sortEl) sortEl.addEventListener('change', () => { mode = sortEl.value; renderShelf(); });
  if (stockEl) stockEl.addEventListener('change', () => { inStock = stockEl.checked; renderShelf(); });
  try { [cats, items] = await Promise.all([api('/api/categories'), api('/api/products')]); renderCats(); renderShelf(); }
  catch (e) { location.href = '/Pages/error.html'; return; }
  try {
    const bundles = await api('/api/bundles');
    if (bundles.length) { bundleData = bundles; bundleBox.innerHTML = bundles.map(bundleCard).join(''); document.getElementById('bundles').hidden = false; }
  } catch (e) { /* bundles are optional */ }
  try {
    const fe = items.filter(p => p.featured && p.stock > 0).slice(0, 8);
    if (fe.length) { const fb = document.getElementById('featured-products'); fb.innerHTML = fe.map(productCard).join(''); document.getElementById('featured').hidden = false; bindProductCards(fb, () => items); }
    const pr = await api('/api/promotions'), strip = document.getElementById('promo-strip');
    if (strip && pr.codes.length) strip.innerHTML = pr.codes.map(c => `<div class="sy-banner info"><b>${esc(c.season)} promo:</b> use code <b>${esc(c.code)}</b> for ${c.percent}% off${c.endsAt ? ' until ' + esc(new Date(c.endsAt + 'T00:00').toLocaleDateString(undefined, { month: 'long', day: 'numeric' })) : ''}.</div>`).join('');
  } catch (e) { /* featured and promos are optional */ }
  initHeader(t => { q = t; renderCats(); renderShelf(); });
  if (location.hash === '#bundles') setTimeout(() => { const b = document.getElementById('bundles'); if (b && !b.hidden) b.scrollIntoView(); }, 300);
  bindProductCards(shelf, () => items);
})();
