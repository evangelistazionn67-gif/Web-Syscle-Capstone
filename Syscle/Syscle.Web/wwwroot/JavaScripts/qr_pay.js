// Shared QR Ph payment box (walk-in screen and the order list). Needs api.js (api, showModal, peso, toast).
window.SyQr = (function () {
    function show(orderId, after, canCancelSale) {
        let timer = null, tick = null, left = 1800, closed = false;
        const m = showModal(`<h2>Scan to pay</h2><p class="sy-sub" id="qr-sub">Making the QR code...</p><div style="text-align:center"><img id="qr-img" alt="QR Ph payment code" style="display:block;margin:0 auto;width:min(100%,300px);aspect-ratio:1;background:#f3f4f6;border-radius:12px"><div class="sy-bigamt" id="qr-amt"></div><p class="sy-sub" id="qr-left" style="text-align:center"></p></div>
          <p class="sy-sub">The customer opens GCash, Maya or any bank app, taps Scan QR and pays. This sale closes by itself when the payment arrives.</p>
          <div class="sy-modal-actions"><button class="sy-btn ghost" type="button" id="qr-cancel">Cancel sale</button><button class="sy-btn" type="button" id="qr-new" hidden>New QR code</button></div>`);
        const q = id => m.el.querySelector('#' + id);
        const stop = () => { closed = true; clearInterval(timer); clearInterval(tick); };
        async function make() {
            q('qr-new').hidden = true; q('qr-sub').textContent = 'Making the QR code...'; left = 1800;
            try {
                const r = await api('/api/admin/orders/' + orderId + '/qr', 'POST'); q('qr-img').src = r.image; q('qr-amt').textContent = peso(r.amount); q('qr-sub').textContent = 'Order ' + r.code + '. Ask the customer to scan this.';
                clearInterval(timer); clearInterval(tick);
                timer = setInterval(async () => { try { const s = await api('/api/admin/orders/' + orderId + '/qr-status'); if (s.paid && !closed) { stop(); m.close(); after(true); } else if (s.cancelled && !closed) { stop(); m.close(); after(false); } } catch (e) { } }, 3000);
                tick = setInterval(() => { left--; q('qr-left').textContent = left > 0 ? 'Code is good for ' + Math.floor(left / 60) + ':' + String(left % 60).padStart(2, '0') : 'Code expired.'; if (left <= 0) { clearInterval(timer); clearInterval(tick); q('qr-new').hidden = false; } }, 1000);
            } catch (e) { q('qr-sub').textContent = e.message; q('qr-new').hidden = false; }
        }
        q('qr-new').onclick = make;
        if (!canCancelSale) q('qr-cancel').textContent = 'Close';
        q('qr-cancel').onclick = async () => { stop(); if (!canCancelSale) { m.close(); return after(false, true); } try { await api('/api/admin/orders/' + orderId + '/qr-cancel', 'POST'); } catch (e) { toast(e.message, true); } m.close(); after(false); };
        make();
    }
    return { show: show };
})();
