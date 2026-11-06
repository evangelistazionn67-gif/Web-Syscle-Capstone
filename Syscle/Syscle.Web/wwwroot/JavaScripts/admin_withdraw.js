(async function () {
    const $ = id => document.getElementById(id);
    if (!$('wd-balance')) return;
    let info = null, picked = null;
    const code6 = id => { const v = $(id).value.replace(/\s/g, ''); return /^\d{6}$/.test(v) ? v : null; };

    function paint() {
        const i = info, tf = i.twoFactor;
        $('wd-2fa').hidden = tf;
        $('wd-balance').innerHTML = i.walletError
            ? `<h3>Wallet</h3><p class="sy-red">${esc(i.walletError)}</p><button class="sy-btn small ghost" id="wd-refresh" type="button">Try again</button>`
            : `<div class="sy-wallet"><div><small>Available to withdraw</small><div class="sy-bal">${peso(i.available)}</div></div><div><small>Still clearing</small><div class="sy-bal small">${peso(i.pending)}</div></div><div><small>PayMongo wallet</small><div>${esc(i.walletName || '')}<br><span class="sy-muted">${esc(i.walletNumber || '')}</span></div></div><button class="sy-btn small ghost" id="wd-refresh" type="button">Refresh</button></div>`;
        $('wd-refresh').addEventListener('click', load);
        $('wd-limits').textContent = `From ${peso(i.min)} up to ${peso(i.max)} each time. PayMongo charges about ${peso(i.fee)} per transfer, taken from the wallet on top of the amount.`;
        if (!picked || !i.accounts.some(a => a.id === picked)) picked = i.accounts.length ? i.accounts[0].id : null;
        $('wd-accounts').innerHTML = i.accounts.length ? i.accounts.map(a => `<label class="sy-payopt${picked === a.id ? ' on' : ''}"><input type="radio" name="wacct" value="${a.id}"${picked === a.id ? ' checked' : ''}><i class="bi bi-phone" aria-hidden="true"></i><span><b>${esc(a.name)}</b><small>${esc(a.number)}</small></span><button class="sy-x" type="button" data-del="${a.id}">Remove</button></label>`).join('') : '<div class="sy-empty">No GCash account saved yet. Add one below.</div>';
        const off = !tf || !!i.walletError || !i.accounts.length;
        ['wd-go', 'wd-all', 'wd-amount', 'wd-code', 'wa-add'].forEach(x => { $(x).disabled = x === 'wa-add' ? !tf : off; });
    }
    async function history() {
        try {
            const h = await api('/api/admin/wallet/withdrawals');
            $('wd-history').innerHTML = h.length ? h.map(x => `<tr><td>${esc(fmtDateTime(x.at))}</td><td><b>${esc(x.reference)}</b></td><td>${esc(x.name)}<br><small class="sy-muted">${esc(x.number)}</small></td><td>${peso(x.amount)}</td><td><span class="sy-chip ${x.status === 'Succeeded' ? 'ok' : x.status === 'Failed' ? 'bad' : 'warn'}">${esc(x.status)}</span>${x.error ? `<br><small class="sy-muted">${esc(x.error)}</small>` : ''}</td></tr>`).join('') : '<tr><td colspan="5" class="sy-empty">No withdrawals yet.</td></tr>';
        } catch (e) { $('wd-history').innerHTML = '<tr><td colspan="5" class="sy-empty">Could not load the history.</td></tr>'; }
    }
    async function load() { try { info = await api('/api/admin/wallet'); paint(); } catch (e) { $('wd-balance').innerHTML = '<p class="sy-red">' + esc(e.message) + '</p>'; } await history(); }

    $('wd-accounts').addEventListener('change', e => { if (e.target.name === 'wacct') { picked = +e.target.value; paint(); } });
    $('wd-accounts').addEventListener('click', async e => {
        const b = e.target.closest('[data-del]'); if (!b) return; e.preventDefault();
        if (!confirm('Remove this GCash account?')) return;
        try { await api('/api/admin/wallet/accounts/' + b.dataset.del, 'DELETE'); await load(); toast('Account removed.'); } catch (err) { toast(err.message, true); }
    });
    $('wa-add').addEventListener('click', async () => {
        const name = $('wa-name').value.trim(), number = $('wa-number').value.trim(), code = code6('wa-code');
        if (!name || !number) return toast('Enter the name and the GCash number.', true);
        if (!code) return toast('Enter the 6-digit code from your authenticator app.', true);
        const b = $('wa-add'); b.disabled = true;
        try { await api('/api/admin/wallet/accounts', 'POST', { name, number, code }); $('wa-name').value = $('wa-number').value = $('wa-code').value = ''; await load(); toast('GCash account added.'); } catch (err) { toast(err.message, true); }
        b.disabled = false;
    });
    $('wd-all').addEventListener('click', () => { if (info && info.available != null) $('wd-amount').value = Math.max(0, Math.floor((Math.min(info.available - info.fee, info.max)) * 100) / 100) || ''; });
    $('wd-go').addEventListener('click', async () => {
        const a = info.accounts.find(x => x.id === picked), amount = +$('wd-amount').value, code = code6('wd-code');
        if (!a) return toast('Choose a GCash account.', true);
        if (!(amount >= info.min)) return toast('The smallest withdrawal is ' + peso(info.min) + '.', true);
        if (amount > info.max) return toast('The most you can send at once is ' + peso(info.max) + '.', true);
        if (!code) return toast('Enter the 6-digit code from your authenticator app.', true);
        if (!confirm('Send ' + peso(amount) + ' to ' + a.name + ' (' + a.number + ')?\nThis cannot be undone.')) return;
        const b = $('wd-go'); b.disabled = true;
        try { const r = await api('/api/admin/wallet/withdraw', 'POST', { accountId: a.id, amount, code }); $('wd-amount').value = $('wd-code').value = ''; toast(r.message || 'Withdrawal sent.'); }
        catch (err) { toast(err.message, true); }
        b.disabled = false; $('wd-code').value = ''; await load();
    });
    initHeader(null); load();
})();
