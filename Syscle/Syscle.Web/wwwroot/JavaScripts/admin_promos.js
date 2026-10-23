(async function () {
    const $ = id => document.getElementById(id);
    if (!$('promo-table-body')) return;
    let promos = [];
    async function load() {
        try {
            promos = await api('/api/admin/promos');
            $('promo-table-body').innerHTML = promos.length ? promos.map(p => `<tr data-code="${esc(p.code)}">
        <td><b>${esc(p.code)}</b></td><td>${p.percent} %</td>
        <td><span class="sy-chip ${p.isActive ? 'ok' : 'warn'}">${p.isActive ? 'Active' : 'Off'}</span></td>
        <td><button class="sy-btn small" data-toggle="1">${p.isActive ? 'Turn off' : 'Turn on'}</button> <button class="sy-btn small ghost" data-pct="1">Change %</button> <button class="sy-x" data-del="1">Delete</button></td></tr>`).join('')
                : '<tr><td colspan="4" class="sy-empty">No promo codes yet.</td></tr>';
        } catch (e) { toast(e.message, true); }
    }
    $('pc-add').addEventListener('click', async () => {
        const code = $('pc-code').value.trim(), percent = +$('pc-pct').value;
        if (!code) return toast('Type a code.', true);
        try { await api('/api/admin/promos', 'POST', { code, percent }); $('pc-code').value = ''; await load(); toast('Promo code added.'); } catch (e) { toast(e.message, true); }
    });
    $('promo-table-body').addEventListener('click', async e => {
        const row = e.target.closest('tr[data-code]'); if (!row) return;
        const code = row.dataset.code, p = promos.find(x => x.code === code), url = '/api/admin/promos/' + encodeURIComponent(code);
        try {
            if (e.target.dataset.toggle) { await api(url, 'PUT', { isActive: !p.isActive }); await load(); }
            else if (e.target.dataset.pct) {
                const v = prompt('New discount percent for ' + code + ' (1-90):', p.percent); if (v === null) return;
                await api(url, 'PUT', { percent: +v }); await load(); toast('Updated.');
            } else if (e.target.dataset.del && confirm('Delete code ' + code + '?')) { await api(url, 'DELETE'); await load(); }
        } catch (err) { toast(err.message, true); }
    });
    initHeader(null); load();
})();
