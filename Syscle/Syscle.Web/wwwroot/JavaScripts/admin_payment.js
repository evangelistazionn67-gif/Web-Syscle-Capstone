(async function () {
    const $ = id => document.getElementById(id);
    if (!$('btn-apply')) return;
    const btns = [...document.querySelectorAll('.toggle-btn')].filter(b => b.dataset.code);
    const paint = b => { b.textContent = b.classList.contains('active') ? 'ON' : 'OFF'; };

    btns.forEach(b => b.addEventListener('click', () => { b.classList.toggle('active'); paint(b); }));

    try {
        const list = await api('/api/admin/payment-methods');
        btns.forEach(b => { const m = list.find(x => x.code === b.dataset.code); b.classList.toggle('active', !!(m && m.isOn)); paint(b); });
        [['paymongo', 'PayMongo']].forEach(([code, name]) => {
            const m = list.find(x => x.code === code), el = $('st-' + code); if (!el) return;
            el.textContent = m && m.ready
                ? (code === 'paymongo' ? 'PayMongo keys are set. At checkout, PayMongo shows a QR code made for the exact order total (scan it with any bank or e-wallet app), plus GCash, Maya, cards and the other networks you activated in PayMongo. The order is marked paid automatically.' : '')
                : name + ' is not connected yet: add its keys in appsettings.json (see docs/PAYMONGO_DELIVERY_SETUP.md). Customers will not see it until then.';
        });
    } catch (e) { toast(e.message, true); }

    $('btn-apply').addEventListener('click', async () => {
        const body = btns.map(b => ({ code: b.dataset.code, isOn: b.classList.contains('active') }));
        try { await api('/api/admin/payment-methods', 'PUT', body); toast('Payment settings saved.'); }
        catch (e) { toast(e.message, true); }
    });
    initHeader(null);
})();
