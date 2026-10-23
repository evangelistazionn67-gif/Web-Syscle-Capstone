(async function () {
    const $ = id => document.getElementById(id);
    if (!$('btn-apply')) return;
    const btns = [...document.querySelectorAll('.toggle-btn')].filter(b => b.dataset.code);
    const paint = b => { b.textContent = b.classList.contains('active') ? 'ON' : 'OFF'; };

    btns.forEach(b => b.addEventListener('click', () => { b.classList.toggle('active'); paint(b); }));
    const qrToggle = $('qr-toggle');
    if (qrToggle) qrToggle.addEventListener('click', () => { qrToggle.classList.toggle('active'); paint(qrToggle); });

    try {
        const list = await api('/api/admin/payment-methods');
        btns.forEach(b => { const m = list.find(x => x.code === b.dataset.code); b.classList.toggle('active', !!(m && m.isOn)); paint(b); });
        const g = list.find(x => x.code === 'gcash');
        if (g && $('gcash-number')) { $('gcash-number').value = g.accountNumber; $('gcash-name').value = g.accountName; }

        const qrData = await api('/api/settings/payment-qr');
        if (qrToggle) { qrToggle.classList.toggle('active', qrData.show); paint(qrToggle); }
        if (qrData.image && $('qr-preview')) { $('qr-preview').src = qrData.image; $('qr-preview').style.display = 'block'; }
    } catch (e) { toast(e.message, true); }

    if ($('qr-file')) $('qr-file').addEventListener('change', () => {
        const f = $('qr-file').files[0];
        if (f && $('qr-preview')) { $('qr-preview').src = URL.createObjectURL(f); $('qr-preview').style.display = 'block'; }
    });

    $('btn-apply').addEventListener('click', async () => {
        const body = btns.map(b => ({
            code: b.dataset.code, isOn: b.classList.contains('active'),
            accountNumber: b.dataset.code === 'gcash' ? $('gcash-number').value : '', accountName: b.dataset.code === 'gcash' ? $('gcash-name').value : ''
        }));
        try {
            await api('/api/admin/payment-methods', 'PUT', body);

            const fd = new FormData();
            fd.append('show', qrToggle.classList.contains('active'));
            const file = $('qr-file').files[0];
            if (file) fd.append('image', file);
            await api('/api/admin/settings/payment-qr', 'POST', fd);

            toast('Payment settings saved.');
        } catch (e) { toast(e.message, true); }
    });
    initHeader(null);
})();