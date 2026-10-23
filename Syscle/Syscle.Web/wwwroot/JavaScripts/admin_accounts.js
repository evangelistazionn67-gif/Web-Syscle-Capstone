(async function () {
    const $ = id => document.getElementById(id);
    if (!$('account-table-body')) return;
    let rows = [], q = '';
    async function load() { try { rows = await api('/api/admin/accounts'); render(); } catch (e) { toast(e.message, true); } }
    function render() {
        const list = rows.filter(a => (a.username + ' ' + a.email).toLowerCase().includes(q));
        $('account-table-body').innerHTML = list.length ? list.map(a => {
            const st = a.status === 'Suspended' ? '<td class="status-suspended">Suspended</td>' : a.online ? '<td class="status-online">Online</td>' : '<td class="status-offline">Offline</td>';
            return `<tr><td class="checkbox-col"><input type="checkbox" class="account-cb" data-id="${a.id}"></td>
        <td>${a.role === 'SuperAdmin' ? 'SA' : a.role === 'Admin' ? 'A' : 'C'}-${String(a.id).padStart(6, '0')}</td><td>${esc(a.username)} <small>(${esc(a.role)})</small></td><td>${esc(a.email)}</td><td>${esc(a.phone || '-')}</td>${st}
        <td class="${a.fpRequest ? 'fp-yes' : 'fp-no'}">${a.fpRequest ? 'Yes' : 'No'}</td></tr>`;
        }).join('')
            : '<tr><td colspan="7" class="sy-empty">No accounts found.</td></tr>';
    }
    const selected = () => [...document.querySelectorAll('.account-cb:checked')].map(c => rows.find(r => r.id === +c.dataset.id));
    async function each(fn, msg) {
        const sel = selected(); if (!sel.length) return toast('Tick at least one account first.', true);
        try { for (const a of sel) await fn(a); toast(msg); } catch (e) { toast(e.message, true); }
        await load();
    }

    $('btn-change-password').addEventListener('click', async () => {
        const sel = selected(); if (sel.length !== 1) return toast('Tick exactly one account.', true);
        const pw = prompt(`New password for ${sel[0].username} (8+ characters):`); if (!pw) return;
    try { await api(`/api/admin/accounts/${sel[0].id}/password`, 'POST', { password: pw }); toast('Password changed.'); } catch (e) { toast(e.message, true); }
    await load();
});

$('btn-logout').addEventListener('click', () => {
    if (!selected().length) { logoutUser(); return; }
    each(a => api(`/api/admin/accounts/${a.id}/logout`, 'POST'), 'Selected accounts were logged out.');
});

$('btn-suspend').addEventListener('click', () => each(a => api(`/api/admin/accounts/${a.id}/suspend`, 'POST', { suspend: true }), 'Account suspended.'));
$('btn-restore').addEventListener('click', () => each(a => api(`/api/admin/accounts/${a.id}/suspend`, 'POST', { suspend: false }), 'Account restored.'));

$('btn-delete').addEventListener('click', () => {
    const sel = selected(); if (!sel.length) return toast('Tick at least one account first.', true);
    if (confirm(`Permanently delete ${sel.length} account(s)? This cannot be undone.`)) each(a => api('/api/admin/accounts/' + a.id, 'DELETE'), 'Deleted.');
});

$('btn-add-admin').addEventListener('click', async () => {
    try {
        await api('/api/admin/accounts', 'POST', { username: $('new-admin-username').value, email: $('new-admin-email').value, password: $('new-admin-password').value });
        ['new-admin-username', 'new-admin-email', 'new-admin-password'].forEach(i => { $(i).value = ''; });
        toast('Admin account created.'); await load();
    } catch (e) { toast(e.message, true); }
});

const btnPromote = $('btn-promote');
if (btnPromote) btnPromote.addEventListener('click', () => {
    const sel = selected(); if (sel.length !== 1) return toast('Tick exactly one account.', true);
    each(a => api(`/api/admin/accounts/${a.id}/role`, 'POST', { role: 'Admin' }), 'Account promoted to Admin.');
});

const btnDemote = $('btn-demote');
if (btnDemote) btnDemote.addEventListener('click', () => {
    const sel = selected(); if (sel.length !== 1) return toast('Tick exactly one account.', true);
    each(a => api(`/api/admin/accounts/${a.id}/role`, 'POST', { role: 'Customer' }), 'Account demoted to Customer.');
});

initHeader(t => { q = t; render(); }); load();
}) ();