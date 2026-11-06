// SETTINGS: customers get their own preferences; admins get store details and a status check for email / SMS.
(async function () {
    const $ = id => document.getElementById(id), body = $('settings-body');
    let me; try { me = await api('/api/auth/me'); } catch (e) { return; }
    const admin = me.role === 'Admin' || me.role === 'SuperAdmin';
    $('back-link').href = admin ? 'adminhomepage.html' : 'homepage.html'; $('back-link').innerHTML = admin ? '<i class="bi bi-arrow-left" aria-hidden="true"></i> Dashboard' : '<i class="bi bi-arrow-left" aria-hidden="true"></i> Back to Storefront';

    if (admin) {
        $('set-sub').textContent = 'Your store details show up in the customers’ help window.';
        let d; try { d = await api('/api/admin/store-info'); } catch (e) { return toast(e.message, true); }
        const f = (id, label, v, ph) => `<label for="${id}">${label}</label><input class="sy-input" id="${id}" maxlength="200" value="${esc(v)}" placeholder="${esc(ph || '')}">`;
        body.innerHTML = `<div class="sy-card"><h3>Store details</h3>
            ${f('s-name', 'Store name', d.info.StoreName, 'Web-Syscle')}${f('s-addr', 'Address', d.info.StoreAddress, 'Street, city')}${f('s-phone', 'Store phone / Viber', d.info.StorePhone, '09XX XXX XXXX')}
            ${f('s-hours', 'Opening hours', d.info.StoreHours, 'Mon-Sat 9am-6pm')}${f('s-note', 'Pick-up note for customers', d.info.PickupNote, 'Bring your order number')}
            <button class="sy-btn" id="s-save" type="button" style="margin-top:10px">SAVE STORE DETAILS</button></div>
          <div class="sy-card" id="del-card"><h3>Home delivery &amp; receipts</h3>
            <p class="sy-sub">You deliver with Lalamove, so set where your shop is and how far you go. Customers pin their place on a map; the price and the range are checked on the server.</p>
            <div id="st-map" class="sy-map" aria-label="Map: tap your shop's place"></div>
            <div class="sy-maprow"><button class="sy-btn small ghost" type="button" id="st-locate">Use my current location</button><span class="sy-sub" id="st-pinmsg"></span></div>
            <div class="sy-grid2"><div>${f('s-lat', 'Shop latitude', d.info.StoreLat, '14.5995')}</div><div>${f('s-lng', 'Shop longitude', d.info.StoreLng, '120.9842')}</div></div>
            <div class="sy-grid2"><div>${f('s-maxkm', 'Delivery range (km by road)', d.info.DeliveryMaxKm, '8')}</div><div>${f('s-cod', 'Cash on delivery limit (PHP, 0 = no limit)', d.info.CodMax, '5000')}</div></div>
            <div class="sy-grid2"><div>${f('s-base', 'Delivery base fee (PHP)', d.info.DeliveryBaseFee, '50')}</div><div>${f('s-perkm', 'Fee per km (PHP)', d.info.DeliveryPerKm, '12')}</div></div>
            <p class="sy-sub" id="st-est"></p>
            <label for="s-svc">Lalamove vehicle</label><select class="sy-input" id="s-svc"><option value="MOTORCYCLE">Motorcycle</option><option value="SEDAN">Sedan</option><option value="MPV">MPV</option><option value="VAN">Van</option></select>
            <p class="sy-sub" id="st-lala"></p>
            ${f('s-tin', 'TIN / permit number on receipts (optional)', d.info.StoreTIN, '')}${f('s-foot', 'Receipt footer message', d.info.ReceiptFooter, 'Thank you!')}
            <div class="sy-switch"><span>Print the receipt by itself after a sale</span><button class="toggle-btn${d.info.AutoPrintReceipt !== '0' ? ' active' : ''}" id="s-auto" type="button">${d.info.AutoPrintReceipt !== '0' ? 'ON' : 'OFF'}</button></div>
            <p class="sy-sub">Delivery is switched on once the shop location is set. The customer pays: base fee + fee per km, rounded up to the next PHP 5. Check Lalamove's rate card and adjust these two numbers so the fee covers the rider.</p>
            <button class="sy-btn" id="s-save2" type="button" style="margin-top:10px">SAVE DELIVERY &amp; RECEIPTS</button></div>
          <div class="sy-grid2"><div class="sy-card"><h3>Password recovery</h3>
            <p>Email reset links: <span class="sy-status ${d.emailReady ? 'on' : 'off'}">${d.emailReady ? 'ON' : 'NOT SET UP'}</span></p>
            <p>SMS reset codes: <span class="sy-status ${d.smsReady ? 'on' : 'off'}">${d.smsReady ? 'ON' : 'NOT SET UP'}</span></p>
            <p class="sy-sub">${d.smsReady && d.emailReady ? 'Customers can recover their account by email or by mobile number.' : 'Not set up means the server has no email / SMS key yet. See the README (Email and SMS section) to add them. While testing, the code or link is shown on screen.'}</p></div>
          <div class="sy-card"><h3>Shortcuts</h3><div class="sy-links"><a class="sy-btn small" href="admin_messages.html">Customer messages</a><a class="sy-btn small ghost" href="admin_payment_setup.html">Payment setup</a><a class="sy-btn small ghost" href="admin_account_management.html">Accounts</a><a class="sy-btn small ghost" href="account.html">My account &amp; 2FA</a></div></div></div>`;
        $('s-save').addEventListener('click', async () => {
            try { await api('/api/admin/store-info', 'PUT', { StoreName: $('s-name').value, StoreAddress: $('s-addr').value, StorePhone: $('s-phone').value, StoreHours: $('s-hours').value, PickupNote: $('s-note').value }); toast('Store details saved.'); }
            catch (e) { toast(e.message, true); }
        });
        // ----- delivery card -----
        const num = id => $(id).value.trim();
        const est = () => { const b = parseFloat(num('s-base')) || 0, k = parseFloat(num('s-perkm')) || 0, m = parseFloat(num('s-maxkm')) || 0; const fee = km => Math.ceil((b + k * km) / 5) * 5; $('st-est').textContent = 'Example fees: 2 km = ' + peso(fee(2)) + ', 5 km = ' + peso(fee(5)) + ', ' + m + ' km (farthest) = ' + peso(fee(m)) + '.'; };
        ['s-base', 's-perkm', 's-maxkm'].forEach(id => $(id).addEventListener('input', est)); est();
        $('s-svc').value = d.info.LalamoveService || 'MOTORCYCLE';
        $('s-auto').addEventListener('click', () => { const on = !$('s-auto').classList.contains('active'); $('s-auto').classList.toggle('active', on); $('s-auto').textContent = on ? 'ON' : 'OFF'; });
        api('/api/admin/delivery/status').then(t => { $('st-lala').innerHTML = t.lalamoveReady ? (t.production ? '<span class="sy-chip ok">Lalamove: live</span>' : '<span class="sy-chip warn">Lalamove: sandbox (practice, no real rider)</span>') : '<span class="sy-chip mute">Lalamove API keys not set on the server yet</span> You can still book in the Lalamove app and type the booking into the order.'; }).catch(() => { });
        let smap, smark; const setShop = (lat, lng, move) => { $('s-lat').value = (+lat).toFixed(6); $('s-lng').value = (+lng).toFixed(6); $('st-pinmsg').textContent = 'Shop pin set.'; if (smap) { if (smark) smark.setLatLng([lat, lng]); else smark = L.marker([lat, lng]).addTo(smap); if (move) smap.setView([lat, lng], 16); } };
        (async () => {
            try {
                if (!window.L) {
                    const css = document.createElement('link'); css.rel = 'stylesheet'; css.href = 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.min.css'; document.head.appendChild(css);
                    await new Promise((ok, no) => { const sc = document.createElement('script'); sc.src = 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.min.js'; sc.onload = ok; sc.onerror = no; document.head.appendChild(sc); });
                }
                const la = parseFloat(num('s-lat')), ln = parseFloat(num('s-lng')), has = isFinite(la) && isFinite(ln) && !(la === 0 && ln === 0);
                smap = L.map('st-map', { scrollWheelZoom: false }).setView(has ? [la, ln] : [14.5995, 120.9842], has ? 15 : 11);
                L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; OpenStreetMap contributors' }).addTo(smap);
                smap.on('click', e => setShop(e.latlng.lat, e.latlng.lng, false)); if (has) setShop(la, ln, false);
                setTimeout(() => smap.invalidateSize(), 80);
            } catch (e) { $('st-map').innerHTML = '<p class="sy-sub" style="padding:14px">The map could not load. Type the latitude and longitude instead (right-click your shop in Google Maps to copy them).</p>'; }
        })();
        $('st-locate').addEventListener('click', () => {
            if (!navigator.geolocation) return toast('This browser cannot share its location.', true);
            navigator.geolocation.getCurrentPosition(p => setShop(p.coords.latitude, p.coords.longitude, true), () => toast('Could not get your location. Tap the map instead.', true), { enableHighAccuracy: true, timeout: 12000 });
        });
        $('s-save2').addEventListener('click', async () => {
            const la = parseFloat(num('s-lat')), ln = parseFloat(num('s-lng'));
            if (num('s-lat') !== '' || num('s-lng') !== '') { if (!(la >= -90 && la <= 90 && ln >= -180 && ln <= 180)) return toast('Check the shop latitude and longitude.', true); }
            try {
                await api('/api/admin/store-info', 'PUT', { StoreLat: num('s-lat'), StoreLng: num('s-lng'), DeliveryMaxKm: num('s-maxkm'), CodMax: num('s-cod'), DeliveryBaseFee: num('s-base'), DeliveryPerKm: num('s-perkm'), LalamoveService: $('s-svc').value, StoreTIN: num('s-tin'), ReceiptFooter: num('s-foot'), AutoPrintReceipt: $('s-auto').classList.contains('active') ? '1' : '0' });
                toast('Delivery and receipt settings saved.');
            } catch (e) { toast(e.message, true); }
        });
        return;
    }

    $('set-sub').textContent = 'Your preferences for shopping at Web-Syscle.';
    let methods = [], prefs = { emailNotify: true }, saved = '';
    try { methods = await api('/api/payment-methods'); prefs = await api('/api/me/prefs'); } catch (e) { }
    try { saved = localStorage.getItem('syscle.pay') || ''; } catch (e) { }
    body.innerHTML = `<div class="sy-card" id="payment"><h3>Payment</h3>
        <p class="sy-sub">Pick the method that is selected for you when you open your cart. You can still change it at checkout.</p>
        <label for="p-method">Preferred payment</label>
        <select class="sy-input" id="p-method"><option value="">No preference</option>${methods.map(m => `<option value="${esc(m.code)}"${m.code === saved ? ' selected' : ''}>${esc(m.label)}</option>`).join('')}</select>
        <p class="sy-sub">GCash orders need the reference number from your GCash receipt. Cash is paid at pick-up or when your delivery arrives.</p></div>
      <div class="sy-card"><h3>Notifications</h3>
        <div class="sy-switch"><span>Email me when my order status changes</span><button class="toggle-btn${prefs.emailNotify ? ' active' : ''}" id="p-mail" type="button">${prefs.emailNotify ? 'ON' : 'OFF'}</button></div></div>
      <div class="sy-card"><h3>Account &amp; security</h3>
        <p class="sy-sub">Change your email, mobile number (used for SMS password recovery), password, or turn on 2FA.</p>
        <div class="sy-links"><a class="sy-btn small" href="account.html">Edit account</a><a class="sy-btn small ghost" href="account.html#history-section">My orders</a></div></div>
      <div class="sy-card"><h3>Need help?</h3><p class="sy-sub">Open the help window for quick answers or to message the store.</p>
        <button class="sy-btn small accent" id="p-help" type="button">Open help</button></div>`;
    $('p-method').addEventListener('change', () => { try { localStorage.setItem('syscle.pay', $('p-method').value); } catch (e) { } toast('Saved.'); });
    $('p-mail').addEventListener('click', async () => {
        const on = !$('p-mail').classList.contains('active');
        try { await api('/api/me/prefs', 'PUT', { emailNotify: on }); $('p-mail').classList.toggle('active', on); $('p-mail').textContent = on ? 'ON' : 'OFF'; toast('Saved.'); } catch (e) { toast(e.message, true); }
    });
    $('p-help').addEventListener('click', () => { if (window.openHelp) window.openHelp(); });
    if (location.hash === '#payment') $('payment').scrollIntoView();
})();
