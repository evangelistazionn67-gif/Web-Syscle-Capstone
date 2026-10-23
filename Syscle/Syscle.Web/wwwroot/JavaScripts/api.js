let signedOutShown = false;
async function api(url, method = 'GET', body) {
    const opt = { method, headers: { 'X-Requested-With': 'syscle' } };
    if (body instanceof FormData) opt.body = body;
    else if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    const r = await fetch(url, opt);
    let data = null; try { data = await r.json(); } catch (e) { }
    if (r.status === 401) { handleSignedOut(r.headers.get('X-Session-Reason')); throw new Error('Please log in again.'); }
    if (!r.ok) { const err = new Error((data && data.error) || 'Something went wrong.'); err.status = r.status; err.data = data; throw err; }
    return data;
}
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const peso = n => '\u20b1' + Number(n).toLocaleString('en-PH', { maximumFractionDigits: 2 });
const fmtDate = d => d ? new Date(d).toLocaleDateString() : '-';
const fmtDateTime = d => d ? new Date(d).toLocaleString() : '-';
function toast(msg, isError, linkText, linkHref) {
    const t = document.createElement('div'); t.className = 'sy-toast' + (isError ? ' err' : ''); t.append(msg);
    if (linkHref) { const a = document.createElement('a'); a.href = linkHref; a.textContent = linkText; t.appendChild(a); }
    document.body.appendChild(t); setTimeout(() => t.remove(), linkHref ? 5000 : 3200);
}
async function logoutUser() { signedOutShown = true; try { await api('/api/auth/logout', 'POST'); } catch (e) { } location.href = '/index.html'; }

function showModal(html, onClose) {
    const ov = document.createElement('div'); ov.className = 'sy-ov';
    ov.innerHTML = '<div class="sy-ovbox" role="dialog" aria-modal="true"><button class="sy-ovx" aria-label="Close">&times;</button>' + html + '</div>';
    const close = () => { ov.remove(); document.removeEventListener('keydown', onKey); if (onClose) onClose(); };
    const onKey = e => { if (e.key === 'Escape') close(); };
    ov.addEventListener('click', e => { if (e.target === ov || e.target.closest('.sy-ovx')) close(); });
    document.addEventListener('keydown', onKey); document.body.appendChild(ov);
    return { el: ov, close };
}

async function refreshCartBadge() {
    const icon = document.querySelector('.cart-icon'); if (!icon) return;
    const link = icon.closest('a'); if (!link || link.style.display === 'none') return;
    try {
        const items = await api('/api/cart'), n = items.reduce((s, i) => s + i.qty, 0);
        let b = link.querySelector('.sy-badge');
        if (!b) { link.style.position = 'relative'; b = document.createElement('span'); b.className = 'sy-badge'; link.appendChild(b); }
        b.textContent = n; b.style.display = n ? 'flex' : 'none';
    } catch (e) { }
}

function addFooter() {
    if (document.querySelector('.sy-footer') || !document.querySelector('.cart-icon')) return;
    const f = document.createElement('footer'); f.className = 'sy-footer';
    f.innerHTML = '<div class="sy-fcols"><div><b>Web-Syscle</b><p>Shop &amp; Customize. Quality bicycle parts for every ride.</p></div>' +
        '<div><b>Shop</b><p><a href="homepage.html">All parts</a><br><a href="cart.html">My cart</a><br><a href="account.html">My account &amp; orders</a></p></div>' +
        '<div><b>How it works</b><p>1. Pick your parts<br>2. Review your cart<br>3. Pick up at the store and pay in Cash or GCash</p></div></div>' +
        '<div class="sy-fcopy">&copy; ' + new Date().getFullYear() + ' Web-Syscle. All rights reserved.</div>';
    document.body.appendChild(f);
}

function initHeader(onSearch) {
    const isShop = !!document.querySelector('.cart-icon');
    if (isShop) { refreshCartBadge(); addFooter(); }
    const input = document.getElementById('main-search-input');
    if (!input) return;
    const run = () => { if (onSearch) onSearch(input.value.trim().toLowerCase()); };
    const submit = () => { if (!onSearch && isShop) location.href = 'homepage.html?q=' + encodeURIComponent(input.value.trim()); else run(); };
    input.addEventListener('input', run);
    input.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
    const go = document.getElementById('execute-search'), clr = document.getElementById('clear-search');
    if (go) go.addEventListener('click', submit);
    if (clr) clr.addEventListener('click', () => { input.value = ''; input.focus(); run(); });
}

