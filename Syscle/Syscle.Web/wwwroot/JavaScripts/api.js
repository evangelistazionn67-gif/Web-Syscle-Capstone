let signedOutShown = false;
async function api(url, method = 'GET', body) {
    const opt = { method, headers: { 'X-Requested-With': 'syscle', 'X-Area': window.SY_AREA || (document.body.dataset.area === 'admin' ? 'admin' : 'shop') } };
    if (body instanceof FormData) opt.body = body;
    else if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    const r = await fetch(url, opt);
    let data = null; try { data = await r.json(); } catch (e) { }
    if (r.status === 401 && window.SY_ME === null) { const g = new Error('Please log in first.'); g.status = 401; g.guest = true; throw g; }
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
const loginUrl = () => '/login.html?next=' + encodeURIComponent(location.pathname + location.search);
const chipClass = st => ({ Completed: 'ok', Ready: 'ok', Preparing: 'info', 'Out for Delivery': 'info', Pending: 'warn', Cancelled: 'bad' }[st] || 'mute');
const statusChip = st => `<span class="sy-chip ${chipClass(st)}">${esc(st)}</span>`;
const payChip = o => o.refundStatus === 'Refunded' ? '<span class="sy-chip mute">Refunded</span>' : o.paymentStatus === 'Paid' ? '<span class="sy-chip ok">Paid</span>' : o.status === 'Cancelled' ? '' : '<span class="sy-chip warn">Unpaid</span>';
const stars = r => r ? '<span class="st" aria-label="' + r + ' out of 5">' + '<i class="bi bi-star-fill"></i>'.repeat(Math.round(r)) + '<i class="bi bi-star"></i>'.repeat(5 - Math.round(r)) + '</span>' : '';
const ico = (n, c) => '<i class="bi bi-' + n + (c ? ' ' + c : '') + '" aria-hidden="true"></i>';
let _lookups = null;
async function getLookups() { if (!_lookups) { try { _lookups = await fetch('/api/lookups').then(r => r.json()); } catch (e) { _lookups = { itemTypes: [], colors: [], bikeTypes: [], sizeSets: [] }; } } return _lookups; }
// "Label: value" lines become a two-column table
function specTable(specs) {
    const rows = String(specs || '').split(/\r?\n/).map(l => l.trim()).filter(Boolean).map(l => { const i = l.indexOf(':'); return i > 0 ? [l.slice(0, i).trim(), l.slice(i + 1).trim()] : ['', l]; });
    return rows.length ? '<table class="sy-spec"><tbody>' + rows.map(r => '<tr>' + (r[0] ? '<th>' + esc(r[0]) + '</th><td>' + esc(r[1]) + '</td>' : '<td colspan="2">' + esc(r[1]) + '</td>') + '</tr>').join('') + '</tbody></table>' : '';
}
const tagList = t => String(t || '').split(',').map(x => x.trim()).filter(Boolean);

function showModal(html, onClose) {
    const ov = document.createElement('div'); ov.className = 'sy-ov';
    ov.innerHTML = '<div class="sy-ovbox" role="dialog" aria-modal="true"><button class="sy-ovx" aria-label="Close"><i class="bi bi-x-lg" aria-hidden="true"></i></button>' + html + '</div>';
    const close = () => { ov.remove(); document.removeEventListener('keydown', onKey); if (onClose) onClose(); };
    const onKey = e => { if (e.key === 'Escape') close(); };
    ov.addEventListener('click', e => { if (e.target === ov || e.target.closest('.sy-ovx')) close(); });
    document.addEventListener('keydown', onKey); document.body.appendChild(ov);
    return { el: ov, close };
}

async function refreshCartBadge() {
    await (window.SY_READY || Promise.resolve());
    if (!window.SY_ME) return;
    const icon = document.querySelector('.cart-icon'); if (!icon) return;
    const link = icon.closest('a'); if (!link) return;
    try {
        const items = await api('/api/cart'), n = items.reduce((s, i) => s + i.qty, 0);
        let b = link.querySelector('.sy-badge');
        if (!b) { b = document.createElement('span'); b.className = 'sy-badge'; link.appendChild(b); }
        b.textContent = n; b.classList.toggle('show', n > 0); b.style.display = n ? 'flex' : 'none';
    } catch (e) { }
}

function initHeader(onSearch) {
    (window.SY_READY || Promise.resolve()).then(() => {
        const isShop = document.body.dataset.area === 'shop';
        if (isShop) refreshCartBadge();
        const input = document.getElementById('main-search-input');
        if (!input) return;
        const run = () => { if (onSearch) onSearch(input.value.trim().toLowerCase()); };
        const submit = () => { if (!onSearch && isShop) location.href = '/Pages/homepage.html?q=' + encodeURIComponent(input.value.trim()); else run(); };
        input.addEventListener('input', run);
        input.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
        const go = document.getElementById('execute-search'), clr = document.getElementById('clear-search');
        if (go) go.addEventListener('click', submit);
        if (clr) clr.addEventListener('click', () => { input.value = ''; input.focus(); run(); });
        const q0 = new URLSearchParams(location.search).get('q'); if (q0 && !input.value) input.value = q0;
    });
}

const BASKET_SVG = '<i class="bi bi-cart-plus" aria-hidden="true"></i>';
const LOW = () => { const v = parseInt((window.SY_STORE || {}).LowStockThreshold); return isNaN(v) ? 5 : v; };
const stockTag = s => s === 0 ? 'Sold out' : s <= LOW() ? 'Only ' + s + ' left' : '';

function productCard(p) {
    const tag = p.stock === 0 ? '<span class="sy-ptag out">Sold out</span>' : p.stock <= LOW() ? `<span class="sy-ptag">Only ${p.stock} left</span>` : '';
    return `<article class="sy-pcard" data-id="${p.id}">
    <div class="sy-pimg" data-open="1">${tag}${p.image ? `<img src="${esc(p.image)}" alt="${esc(p.name)}" loading="lazy">` : '<span class="sy-pnone">No photo</span>'}</div>
    <div class="sy-pbrand">${esc(p.itemType)}</div>
    <div class="sy-pname" data-open="1">${esc(p.name)}</div>
    <div class="sy-prate">${p.reviews ? stars(p.rating) + ' ' + p.rating + ' (' + p.reviews + ')' : '<span>No reviews yet</span>'}${p.sold ? ' &middot; ' + p.sold + ' sold' : ''}</div>
    <div class="sy-pprice">${peso(p.price)}</div>
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
    const variants = p.variants || []; let selectedColor = '', size = '', q = 1;
    const m = showModal(`<div class="sy-pm">
    <div class="sy-pm-img"><img id="pm-img" src="${esc(p.image)}" alt="${esc(p.name)}"></div>
    <div class="sy-pm-info">
      <div class="sy-pbrand">${esc(p.category || '')}${p.category ? ' &middot; ' : ''}${esc(p.itemType)}</div>
      <h2>${esc(p.name)}</h2>
      <div class="sy-prate">${p.reviews ? stars(p.rating) + ' ' + p.rating + ' (' + p.reviews + ' reviews)' : 'No reviews yet'}${p.sold ? ' &middot; ' + p.sold + ' sold' : ''}</div>
      <div class="sy-pm-price">${peso(p.price)}</div>
      <div class="sy-pm-stock${p.stock <= LOW() ? ' low' : ''}">${ok ? (p.stock <= LOW() ? 'Only ' + p.stock + ' left' : 'In stock (' + p.stock + ')') : 'Sold out'}</div>
      ${variants.length ? `<div class="sy-pm-row"><span>Color</span><select class="sy-select" id="pm-color"><option value="">Choose a color</option>${variants.map(v => `<option value="${esc(v.colorName)}" data-img="${esc(v.image)}">${esc(v.colorName)}</option>`).join('')}</select></div>` : ''}
      ${sizes.length ? `<div class="sy-pm-row"><span>Size</span><select class="sy-select" id="pm-size"><option value="">Choose a size</option>${sizes.map(s => `<option>${esc(s)}</option>`).join('')}</select></div>` : ''}
      <div class="sy-pm-row"><span>Quantity</span><div class="sy-qty"><button type="button" data-q="-1" aria-label="Less">-</button><span id="pm-qty">1</span><button type="button" data-q="1" aria-label="More">+</button></div></div>
      <div class="sy-pm-total">Subtotal: <b id="pm-sub">${peso(p.price)}</b></div>
      <div class="sy-pm-actions"><button class="sy-btn ghost" id="pm-add"${ok ? '' : ' disabled'}>Add to Cart</button><button class="sy-btn accent" id="pm-buy"${ok ? '' : ' disabled'}>Buy Now</button></div>
      ${window.SY_ME ? '' : '<p class="sy-sub" style="margin:12px 0 0">You are browsing as a guest. <a href="' + loginUrl() + '">Log in</a> to buy.</p>'}
    </div>
    <div class="sy-pm-detail" id="pm-detail"></div>
    <div class="sy-pm-rev"><h3>Reviews</h3><div class="list" id="pm-reviews">Loading...</div><div id="pm-revform"></div></div></div>`);
    const $ = id => m.el.querySelector(id);
    renderDetail($('#pm-detail'), p);
    if ($('#pm-color')) $('#pm-color').addEventListener('change', e => { selectedColor = e.target.value; const o = e.target.selectedOptions[0]; if (o && o.dataset.img) $('#pm-img').src = o.dataset.img; });
    if ($('#pm-size')) $('#pm-size').addEventListener('change', e => { size = e.target.value; });
    const upd = () => { $('#pm-qty').textContent = q; $('#pm-sub').textContent = peso(p.price * q); };
    m.el.querySelectorAll('[data-q]').forEach(b => b.addEventListener('click', () => { q = Math.max(1, Math.min(maxQ, q + (+b.dataset.q))); upd(); }));
    const add = async goCart => {
        if (!window.SY_ME) { location.href = loginUrl(); return; }
        if (sizes.length && !size) { toast('Please choose a size.', true); return; }
        if (variants.length && !selectedColor) { toast('Please choose a color.', true); return; }
        try {
            await api('/api/cart', 'POST', { productId: p.id, qty: q, size, color: selectedColor });
            m.close();
            if (goCart) location.href = '/Pages/cart.html';
            else { toast('Added to cart.', false, 'View cart', '/Pages/cart.html'); refreshCartBadge(); }
        } catch (e) { toast(e.message, true); }
    };
    $('#pm-add').addEventListener('click', () => add(false));
    $('#pm-buy').addEventListener('click', () => add(true));
    api('/api/products/' + p.id + '/reviews').then(reviews => {
        $('#pm-reviews').innerHTML = reviews.length ? reviews.map(r => `<div class="it"><b>${esc(r.username)}</b> ${stars(r.rating)} <small class="sy-muted">${fmtDate(r.date)}</small><br>${esc(r.comment)}</div>`).join('') : 'No reviews yet. Be the first!';
    }).catch(() => { $('#pm-reviews').textContent = 'Could not load reviews.'; });
    // reviews: only a customer who bought the item (and received it) can write one
    const rf = $('#pm-revform');
    if (!window.SY_ME) rf.innerHTML = '<p class="sy-muted sy-rev-note">' + ico('lock') + ' Log in to see if you can review this item.</p>';
    else api('/api/products/' + p.id + '/review-status').then(st => {
        if (st.reviewed) rf.innerHTML = '<p class="sy-muted sy-rev-note">' + ico('check-circle') + ' You already reviewed this item. Thank you!</p>';
        else if (!st.canReview) rf.innerHTML = '<p class="sy-muted sy-rev-note">' + ico('bag-check') + ' Only customers who bought this item and received it can write a review.</p>';
        else {
            rf.innerHTML = '<div class="form"><select id="rev-rating" class="sy-input" aria-label="Rating"><option value="5">5 stars</option><option value="4">4 stars</option><option value="3">3 stars</option><option value="2">2 stars</option><option value="1">1 star</option></select><input id="rev-text" class="sy-input" placeholder="Write a review..." maxlength="300"><button class="sy-btn" id="rev-submit">Post</button></div>';
            $('#rev-submit').addEventListener('click', async () => {
                try { await api('/api/products/' + p.id + '/reviews', 'POST', { rating: +$('#rev-rating').value, comment: $('#rev-text').value }); toast('Review posted!'); m.close(); openProduct(p); }
                catch (e) { toast(e.message, true); }
            });
        }
    }).catch(() => { });
}

// description / specifications / "will it fit my bike?" tabs inside the product pop-up
async function renderDetail(host, p) {
    const fit = tagList(p.fitTags), hasSpecs = !!String(p.specs || '').trim(), hasDesc = !!String(p.description || '').trim();
    const tabs = [];
    if (hasDesc) tabs.push(['desc', 'Description', '<p class="sy-desc">' + esc(p.description).replace(/\n/g, '<br>') + '</p>']);
    if (hasSpecs) tabs.push(['spec', 'Specifications', specTable(p.specs)]);
    tabs.push(['fit', 'Will it fit?', '<div id="pm-fit"></div>']);
    host.innerHTML = '<div class="sy-tabs" role="tablist">' + tabs.map((t, i) => '<button type="button" role="tab" class="' + (i ? '' : 'on') + '" data-t="' + t[0] + '">' + t[1] + '</button>').join('') + '</div>' +
        tabs.map((t, i) => '<div class="sy-tabpane" role="tabpanel" data-p="' + t[0] + '"' + (i ? ' hidden' : '') + '>' + t[2] + '</div>').join('');
    host.querySelectorAll('[data-t]').forEach(b => b.addEventListener('click', () => {
        host.querySelectorAll('[data-t]').forEach(x => x.classList.toggle('on', x === b));
        host.querySelectorAll('[data-p]').forEach(x => x.hidden = x.dataset.p !== b.dataset.t);
    }));
    const box = host.querySelector('#pm-fit'), lk = await getLookups(); let mine = ''; try { mine = localStorage.getItem('sy.bike') || ''; } catch (e) { }
    const verdict = bike => {
        if (!bike) return '<p class="sy-muted">Pick your bike type to see if this part is meant for it.</p>';
        if (!fit.length) return '<p class="sy-fitnote warn">' + ico('info-circle') + ' The store did not list the bike types for this part. Check the specifications, or message the store before you buy.</p>';
        return fit.some(f => f.toLowerCase() === bike.toLowerCase()) ? '<p class="sy-fitnote ok">' + ico('check-circle') + ' Made for <b>' + esc(bike) + '</b> bikes.</p>' : '<p class="sy-fitnote bad">' + ico('exclamation-triangle') + ' Not listed for <b>' + esc(bike) + '</b> bikes. It is listed for: ' + fit.map(esc).join(', ') + '.</p>';
    };
    const opts = (lk.bikeTypes || []).map(b => '<option' + (b === mine ? ' selected' : '') + '>' + esc(b) + '</option>').join('');
    box.innerHTML = (fit.length ? '<p class="sy-fitlist">Listed for: ' + fit.map(f => '<span class="sy-chip info">' + esc(f) + '</span>').join(' ') + '</p>' : '') +
        (opts ? '<label for="pm-bike">My bike type</label><select id="pm-bike" class="sy-input"><option value="">Choose...</option>' + opts + '</select>' : '') + '<div id="pm-fitres">' + verdict(mine) + '</div>';
    const sel = box.querySelector('#pm-bike');
    if (sel) sel.addEventListener('change', () => { try { localStorage.setItem('sy.bike', sel.value); } catch (e) { } box.querySelector('#pm-fitres').innerHTML = verdict(sel.value); });
}

function sortItems(list, mode) {
    const a = list.slice();
    if (mode === 'low') a.sort((x, y) => x.price - y.price);
    else if (mode === 'high') a.sort((x, y) => y.price - x.price);
    else if (mode === 'top') a.sort((x, y) => (y.sold || 0) - (x.sold || 0));
    else if (mode === 'rated') a.sort((x, y) => (y.rating || 0) - (x.rating || 0) || (y.reviews || 0) - (x.reviews || 0));
    else if (mode === 'name') a.sort((x, y) => x.name.localeCompare(y.name));
    return a;
}

function handleSignedOut(reason) {
    if (signedOutShown) return; signedOutShown = true;
    const msg = reason === 'suspended' ? 'Account suspended.' : (reason === 'replaced' || reason === 'changed') ? 'Signed in elsewhere.' : reason === 'idle' ? 'You were signed out because nobody was working for a while.' : 'Session ended.';
    showModal(`<div class="sy-done"><h2>Signed Out</h2><p>${msg}</p><div class="sy-pm-actions"><a class="sy-btn accent" href="/login.html">Log in</a></div></div>`, () => { location.href = '/login.html'; });
}
function startSessionWatch() { (window.SY_READY || Promise.resolve(window.SY_ME)).then(me => { if (!me) return; watch(); }); }
function watch() { const ping = async () => { try { await api('/api/auth/ping'); } catch (e) { } }; setInterval(ping, 5000); document.addEventListener('visibilitychange', () => { if (!document.hidden) ping(); }); }
startSessionWatch();

// Chart helper: the server only returns days/months/years that had sales, so fill the empty ones with zero (otherwise the chart skips them).
function fillSeries(series, period, from, to) {
    const iso = d => d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
    const m = new Map((series || []).map(s => [s.label, s])), out = [], d = new Date(from + 'T00:00'), end = new Date(to + 'T00:00');
    if (isNaN(d) || isNaN(end)) return series || [];
    const add = k => out.push(m.get(k) || { label: k, sales: 0, orders: 0 });
    if (period === 'daily') for (; d <= end && out.length < 400; d.setDate(d.getDate() + 1)) add(iso(d));
    else if (period === 'weekly') {
        const key = x => { const t = new Date(x.getFullYear(), x.getMonth(), x.getDate()); t.setDate(t.getDate() + 3 - ((t.getDay() + 6) % 7)); const w1 = new Date(t.getFullYear(), 0, 4); const wk = 1 + Math.round(((t - w1) / 864e5 - 3 + ((w1.getDay() + 6) % 7)) / 7); return t.getFullYear() + '-W' + String(wk).padStart(2, '0'); };
        d.setDate(d.getDate() - ((d.getDay() + 6) % 7)); for (; d <= end && out.length < 400; d.setDate(d.getDate() + 7)) add(key(d));
    }
    else if (period === 'monthly') { d.setDate(1); for (; d <= end && out.length < 400; d.setMonth(d.getMonth() + 1)) add(iso(d).slice(0, 7)); }
    else for (let y = d.getFullYear(); y <= end.getFullYear() && out.length < 400; y++) add(String(y));
    return out;
}
