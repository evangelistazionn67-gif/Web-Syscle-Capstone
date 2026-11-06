// HELP WINDOW: opens from the round support button on every page.
// Customers get quick answers, store contact details, quick links and a chat with the store.
// Admins get a shortcut to the messages inbox and a short how-to guide.
(window.SY_READY || Promise.resolve()).then(function () {
    const btn = document.querySelector('.support-btn');
    if (!btn || typeof api !== 'function') return;
    const css = document.createElement('link'); css.rel = 'stylesheet'; css.href = '../Styles/support.css'; document.head.appendChild(css);

    let me = null, store = {}, view = { name: 'home' }, timer = null, poll = null;
    const isAdmin = () => !!me && (me.role === 'Admin' || me.role === 'SuperAdmin');
    const IC = {
        chat: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a8 8 0 0 1-11.6 7.1L3 21l1.9-5.4A8 8 0 1 1 21 12z"/></svg>',
        card: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="5" width="20" height="14" rx="2"/><path d="M2 10h20"/></svg>',
        box: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 8l-9-5-9 5v8l9 5 9-5z"/><path d="M3 8l9 5 9-5M12 13v8"/></svg>',
        user: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/></svg>',
        gear: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>'
    };

    const hours = () => store.StoreHours || 'Ask the store';
    const FAQ_CUSTOMER = [
        { q: 'How do I place an order?', a: () => '<p>1. Browse the shop and tap <b>Buy</b> on a part (pick the size or colour if it has one).</p><p>2. Open your <b>cart</b>, choose a pick-up date and a payment method, then tap <b>Place Order</b>.</p><p>3. Come to the store on your pick-up date. Your order number is shown after checkout and in <b>My account</b>.</p>' },
        { q: 'How do I pay with GCash?', a: () => '<p>Choose <b>GCash</b> in your cart. The store’s GCash number' + ' (and QR code, if the store turned it on) appears there.</p><p>Send the total in your GCash app, then type the <b>reference number</b> from your GCash receipt before you tap Place Order. The store checks it and completes your order.</p><p>You can also choose <b>Cash</b> and pay when you pick up.</p>' },
        { q: 'How do I set up 2FA (authenticator app)?', a: () => '<p>Go to <b>My account > Security (2FA) > Setup 2FA</b>.</p><p>1. Install Google Authenticator or Authy on your phone.<br>2. Scan the QR code (or type the key).<br>3. Type the 6-digit code the app shows and tap <b>Verify &amp; Enable</b>.</p><p>After that, logging in asks for your password and then a code from the app. Keep your phone’s clock on automatic time.</p>' },
        { q: 'I forgot my password', a: () => '<p>On the login page tap <b>Forgot Password</b>.</p><p>• Enter your email to get a reset link, or<br>• tap <b>Reset with my mobile number</b> to get a 6-digit code by SMS.</p><p>For the SMS option, save your mobile number first in <b>My account > Contact Number</b>.</p>' },
        { q: 'How do promo codes work?', a: () => '<p>Type the code in the <b>Promo Code</b> box in your cart and tap <b>Apply</b>. The discount is taken off your total. Ask the store if you need a code.</p>' },
        { q: 'What is Buy 1 Get 1 / Special offers?', a: () => '<p>The home page shows bundle deals and <b>Buy 1 Get 1</b> offers at a lower price. Tap one to add all its parts to your cart at the offer price while it is available.</p>' },
        { q: 'Can I cancel my order?', a: () => '<p>Yes, while it is still <b>Pending</b>. Go to <b>My account > My Orders</b> and tap <b>Cancel</b>. Completed orders can’t be cancelled online, so send the store a message.</p>' },
        { q: 'Where and when do I pick up?', a: () => '<p>Orders are picked up at the store on the date you chose.</p>' + sellerBlock() + (store.PickupNote ? '<p>' + esc(store.PickupNote) + '</p>' : '') }
    ];
    const FAQ_ADMIN = [
        { q: 'How do I complete or cancel an order?', a: () => '<p>Open <b>Transactions History</b>. Pending orders have <b>Complete</b> and <b>Cancel</b> buttons. Cancelling puts the items back in stock.</p>' },
        { q: 'How do I check a GCash payment?', a: () => '<p>GCash orders show a <b>Ref</b> number under the payment. Open your GCash app, find the payment with that reference, and only then press <b>Complete</b>.</p>' },
        { q: 'How do I answer customer messages?', a: () => '<p>Open <b>Customer Messages</b> from the dashboard, pick a customer, type your reply and press <b>Send</b>. A red number on the support button shows unread messages.</p>' },
        { q: 'How do I make a Buy 1 Get 1 offer?', a: () => '<p>Go to <b>Bundle Offers</b>, choose a part and press <b>Make BOGO</b>. Change the discount in the list if you want another percentage.</p>' },
        { q: 'How do I set my store details?', a: () => '<p>Open <b>Settings</b> (gear icon) and fill in store name, address, phone and opening hours. Customers see them in this help window.</p>' }
    ];
    function sellerBlock() {
        const rows = [['Store', store.StoreName], ['Address', store.StoreAddress], ['Phone', store.StorePhone], ['Hours', store.StoreHours]].filter(r => r[1]);
        return rows.length ? '<div class="sx-info">' + rows.map(r => '<b>' + r[0] + ':</b> ' + esc(r[1])).join('<br>') + '</div>' : '<div class="sx-info">The store has not added its contact details yet. Send a message and we will reply here.</div>';
    }

    const panel = document.createElement('div'); panel.className = 'sx-panel'; panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-label', 'Customer service');
    document.body.appendChild(panel);
    const dot = document.createElement('span'); dot.className = 'sx-dot'; btn.style.position = 'fixed'; btn.appendChild(dot);
    btn.setAttribute('role', 'button'); btn.setAttribute('tabindex', '0'); btn.setAttribute('aria-label', 'Customer service');

    function setView(v) { view = v; clearInterval(timer); timer = null; render(); }
    function head(back, title) {
        const name = me ? esc(me.username) : 'there';
        return '<div class="sx-head">' + (back ? '<button class="sx-back" data-act="home" aria-label="Back">‹</button>' : '') + '<button class="sx-x" data-act="close" aria-label="Close">×</button>' +
            (title ? '<div class="sx-how small">' + esc(title) + '</div>' : '<div class="sx-hi">Hi ' + name + ',</div><div class="sx-how">How can we help?</div>') + '</div>';
    }
    function render() {
        const faq = isAdmin() ? FAQ_ADMIN : FAQ_CUSTOMER;
        if (view.name === 'home') {
            const tiles = isAdmin()
                ? `<a class="sx-tile" href="admin_messages.html">${IC.chat}Customer messages</a><a class="sx-tile" href="admin_payment_setup.html">${IC.card}Payment setup</a><a class="sx-tile" href="admin_transaction_history.html">${IC.box}Transactions</a><a class="sx-tile" href="settings.html">${IC.gear}Settings</a>`
                : `<button class="sx-tile" data-act="seller">${IC.chat}Contact seller</button><a class="sx-tile" href="settings.html#payment">${IC.card}Edit payment</a><a class="sx-tile" href="account.html#history-section">${IC.box}Track my order</a><a class="sx-tile" href="account.html">${IC.user}Edit account</a>`;
            panel.innerHTML = head(false) + '<div class="sx-body">' + faq.map((f, i) => `<button class="sx-q" data-faq="${i}">${esc(f.q)}<span>›</span></button>`).join('') +
                '<div class="sx-tiles">' + tiles + '</div>' + (isAdmin() ? '' : '<button class="sx-send" data-act="chat">Send message</button>') + '</div>';
        } else if (view.name === 'faq') {
            const f = faq[view.i]; panel.innerHTML = head(true, 'Help') + '<div class="sx-body"><div class="sx-ans"><h3>' + esc(f.q) + '</h3>' + f.a() + '</div>' + (isAdmin() ? '' : '<p class="sx-note">Still need help?</p><button class="sx-send" data-act="chat">Send message</button>') + '</div>';
        } else if (view.name === 'seller') {
            panel.innerHTML = head(true, 'Contact seller') + '<div class="sx-body"><div class="sx-ans"><p>Reach the store directly, or leave a message and we will reply in this window.</p>' + sellerBlock() + '</div><button class="sx-send" data-act="chat">Send message</button></div>';
        } else if (view.name === 'chat') {
            panel.innerHTML = head(true, 'Message the store') + '<div class="sx-body"><div class="sx-msgs" id="sx-msgs"><p class="sx-note">Loading...</p></div></div>' +
                '<div class="sx-foot"><textarea id="sx-text" maxlength="500" placeholder="Type your message..." rows="1"></textarea><button id="sx-go" type="button">Send</button></div>';
            loadChat(true); timer = setInterval(() => loadChat(false), 6000);
            const t = panel.querySelector('#sx-text'); t.focus();
            t.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendChat(); } });
        }
    }
    async function loadChat(scroll) {
        const box = panel.querySelector('#sx-msgs'); if (!box) return;
        try {
            const rows = (await api('/api/messages')).reverse(), atEnd = scroll || box.parentElement.scrollHeight - box.parentElement.scrollTop - box.parentElement.clientHeight < 60;
            box.innerHTML = '<div class="sx-b them">Hi ' + esc(me.username) + '! Leave a message and the store will reply here. Replies usually come during store hours' + (store.StoreHours ? ' (' + esc(store.StoreHours) + ')' : '') + '.</div>' +
                rows.map(m => `<div class="sx-b ${m.sender === 'Customer' ? 'me' : 'them'}">${esc(m.body)}<small>${m.sender === 'Customer' ? 'You' : 'Store'} · ${esc(fmtDateTime(m.date))}</small></div>`).join('');
            if (atEnd) box.parentElement.scrollTop = box.parentElement.scrollHeight;
            checkUnread();
        } catch (e) { }
    }
    async function sendChat() {
        const t = panel.querySelector('#sx-text'), go = panel.querySelector('#sx-go'); if (!t) return;
        const body = t.value.trim(); if (!body) return;
        go.disabled = true;
        try { await api('/api/messages', 'POST', { body }); t.value = ''; await loadChat(true); } catch (e) { toast(e.message, true); }
        go.disabled = false; t.focus();
    }
    async function checkUnread() {
        if (!me) return;
        try {
            const r = await api(isAdmin() ? '/api/admin/messages/unread' : '/api/messages/unread'), n = r.count || 0;
            dot.textContent = n > 99 ? '99+' : n; dot.style.display = n && !(view.name === 'chat' && panel.classList.contains('open')) ? 'block' : 'none';
            const tile = document.getElementById('tile-msg-badge'); if (tile) { tile.textContent = n; tile.style.display = n ? 'inline-block' : 'none'; }
        } catch (e) { }
    }

    function open() { panel.classList.add('open'); render(); }
    function close() { panel.classList.remove('open'); clearInterval(timer); timer = null; checkUnread(); }
    btn.addEventListener('click', () => panel.classList.contains('open') ? close() : open());
    btn.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); btn.click(); } });
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && panel.classList.contains('open')) close(); });
    panel.addEventListener('click', e => {
        const f = e.target.closest('[data-faq]'); if (f) return setView({ name: 'faq', i: +f.dataset.faq });
        const a = e.target.closest('[data-act]'); if (!a) { if (e.target.id === 'sx-go') sendChat(); return; }
        const act = a.dataset.act;
        if (act === 'close') close(); else if (act === 'home') setView({ name: 'home' }); else if (act === 'seller') setView({ name: 'seller' }); else if (act === 'chat') setView({ name: 'chat' });
    });
    window.openHelp = open;

    (async function init() {
        try { me = await api('/api/auth/me'); } catch (e) { return; }
        try { store = await api('/api/store-info'); } catch (e) { }
        checkUnread(); poll = setInterval(checkUnread, 30000);
        if (location.hash === '#help') open();
    })();
});