const BASKET_SVG = '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="9" cy="20" r="1.5"/><circle cx="18" cy="20" r="1.5"/><path d="M2 3h3l2.7 12.4a1 1 0 0 0 1 .8h8.9a1 1 0 0 0 1-.8L20.5 8H6"/></svg>';
const stockTag = s => s === 0 ? 'Sold out' : s <= 5 ? 'Only ' + s + ' left' : '';

function productCard(p) {
    return `<article class="sy-pcard" data-id="${p.id}">
    <div class="sy-pimg" data-open="1">${stockTag(p.stock) ? `<span class="sy-ptag">${stockTag(p.stock)}</span>` : ''}${p.image ? `<img src="${esc(p.image)}" alt="${esc(p.name)}" loading="lazy">` : '<span class="sy-pnone">No photo</span>'}</div>
    <div class="sy-pprice">${peso(p.price)}</div>
    <div class="sy-pname" data-open="1">${esc(p.name)}</div>
    <div class="sy-pbrand">${esc(p.itemType)}</div>
    ${p.sizes ? `<div class="sy-psizes">Sizes: ${p.sizes.split(',').map(s => esc(s.trim())).join(' &middot; ')}</div>` : ''}
    <div class="sy-pbuy"><span class="sy-pstock">${p.stock > 0 ? p.stock + ' in stock' : ''}</span>
      <button class="sy-add" data-open="1" aria-label="Buy ${esc(p.name)}"${p.stock > 0 ? '' : ' disabled'}><span>Buy</span>${BASKET_SVG}</button></div>
  </article>`;
}

function bindProductCards(box, getItems) {
    box.addEventListener('click', e => {
        const card = e.target.closest('.sy-pcard'); if (!card || !e.target.closest('[data-open]')) return;
        const p = getItems().find(x => x.id === +card.dataset.id); if (p) openProduct(p);
    });
}

