(async function () {
    const $ = id => document.getElementById(id);
    let me;
    try { me = await api('/api/auth/me'); } catch (e) { return; }
    const idEl = $('acct-id');
    if (idEl) idEl.textContent = (me.role === 'SuperAdmin' ? 'SuperAdmin ID: SA-' : me.role === 'Admin' ? 'Admin ID: A-' : 'Customer ID: C-') + String(me.userId).padStart(6, '0');
    const back = $('back-link');
    if (me.role === 'Admin' || me.role === 'SuperAdmin') {
        back.href = 'adminhomepage.html'; back.textContent = '\u2190 Back to Dashboard';
        document.querySelectorAll('.cart-icon').forEach(i => { const a = i.closest('a'); if (a) a.style.display = 'none'; });
        const h = $('history-section'); if (h) h.style.display = 'none';
    } else { back.href = 'homepage.html'; back.textContent = '\u2190 Back to Storefront'; }

    const prefix = { 'disp-username': 'Username: ', 'disp-email': 'Email: ', 'disp-contact': 'Contact Number: ' };
    const raw = { 'disp-username': me.username, 'disp-email': me.email, 'disp-contact': me.phone };
    const paint = () => Object.keys(prefix).forEach(id => { $(id).value = prefix[id] + (raw[id] || '(not set)'); });
    paint();

    async function saveProfile() {
        try { await api('/api/me/profile', 'PUT', { email: raw['disp-email'], phone: raw['disp-contact'] }); toast('Saved.'); }
        catch (e) { toast(e.message, true); const f = await api('/api/auth/me'); raw['disp-email'] = f.email; raw['disp-contact'] = f.phone; }
        paint();
    }

    document.querySelectorAll('.input-group').forEach(group => {
        const input = group.querySelector('.info-input'), pencil = group.querySelector('.edit-icon');
        if (!input || !pencil) return;
        pencil.style.cursor = 'pointer';
        pencil.addEventListener('click', async () => {
            if (input.id === 'disp-username') return toast('Usernames can\u2019t be changed.', true);
            if (input.id === 'disp-password') {
                const cur = prompt('Enter your current password:'); if (cur === null) return;
                const nxt = prompt('Enter a new password (8 to 14 characters):'); if (nxt === null) return;
                try { await api('/api/me/password', 'POST', { current: cur, next: nxt }); toast('Password changed.'); } catch (e) { toast(e.message, true); }
                return;
            }
            input.readOnly = false; input.value = raw[input.id] || ''; input.focus();
            input.addEventListener('blur', () => { input.readOnly = true; raw[input.id] = input.value.trim(); saveProfile(); }, { once: true });
        });
    });

    async function loadOrders() {
        try {
            const rows = await api('/api/orders');
            $('history-body').innerHTML = rows.length ? rows.map(o => `<tr><td>${esc(o.code)}</td><td>${fmtDateTime(o.date)}</td><td>${esc(o.items)}</td><td>${peso(o.total)}</td><td>${fmtDate(o.pickup)}</td>
        <td>${statusChip(o.status)}</td>
        <td>${o.status === 'Pending' ? `<button class="sy-x" data-cancel="${o.id}">Cancel</button>` : '-'}</td></tr>`).join('')
                : '<tr><td colspan="7" class="sy-empty">No orders yet.</td></tr>';
        } catch (e) { }
    }

    if (me.role !== 'Admin' && me.role !== 'SuperAdmin') {
        loadOrders();
        if ($('history-body')) $('history-body').addEventListener('click', async e => {
            const id = e.target.dataset.cancel; if (!id || !confirm('Cancel this order? The items go back to stock.')) return;
            try { await api('/api/orders/' + id + '/cancel', 'POST'); toast('Order cancelled.'); await loadOrders(); } catch (err) { toast(err.message, true); }
        });
    }

    // ---- Two-factor sign-in (Google Authenticator / Authy) ----
    // NOTE: element ids here must not start with a digit - "#2fa-code" is an invalid CSS selector and made the Verify button do nothing.
    const btn2fa = $('btn-setup-2fa');
    if (btn2fa) {
        const paint = () => { btn2fa.textContent = me.twoFactorEnabled ? 'Turn off 2FA' : 'Setup 2FA'; btn2fa.classList.toggle('ghost', !!me.twoFactorEnabled); const st = $('tfa-status'); if (st) st.textContent = me.twoFactorEnabled ? '2FA is ON. You will be asked for a 6-digit code every time you log in.' : 'Protect your account with Google Authenticator or Authy.'; };
        paint();
        const codeOk = c => /^\d{6}$/.test(c);

        function openSetup() {
            api('/api/me/2fa/setup').then(res => {
                const m = showModal(`<div class="sy-done"><h2>Setup 2FA</h2>
          <p>1. Open Google Authenticator or Authy and scan this QR code:</p>
          <img src="data:image/png;base64,${res.qrCodeImage}" alt="2FA QR code" style="width:200px; height:200px;">
          <p>Can't scan? Type this key into the app: <b style="word-break:break-all">${esc(res.secret)}</b></p>
          <p>2. Type the 6-digit code the app shows now:</p>
          <input class="sy-input" id="tfa-code" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="6-digit code" style="margin-top:6px;">
          <div class="sy-pm-actions"><button class="sy-btn accent" id="btn-verify-tfa" type="button">Verify &amp; Enable</button></div></div>`);
                const input = m.el.querySelector('#tfa-code'), go = m.el.querySelector('#btn-verify-tfa');
                input.focus();
                const verify = async () => {
                    const code = input.value.replace(/\s/g, '');
                    if (!codeOk(code)) return toast('Enter the 6-digit code from your app.', true);
                    go.disabled = true;
                    try {
                        await api('/api/me/2fa/enable', 'POST', { code });
                        me.twoFactorEnabled = true; paint(); m.close();
                        showModal('<div class="sy-done"><h2>2FA is on</h2><p>From now on, logging in asks for your password and then a 6-digit code from your app.</p><div class="sy-pm-actions"><button class="sy-btn accent" type="button" onclick="this.closest(\'.sy-ov\').remove()">Done</button></div></div>');
                    } catch (e) { toast(e.message + ' (Check that your phone clock is set to automatic time.)', true); input.value = ''; input.focus(); }
                    go.disabled = false;
                };
                go.addEventListener('click', verify);
                input.addEventListener('keydown', e => { if (e.key === 'Enter') verify(); });
            }).catch(e => toast(e.message, true));
        }

        function openDisable() {
            const m = showModal(`<div class="sy-done"><h2>Turn off 2FA</h2>
          <p>Enter your password and a current 6-digit code to confirm.</p>
          <input class="sy-input" id="tfa-pass" type="password" placeholder="Your password" autocomplete="current-password">
          <input class="sy-input" id="tfa-off-code" inputmode="numeric" maxlength="6" placeholder="6-digit code" style="margin-top:8px;">
          <div class="sy-pm-actions"><button class="sy-btn accent" id="btn-disable-tfa" type="button">Turn off 2FA</button></div></div>`);
            m.el.querySelector('#btn-disable-tfa').addEventListener('click', async () => {
                const password = m.el.querySelector('#tfa-pass').value, code = m.el.querySelector('#tfa-off-code').value.replace(/\s/g, '');
                if (!password || !codeOk(code)) return toast('Enter your password and the 6-digit code.', true);
                try { await api('/api/me/2fa/disable', 'POST', { password, code }); me.twoFactorEnabled = false; paint(); m.close(); toast('2FA turned off.'); }
                catch (e) { toast(e.message, true); }
            });
        }
        btn2fa.addEventListener('click', () => me.twoFactorEnabled ? openDisable() : openSetup());
    }
})();
function logoutUserClick() { logoutUser(); }