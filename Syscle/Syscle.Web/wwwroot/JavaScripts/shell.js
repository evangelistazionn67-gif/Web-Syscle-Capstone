/* shell.js - builds the same header (shop) or sidebar (admin) on every page, plus the notification bell.
   A page chooses its layout with <body data-area="shop|admin|shared|auth">. "shared" pages (account, settings) switch by role.
   Optional: data-search="placeholder text" shows the search box; data-nav="file.html" marks the active menu item.
   Icons come from the Bootstrap Icons web font (CDN). The store name, tagline, address, phone and hours come from /api/store-info,
   so the header and footer follow what the admin typed in Store-front instead of being written into the page. */
(function () {
  const B = document.body, A0 = B.dataset.area || 'shop';

  /* ---------- icons (Bootstrap Icons, loaded once) ---------- */
  if (!document.querySelector('link[data-sy-icons]')) {
    const l = document.createElement('link'); l.rel = 'stylesheet'; l.dataset.syIcons = '1';
    l.href = 'https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css'; document.head.appendChild(l);
  }
  const bi = (n, cls) => `<i class="bi bi-${n}${cls ? ' ' + cls : ''}" aria-hidden="true"></i>`;
  window.syIcon = bi;

  const ADMIN_NAV = [
    ['Overview'],
    ['Dashboard', 'adminhomepage.html', 'speedometer2'],
    ['Product insights', 'admin_insights.html', 'graph-up-arrow'],
    ['Reports', 'admin_reports.html', 'bar-chart-line'],
    ['Sales'],
    ['Orders', 'admin_transaction_history.html', 'receipt', 'orders'],
    ['Walk-in sale', 'admin_transaction.html', 'upc-scan'],
    ['Promo codes', 'admin_promos.html', 'ticket-perforated'],
    ['Bundles', 'admin_bundles.html', 'box-seam'],
    ['Catalog'],
    ['Inventory', 'admin_inventory.html', 'boxes', 'stock'],
    ['Stock movement', 'admin_stock.html', 'arrow-left-right'],
    ['QR labels', 'admin_labels.html', 'qr-code'],
    ['Store-front', 'admin_storefront.html', 'shop'],
    ['Reviews', 'admin_reviews.html', 'star'],
    ['People'],
    ['Messages', 'admin_messages.html', 'chat-dots', 'msg'],
    ['Accounts', 'admin_account_management.html', 'people'],
    ['Setup'],
    ['Payment setup', 'admin_payment_setup.html', 'credit-card'],
    ['Lists and rules', 'admin_lists.html', 'sliders'],
    ['Preview store', 'admin_preview.html', 'eye'],
    ['Settings', 'settings.html', 'gear']
  ];
  const file = () => location.pathname.split('/').pop() || 'index.html';
  const e = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const lsGet = k => { try { return JSON.parse(localStorage.getItem(k) || 'null'); } catch (x) { return null; } };
  const lsSet = (k, v) => { try { localStorage.setItem(k, JSON.stringify(v)); } catch (x) { } };
  const ssGet = k => { try { return sessionStorage.getItem(k); } catch (x) { return null; } };
  const ssSet = (k, v) => { try { sessionStorage.setItem(k, v); } catch (x) { } };
  /* the page tells the server which portal it belongs to ("shop" or "admin"), so the customer login and the admin login never get mixed up */
  let AREA = A0 === 'admin' ? 'admin' : 'shop';
  const get = async u => { try { const r = await fetch(u, { headers: { 'X-Requested-With': 'syscle', 'X-Area': AREA } }); return r.ok ? await r.json() : null; } catch (x) { return null; } };
  const fmtP = n => '₱' + Number(n).toLocaleString('en-PH', { maximumFractionDigits: 2 });

  window.SY_READY = (async () => {
    const [who, store] = await Promise.all([get('/api/auth/whoami'), get('/api/store-info')]);
    window.SY_WHO = who; window.SY_STORE = store || {};
    let area = A0;
    if (area === 'shared') area = who && who.admin ? 'admin' : 'shop';
    if (area === 'admin' || area === 'shop') AREA = area; else AREA = 'shop';
    window.SY_AREA = AREA;
    const me = await get('/api/auth/me'); window.SY_ME = me; // null = guest (for this portal)
    const admin = !!me && (me.role === 'Admin' || me.role === 'SuperAdmin');
    B.dataset.area = area;
    if (area === 'auth') return me;
    if (area === 'admin') buildAdmin(me); else buildShop(me, admin, who);
    if (me) startBell(me, area === 'admin');
    wireMenus();
    if (area === 'admin' && me && who && who.idleSeconds) startIdleTimer(who.idleSeconds);
    if (area === 'shop') promoPopup();
    return me;
  })();

  const storeName = () => (window.SY_STORE && window.SY_STORE.StoreName) || 'Web-Syscle';
  const logoHtml = () => { const n = storeName(); return n === 'Web-Syscle' ? 'Web<i>-</i>Syscle' : e(n); };
  const tagline = () => (window.SY_STORE && window.SY_STORE.StoreTagline) || 'Shop & Customize';

  const accountMenu = (me, admin) => me ? `<div class="sy-wrapr"><button class="sy-ibtn" id="sy-umenu" aria-label="Account menu" aria-haspopup="true">${bi('person-circle')}</button>
      <div class="sy-menu" id="sy-umenu-box"><div class="who"><b>${e(me.username)}</b><span>${e(me.email || '')} &middot; ${admin ? e(me.role) : 'Customer'}</span></div>
      <a href="/Pages/account.html">My account</a>${admin ? '' : '<a href="/Pages/orders.html">My orders</a>'}<a href="/Pages/settings.html">Settings</a>
      <button type="button" onclick="logoutUser()">Log out</button></div></div>` : '';
  const bellHtml = `<div class="sy-wrapr"><button class="sy-ibtn" id="sy-bell" aria-label="Notifications" aria-haspopup="true">${bi('bell')}<span class="sy-badge" id="sy-bell-n">0</span></button>
      <div class="sy-note" id="sy-note"><header>Notifications <button type="button" id="sy-readall">Mark all as read</button></header><div class="sy-nlist" id="sy-nlist"><div class="sy-nempty">Loading...</div></div></div></div>`;
  const searchHtml = ph => `<div class="sy-search" role="search"><input type="text" id="main-search-input" placeholder="${e(ph)}" aria-label="Search" autocomplete="off">
      <button type="button" id="clear-search" aria-label="Clear search">${bi('x-lg')}</button><button type="button" id="execute-search" aria-label="Search">${bi('search')}</button></div>`;

  function footerHtml() {
    const s = window.SY_STORE || {}, rows = [];
    if (s.StoreAddress) rows.push(`${bi('geo-alt')} ${e(s.StoreAddress)}`);
    if (s.StorePhone) rows.push(`${bi('telephone')} ${e(s.StorePhone)}`);
    if (s.StoreHours) rows.push(`${bi('clock')} ${e(s.StoreHours)}`);
    if (s.PickupNote) rows.push(`${bi('bag-check')} ${e(s.PickupNote)}`);
    return `<div class="sy-fcols"><div><b>${e(storeName())}</b><p>${e(s.StoreTagline ? s.StoreTagline : 'Quality bicycle parts for every ride. Order online, pick up in store.')}</p></div>
        <div><b>Shop</b><p><a href="/Pages/homepage.html">All parts</a><br><a href="/Pages/homepage.html#bundles">Bundle deals</a><br><a href="/Pages/cart.html">My cart</a></p></div>
        <div><b>Your account</b><p><a href="/Pages/orders.html">Track my orders</a><br><a href="/Pages/account.html">Account &amp; security</a><br><a href="/Pages/settings.html">Settings</a></p></div>
        ${rows.length ? `<div><b>Visit us</b><p class="sy-fvisit">${rows.join('<br>')}</p></div>` : ''}</div>
        <div class="sy-fcopy">&copy; ${new Date().getFullYear()} ${e(storeName())}. All rights reserved.</div>`;
  }

  function buildShop(me, admin, who) {
    document.querySelectorAll('.top-nav').forEach(n => n.remove());
    const f = file(), on = n => f === n ? ' class="on"' : '';
    const links = `<a href="/Pages/homepage.html"${on('homepage.html')}>Shop</a><a href="/Pages/homepage.html#bundles">Bundle deals</a>` + (me ? `<a href="/Pages/orders.html"${on('orders.html')}>My orders</a>` : `<a href="/index.html#how">How it works</a>`);
    const backToAdmin = who && who.admin ? `<a class="sy-btn ghost small" href="/Pages/adminhomepage.html">${bi('arrow-left-short')} Admin panel</a>` : '';
    const nav = document.createElement('header'); nav.className = 'sy-nav';
    nav.innerHTML = `<div class="sy-nav-in"><a class="sy-logo" href="/index.html" aria-label="${e(storeName())} home"><b>${logoHtml()}</b><span id="hdr-sub">${e(tagline())}</span></a>
      <nav class="sy-links-nav" id="sy-links" aria-label="Main">${links}</nav>
      ${B.dataset.search ? searchHtml(B.dataset.search) : ''}
      <div class="sy-tools">${me ? bellHtml : ''}
        <a class="sy-ibtn" href="${me ? '/Pages/cart.html' : '/login.html?next=' + encodeURIComponent('/Pages/cart.html')}" aria-label="Cart">${bi('cart3')}</a>
        ${me ? accountMenu(me, admin) : `<div class="sy-guestbtns">${backToAdmin}<a class="sy-btn ghost small" href="/login.html">Log in</a><a class="sy-btn accent small" href="/login.html?signup=1">Sign up</a></div>`}
        <button class="sy-ibtn sy-burger" id="sy-burger" aria-label="Menu">${bi('list')}</button></div></div>`;
    B.prepend(nav);
    if (!document.querySelector('.sy-footer')) { const ft = document.createElement('footer'); ft.className = 'sy-footer'; ft.innerHTML = footerHtml(); B.appendChild(ft); }
    if (!document.querySelector('.support-btn') && me) { const s = document.createElement('div'); s.className = 'support-btn'; s.setAttribute('role', 'button'); s.setAttribute('aria-label', 'Help and customer service'); B.appendChild(s); }
  }

  function buildAdmin(me) {
    document.querySelectorAll('.top-nav').forEach(n => n.remove());
    const f = file(); let h = '';
    ADMIN_NAV.forEach(it => {
      if (it.length === 1) h += `<h4>${it[0]}</h4>`;
      else h += `<a href="/Pages/${it[1]}"${f === it[1] ? ' class="on"' : ''}>${bi(it[2])}<span>${it[0]}</span>${it[3] ? `<span class="sy-badge" data-side="${it[3]}">0</span>` : ''}</a>`;
    });
    const side = document.createElement('aside'); side.className = 'sy-side'; side.id = 'sy-side';
    side.innerHTML = `<a class="sy-logo" href="/Pages/adminhomepage.html"><b>${logoHtml()}</b><span id="hdr-sub">Admin Panel</span></a><nav aria-label="Admin">${h}</nav><div class="foot">Signed in as <b style="color:#fff">${e(me ? me.username : '')}</b></div>`;
    const scrim = document.createElement('div'); scrim.className = 'sy-scrim'; scrim.id = 'sy-scrim';
    const top = document.createElement('header'); top.className = 'sy-top' + (B.dataset.search ? ' has-search' : '');
    top.innerHTML = `<button class="sy-ibtn sy-burger" id="sy-aburger" aria-label="Open menu">${bi('list')}</button>
      ${B.dataset.search ? searchHtml(B.dataset.search) : `<span class="crumb">${e((document.title || '').replace(/^Syscle\s*-\s*/i, ''))}</span>`}
      <div class="sy-tools"><a class="sy-btn ghost small sy-viewcust" href="/Pages/admin_preview.html" title="See the store the way a customer sees it">${bi('eye')} <span>View as customer</span></a>${bellHtml}${accountMenu(me, true)}</div>`;
    B.prepend(top); B.prepend(scrim); B.prepend(side);
    const open = v => { side.classList.toggle('open', v); scrim.classList.toggle('open', v); };
    document.getElementById('sy-aburger').addEventListener('click', () => open(!side.classList.contains('open')));
    scrim.addEventListener('click', () => open(false));
    if (!document.querySelector('.support-btn')) { const sb = document.createElement('div'); sb.className = 'support-btn'; sb.setAttribute('role', 'button'); sb.setAttribute('aria-label', 'Help'); B.appendChild(sb); }
  }

  function wireMenus() {
    const burger = document.getElementById('sy-burger'), links = document.getElementById('sy-links');
    if (burger && links) burger.addEventListener('click', () => links.classList.toggle('open'));
    const pairs = [['sy-umenu', 'sy-umenu-box'], ['sy-bell', 'sy-note']];
    pairs.forEach(([b, p]) => { const bt = document.getElementById(b), pn = document.getElementById(p); if (bt && pn) bt.addEventListener('click', ev => { ev.stopPropagation(); const o = !pn.classList.contains('open'); pairs.forEach(([, q]) => { const x = document.getElementById(q); if (x) x.classList.remove('open'); }); pn.classList.toggle('open', o); }); });
    document.addEventListener('click', ev => { if (!ev.target.closest('.sy-menu,.sy-note')) pairs.forEach(([, q]) => { const x = document.getElementById(q); if (x) x.classList.remove('open'); }); });
    document.addEventListener('keydown', ev => { if (ev.key === 'Escape') pairs.forEach(([, q]) => { const x = document.getElementById(q); if (x) x.classList.remove('open'); }); });
  }

  /* ---------- admin auto sign-out ----------
     The admin portal signs itself out when nobody is working. The length (in seconds) is a store rule the admin can change (Lists and rules).
     Only a real click, key press, scroll or touch counts as activity; the background refresh of the bell does not. */
  function startIdleTimer(seconds) {
    if (!seconds || seconds < 10) return;
    let last = Date.now(), pinged = 0, done = false, warnOn = false;
    const warnAt = Math.min(30, Math.max(10, Math.round(seconds * 0.2)));
    const pingGap = Math.max(1000, Math.min(2000, seconds * 100));
    const tools = document.querySelector('.sy-top .sy-tools');
    const chip = document.createElement('div'); chip.className = 'sy-idle'; chip.setAttribute('role', 'timer'); chip.title = 'The admin portal signs out by itself when nobody is working';
    chip.innerHTML = `${bi('stopwatch')}<span id="sy-idle-t"></span>`; if (tools) tools.prepend(chip);
    const box = document.createElement('div'); box.className = 'sy-ov sy-idle-warn'; box.hidden = true; box.setAttribute('role', 'alertdialog'); box.setAttribute('aria-modal', 'true'); box.setAttribute('aria-labelledby', 'sy-idle-h');
    box.innerHTML = `<div class="sy-ovbox sy-idle-box"><h2 id="sy-idle-h">${bi('hourglass-split')} Still there?</h2><p>For safety this admin session will sign out in <b id="sy-idle-n"></b> seconds.</p>
      <div class="sy-modal-actions"><button type="button" class="sy-btn ghost" id="sy-idle-out">Sign out now</button><button type="button" class="sy-btn accent" id="sy-idle-stay">Stay signed in</button></div></div>`;
    document.body.appendChild(box);
    const fmt = s => { s = Math.max(0, Math.ceil(s)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };
    const touch = () => {
      if (done) return; const now = Date.now(); last = now;
      if (warnOn) { warnOn = false; box.hidden = true; }
      if (now - pinged > pingGap) { pinged = now; fetch('/api/auth/activity', { method: 'POST', keepalive: true, headers: { 'X-Requested-With': 'syscle', 'X-Area': 'admin' } }).catch(() => { }); }
    };
    ['pointerdown', 'keydown', 'scroll', 'touchstart', 'wheel'].forEach(ev => window.addEventListener(ev, touch, { passive: true, capture: true }));
    const leave = async () => {
      if (done) return; done = true;
      try { await fetch('/api/auth/logout', { method: 'POST', headers: { 'X-Requested-With': 'syscle', 'X-Area': 'admin' } }); } catch (x) { }
      location.href = '/login.html?reason=idle';
    };
    document.getElementById('sy-idle-stay').addEventListener('click', touch);
    document.getElementById('sy-idle-out').addEventListener('click', leave);
    const t = document.getElementById('sy-idle-t'), n = document.getElementById('sy-idle-n');
    setInterval(() => {
      if (done) return;
      const left = seconds - (Date.now() - last) / 1000 - pingGap / 1000;   // the server counts from the last ping, which can be a moment older
      t.textContent = fmt(left); chip.classList.toggle('low', left <= warnAt);
      if (left <= warnAt && left > 0) { if (!warnOn) { warnOn = true; box.hidden = false; document.getElementById('sy-idle-stay').focus(); } n.textContent = Math.ceil(left); }
      if (left <= 0) leave();
    }, 500);
    t.textContent = fmt(seconds);
  }

  /* ---------- promo pop-up: tells visitors about live codes and bundles. It appears at random moments while they browse
     (never on the cart or payment page, never on top of another dialog, at most 4 times per visit, and it can be silenced for the day) ---------- */
  async function promoPopup() {
    const p = location.pathname.toLowerCase();
    if (/\/(cart|pay_sandbox)\.html$/.test(p)) return;
    const preview = /[?&]preview=1/.test(location.search);   // the admin "view as customer" frame shows it quickly
    if (!preview && (lsGet('sy.popup.hide') || '') === new Date().toDateString()) return;
    const d = await get('/api/promotions'); if (!d || !d.popup) return;
    const codes = d.codes || [], bundles = d.bundles || []; if (!codes.length && !bundles.length) return;
    const MAX = 4, rnd = (a, b) => (a + Math.random() * (b - a)) * 1000;
    const schedule = ms => setTimeout(tick, ms);
    function tick() {
      if ((lsGet('sy.popup.hide') || '') === new Date().toDateString() && !preview) return;
      const n = +ssGet('sy.popup.n') || 0; if (n >= MAX) return;
      if (document.hidden || document.querySelector('.sy-ov')) return schedule(15000);   // wait until the visitor is looking and no dialog is open
      ssSet('sy.popup.n', String(n + 1));
      // a random mix: sometimes the codes, sometimes the bundle deals, sometimes both
      const roll = Math.random(); let c = codes, b = bundles;
      if (codes.length && bundles.length && n > 0) { if (roll < 0.4) b = []; else if (roll < 0.7) c = []; }
      showPromo(c, b, () => schedule(rnd(60, 180)));
    }
    schedule(preview ? 800 : (ssGet('sy.popup.n') ? rnd(40, 120) : rnd(6, 25)));
  }
  function showPromo(codes, bundles, onClose) {
    const claimed = lsGet('sy.claim') || {};
    const codeCard = c => `<div class="sy-pcode"><div class="pc-pct">${c.percent}%<small>OFF</small></div><div class="pc-body"><b>${e(c.code)}</b>
        <span>${c.season ? e(c.season) + ' &middot; ' : ''}${c.endsAt ? 'until ' + e(c.endsAt) : 'no end date'}${c.left != null ? ' &middot; ' + c.left + ' left' : ''}</span></div>
        <button type="button" class="sy-btn accent small" data-claim="${e(c.code)}" data-pct="${c.percent}">${claimed.code === c.code ? 'Saved' : 'Claim'}</button></div>`;
    const bundleCard = b => `<a class="sy-pbundle" href="/Pages/homepage.html#bundles">${b.image ? `<img src="${e(b.image)}" alt="" loading="lazy">` : (b.items && b.items[0] && b.items[0].image ? `<img src="${e(b.items[0].image)}" alt="" loading="lazy">` : `<span class="ph">${bi('box-seam')}</span>`)}
        <span><b>${e(b.name)}</b><small>Save ${b.discountPercent}% &middot; now ${fmtP(b.price)}</small></span></a>`;
    const ov = document.createElement('div'); ov.className = 'sy-ov sy-promo'; ov.setAttribute('role', 'dialog'); ov.setAttribute('aria-modal', 'true'); ov.setAttribute('aria-labelledby', 'sy-promo-h');
    ov.innerHTML = `<div class="sy-ovbox sy-promobox"><button class="sy-ovx" type="button" aria-label="Close">${bi('x-lg')}</button>
      <div class="sy-promo-head">${bi('gift')}<div><h2 id="sy-promo-h">${codes.length ? 'You have a discount waiting' : 'Bundle deals for you'}</h2><p>${codes.length ? 'Claim a code now. It is filled in for you at checkout.' : 'Buy together and save.'}</p></div></div>
      ${codes.length ? `<div class="sy-pcodes">${codes.map(codeCard).join('')}</div>` : ''}
      ${bundles.length ? `<h3 class="sy-promo-sub">${bi('box-seam')} Bundle deals</h3><div class="sy-pbundles">${bundles.map(bundleCard).join('')}</div>` : ''}
      <label class="sy-promo-skip"><input type="checkbox" id="sy-promo-skip"> Do not show this again today</label>
      <div class="sy-modal-actions"><a class="sy-btn ghost" href="/Pages/homepage.html#bundles">See all deals</a><button type="button" class="sy-btn accent" id="sy-promo-go">Start shopping</button></div></div>`;
    let closed = false; const close = () => { if (closed) return; closed = true; if (document.getElementById('sy-promo-skip').checked) lsSet('sy.popup.hide', new Date().toDateString()); ov.remove(); if (onClose) onClose(); };
    ov.addEventListener('click', ev => { if (ev.target === ov) close(); });
    ov.querySelector('.sy-ovx').addEventListener('click', close);
    ov.querySelector('#sy-promo-go').addEventListener('click', () => { close(); const s = document.getElementById('shop') || document.querySelector('.sy-products'); if (s) s.scrollIntoView({ behavior: 'smooth' }); });
    ov.addEventListener('keydown', ev => { if (ev.key === 'Escape') close(); });
    ov.querySelectorAll('[data-claim]').forEach(btn => btn.addEventListener('click', () => {
      lsSet('sy.claim', { code: btn.dataset.claim, percent: +btn.dataset.pct, at: Date.now() });
      ov.querySelectorAll('[data-claim]').forEach(x => x.textContent = x === btn ? 'Saved' : 'Claim');
      if (window.toast) window.toast('Code ' + btn.dataset.claim + ' saved. It will be applied at checkout.', false, window.SY_ME ? 'View cart' : 'Log in', window.SY_ME ? '/Pages/cart.html' : '/login.html?next=' + encodeURIComponent('/Pages/cart.html'));
    }));
    document.body.appendChild(ov); const first = ov.querySelector('[data-claim],#sy-promo-go'); if (first) first.focus();
  }

  /* ---------- notification bell ---------- */
  const STEP = {
    Pending: ['warn', 'hourglass-split', 'Order received', 'We got your order and will start soon.'],
    Preparing: ['info', 'tools', 'Being prepared', 'The store checked your items and is preparing them.'],
    Ready: ['ok', 'bag-check', 'Ready for pick-up', 'Please bring your order number or its QR code.'],
    'Out for Delivery': ['info', 'truck', 'Out for delivery', 'Your order is on its way.'],
    Completed: ['ok', 'check-circle', 'Completed', 'Thank you for shopping with us.'],
    Cancelled: ['bad', 'x-circle', 'Cancelled', 'The items went back to stock.']
  };
  function paintBell(items, unread) {
    const n = document.getElementById('sy-bell-n'), l = document.getElementById('sy-nlist'); if (!n || !l) return;
    n.textContent = unread > 99 ? '99+' : unread; n.classList.toggle('show', unread > 0);
    l.innerHTML = items.length ? items.map(x => `<a class="sy-nitem${x.fresh ? ' new' : ''}" href="${x.href}"><div class="dot ${x.tone}">${bi(x.icon)}</div><div><b>${e(x.title)}</b><span>${e(x.text)}</span></div></a>`).join('') : '<div class="sy-nempty">You are all caught up.</div>';
  }
  function startBell(me, admin) {
    const key = 'syscle.seen.' + me.userId;
    let first = true, lastSeen = lsGet(key) || {};
    async function tickCustomer() {
      const rows = await get('/api/orders'); if (!rows) return;
      const seen = lsGet(key) || {}, items = []; let unread = 0, cur = {};
      rows.forEach(o => { cur[o.id] = o.status + '|' + (o.refundStatus || 'None'); });
      rows.slice(0, 8).forEach(o => {
        const sig = o.status + '|' + (o.refundStatus || 'None');
        let s = STEP[o.status] || STEP.Pending;
        if (o.refundStatus === 'Requested') s = ['warn', 'arrow-counterclockwise', 'Refund requested', 'The store is checking your refund request.'];
        else if (o.refundStatus === 'Refunded') s = ['ok', 'cash-coin', 'Refunded', 'Your money is on its way back.'];
        else if (o.refundStatus === 'Rejected') s = ['bad', 'slash-circle', 'Refund declined', 'Message the store if you have questions.'];
        const fresh = seen[o.id] !== sig; if (fresh) unread++;
        if (!first && lastSeen[o.id] && lastSeen[o.id] !== sig && window.toast) window.toast(`Order ${o.code}: ${s[2]}`, o.status === 'Cancelled');
        items.push({ tone: s[0], icon: s[1], title: `${o.code} · ${s[2]}`, text: `${s[3]} (${fmtP(o.total)})`, href: '/Pages/orders.html', fresh });
      });
      lastSeen = cur; first = false; window.__syOrders = rows; paintBell(items, unread);
      document.getElementById('sy-readall').onclick = () => { lsSet(key, cur); paintBell(items.map(x => ({ ...x, fresh: false })), 0); };
    }
    async function tickAdmin() {
      const [st, msg] = await Promise.all([get('/api/admin/stats'), get('/api/admin/messages/unread')]); if (!st) return;
      const o = st.orders, p = st.prods, m = msg ? msg.count : 0, items = [];
      if (o.pendingOrders) items.push({ tone: 'warn', icon: 'bell', title: `${o.pendingOrders} new order${o.pendingOrders > 1 ? 's' : ''} waiting`, text: 'Check the items, then start preparing.', href: '/Pages/admin_transaction_history.html', fresh: true });
      if (o.refundRequests) items.push({ tone: 'bad', icon: 'arrow-counterclockwise', title: `${o.refundRequests} refund request${o.refundRequests > 1 ? 's' : ''}`, text: 'Approve or decline them.', href: '/Pages/admin_transaction_history.html', fresh: true });
      const mid = (o.openOrders || 0) - (o.pendingOrders || 0); if (mid > 0) items.push({ tone: 'info', icon: 'tools', title: `${mid} order${mid > 1 ? 's' : ''} in progress`, text: 'Preparing or ready for pick-up.', href: '/Pages/admin_transaction_history.html' });
      if (p.outOfStock) items.push({ tone: 'bad', icon: 'exclamation-triangle', title: `${p.outOfStock} item${p.outOfStock > 1 ? 's' : ''} out of stock`, text: 'Restock to keep selling.', href: '/Pages/admin_inventory.html', fresh: true });
      if (p.lowStock) items.push({ tone: 'warn', icon: 'box-seam', title: `${p.lowStock} item${p.lowStock > 1 ? 's' : ''} running low`, text: 'Below the low-stock level.', href: '/Pages/admin_inventory.html' });
      if (m) items.push({ tone: 'info', icon: 'chat-dots', title: `${m} unread message${m > 1 ? 's' : ''}`, text: 'Customers are waiting for a reply.', href: '/Pages/admin_messages.html', fresh: true });
      paintBell(items, (o.pendingOrders || 0) + (o.refundRequests || 0) + (p.outOfStock || 0) + m);
      document.querySelectorAll('[data-side]').forEach(b => { const v = b.dataset.side === 'orders' ? (o.pendingOrders || 0) + (o.refundRequests || 0) : b.dataset.side === 'msg' ? m : (p.outOfStock || 0) + (p.lowStock || 0); b.textContent = v; b.classList.toggle('show', v > 0); });
      const ra = document.getElementById('sy-readall'); if (ra) ra.style.display = 'none';
    }
    const tick = admin ? tickAdmin : tickCustomer; tick(); setInterval(() => { if (!document.hidden) tick(); }, admin ? 30000 : 20000);
    window.syRefreshBell = tick;
  }
})();

/* BACK BUTTON: every inner page gets the same "Back" button, so nobody is stuck. Home pages and the login pages do not need one. */
(function () {
  const p = location.pathname.toLowerCase();
  if (p === '/' || /\/(index|login|reset|error|adminhomepage)\.html$/.test(p)) return;
  const isShopHome = /\/homepage\.html$/.test(p);
  const run = () => {
    const admin = document.body.dataset.area === 'admin', home = admin ? '/Pages/adminhomepage.html' : '/Pages/homepage.html';
    let ref = ''; try { const r = document.referrer && new URL(document.referrer); ref = r && r.origin === location.origin && r.pathname.toLowerCase() !== p && !/login|reset/i.test(r.pathname) ? r.pathname : ''; } catch (e) { }
    /* the shop homepage is a starting point, so it only gets a Back button when you arrived from another page (for example "View everything" on the landing page) */
    if (isShopHome && !ref) return;
    if (window.self !== window.top) return;   /* inside the admin "view as customer" frame there is no Back button */
    const old = [...document.querySelectorAll('.back-btn')]; let btn = old.shift(); old.forEach(x => x.remove());
    if (!btn) {
      const host = [...document.querySelectorAll('.sy-page, .sy-section, .sy-shelf')].find(x => x.offsetWidth > 0 && !x.hidden) || document.querySelector('main'); if (!host) return;
      btn = document.createElement('a'); host.prepend(btn);
    }
    btn.className = 'back-btn'; btn.removeAttribute('onclick'); btn.setAttribute('href', home); btn.innerHTML = '<i class="bi bi-arrow-left" aria-hidden="true"></i> Back'; btn.setAttribute('role', 'button');
    btn.addEventListener('click', ev => {
      ev.preventDefault(); ev.stopImmediatePropagation();
      let same = false; try { same = document.referrer && new URL(document.referrer).origin === location.origin && !/login|reset/i.test(document.referrer); } catch (e) { }
      if (same && history.length > 1) history.back(); else location.href = isShopHome ? '/index.html' : home;
    }, true);
  };
  const go = () => { (window.SY_READY || Promise.resolve()).then(() => setTimeout(run, 0), () => setTimeout(run, 0)); };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', go); else go();
})();