function openProduct(p) {
    const ok = p.stock > 0, maxQ = Math.max(1, p.stock), sizes = (p.sizes || '').split(',').map(s => s.trim()).filter(Boolean);
    let variants = p.variants || []; let currentImage = p.image; let selectedColor = '';

    const m = showModal(`<div class="sy-pm">
    <div class="sy-pm-img"><img id="pm-img" src="${esc(currentImage)}" alt="${esc(p.name)}"></div>
    <div class="sy-pm-info">
      <div class="sy-pbrand">${esc(p.category || '')}${p.category ? ' &middot; ' : ''}${esc(p.itemType)}</div>
      <h2>${esc(p.name)}</h2>
      <div class="sy-pm-price">${peso(p.price)}</div>
      <div class="sy-pm-stock${p.stock <= 5 ? ' low' : ''}">${ok ? (p.stock <= 5 ? 'Only ' + p.stock + ' left' : 'In stock') : 'Sold out'}</div>
      
      ${variants.length ? `<div class="sy-pm-row"><span>Color</span><div class="sy-sizebtns">${variants.map(v => `<button type="button" class="sy-sizebtn" data-color="${esc(v.colorName)}" data-img="${esc(v.image)}">${esc(v.colorName)}</button>`).join('')}</div></div>` : ''}
      ${sizes.length ? `<div class="sy-pm-row"><span>Size</span><div class="sy-sizebtns">${sizes.map(s => `<button type="button" class="sy-sizebtn" data-size="${esc(s)}">${esc(s)}</button>`).join('')}</div></div>` : ''}
      
      <div class="sy-pm-row"><span>Quantity</span><div class="sy-qty"><button data-q="-1">-</button><span id="pm-qty">1</span><button data-q="1">+</button></div></div>
      <div class="sy-pm-total">Subtotal: <b id="pm-sub">${peso(p.price)}</b></div>
      
      <div class="sy-pm-actions"><button class="sy-btn ghost" id="pm-add"${ok ? '' : ' disabled'}>Add to Cart</button><button class="sy-btn accent" id="pm-buy"${ok ? '' : ' disabled'}>Buy Now</button></div>
    </div>
    <div style="grid-column:1/-1; border-top: 1px solid #ddd; margin-top: 15px; padding-top: 15px;">
      <h3>Reviews</h3>
      <div id="pm-reviews" style="max-height:150px; overflow-y:auto; margin-bottom:10px; font-size:14px;">Loading...</div>
      <div style="display:flex; gap:10px;">
        <select id="rev-rating" class="sy-input" style="width:100px; margin:0;"><option value="5">5 Stars</option><option value="4">4 Stars</option><option value="3">3 Stars</option><option value="2">2 Stars</option><option value="1">1 Star</option></select>
        <input id="rev-text" class="sy-input" placeholder="Write a review..." style="margin:0;">
        <button class="sy-btn" id="rev-submit">Post</button>
      </div>
    </div>
  </div>`);

    let q = 1, size = '';

    m.el.querySelectorAll('.sy-sizebtn').forEach(b => b.addEventListener('click', () => {
        const parent = b.parentElement;
        parent.querySelectorAll('.sy-sizebtn').forEach(x => x.classList.remove('on'));
        b.classList.add('on');
        if (b.dataset.color) { selectedColor = b.dataset.color; m.el.querySelector('#pm-img').src = b.dataset.img; }
        if (b.dataset.size) size = b.dataset.size;
    }));

    const upd = () => { m.el.querySelector('#pm-qty').textContent = q; m.el.querySelector('#pm-sub').textContent = peso(p.price * q); };
    m.el.querySelectorAll('[data-q]').forEach(b => b.addEventListener('click', () => { q = Math.max(1, Math.min(maxQ, q + (+b.dataset.q))); upd(); }));

    const add = async goCart => {
        if (sizes.length && !size) { toast('Please choose a size.', true); return; }
        if (variants.length && !selectedColor) { toast('Please choose a color.', true); return; }
        try {
            await api('/api/cart', 'POST', { productId: p.id, qty: q, size, color: selectedColor });
            m.close();
            if (goCart) location.href = 'cart.html';
            else { toast('Added to cart.', false, 'View cart', 'cart.html'); refreshCartBadge(); }
        } catch (e) { toast(e.message, true); }
    };
    m.el.querySelector('#pm-add').addEventListener('click', () => add(false));
    m.el.querySelector('#pm-buy').addEventListener('click', () => add(true));

    api(`/api/products/${p.id}/reviews`).then(reviews => {
        m.el.querySelector('#pm-reviews').innerHTML = reviews.length ? reviews.map(r => `<div style="border-bottom:1px solid #eee; padding:5px 0;"><b>${esc(r.username)}</b> (${r.rating}★) <small style="color:#666">${fmtDate(r.date)}</small><br>${esc(r.comment)}</div>`).join('') : 'No reviews yet. Be the first!';
    }).catch(() => m.el.querySelector('#pm-reviews').textContent = 'Could not load reviews.');

    m.el.querySelector('#rev-submit').addEventListener('click', async () => {
        try {
            await api(`/api/products/${p.id}/reviews`, 'POST', { rating: +m.el.querySelector('#rev-rating').value, comment: m.el.querySelector('#rev-text').value });
            toast('Review posted!'); m.close(); openProduct(p);
        } catch (e) { toast(e.message, true); }
    });
}

function sortItems(list, mode) {
    const a = list.slice();
    if (mode === 'low') a.sort((x, y) => x.price - y.price);
    else if (mode === 'high') a.sort((x, y) => y.price - x.price);
    else if (mode === 'name') a.sort((x, y) => x.name.localeCompare(y.name));
    return a;
}

function handleSignedOut(reason) {
    if (signedOutShown) return; signedOutShown = true;
    const msg = reason === 'suspended' ? 'Account suspended.' : (reason === 'replaced' || reason === 'changed') ? 'Signed in elsewhere.' : 'Session ended.';
    showModal(`<div class="sy-done"><h2>Signed Out</h2><p>${msg}</p><div class="sy-pm-actions"><a class="sy-btn accent" href="/index.html">Login</a></div></div>`, () => { location.href = '/index.html'; });
}
function startSessionWatch() { const ping = async () => { try { await api('/api/auth/ping'); } catch (e) { } }; setInterval(ping, 5000); document.addEventListener('visibilitychange', () => { if (!document.hidden) ping(); }); }
startSessionWatch();
