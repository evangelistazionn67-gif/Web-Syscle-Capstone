// SETTINGS: customers get their own preferences; admins get store details and a status check for email / SMS.
(async function () {
    const $ = id => document.getElementById(id), body = $('settings-body');
    let me; try { me = await api('/api/auth/me'); } catch (e) { return; }
    const admin = me.role === 'Admin' || me.role === 'SuperAdmin';
    $('back-link').href = admin ? 'adminhomepage.html' : 'homepage.html'; $('back-link').innerHTML = admin ? '&larr; Dashboard' : '&larr; Back to Storefront';
    $('hdr-sub').textContent = admin ? 'Admin Panel' : 'Shop & Customize';

    if (admin) {
        $('set-sub').textContent = 'Your store details show up in the customers’ help window.';
        let d; try { d = await api('/api/admin/store-info'); } catch (e) { return toast(e.message, true); }
        const f = (id, label, v, ph) => `<label for="${id}">${label}</label><input class="sy-input" id="${id}" maxlength="200" value="${esc(v)}" placeholder="${esc(ph || '')}">`;
        body.innerHTML = `<div class="sy-card"><h3>Store details</h3>
            ${f('s-name', 'Store name', d.info.StoreName, 'Web-Syscle')}${f('s-addr', 'Address', d.info.StoreAddress, 'Street, city')}${f('s-phone', 'Store phone / Viber', d.info.StorePhone, '09XX XXX XXXX')}
            ${f('s-hours', 'Opening hours', d.info.StoreHours, 'Mon-Sat 9am-6pm')}${f('s-note', 'Pick-up note for customers', d.info.PickupNote, 'Bring your order number')}
            <button class="sy-btn" id="s-save" type="button" style="margin-top:10px">SAVE STORE DETAILS</button></div>
          <div class="sy-grid2"><div class="sy-card"><h3>Password recovery</h3>
            <p>Email reset links: <span class="sy-status ${d.emailReady ? 'on' : 'off'}">${d.emailReady ? 'ON' : 'NOT SET UP'}</span></p>
            <p>SMS reset codes: <span class="sy-status ${d.smsReady ? 'on' : 'off'}">${d.smsReady ? 'ON' : 'NOT SET UP'}</span></p>
            <p class="sy-sub">${d.smsReady && d.emailReady ? 'Customers can recover their account by email or by mobile number.' : 'Not set up means the server has no email / SMS key yet. See the README (Email and SMS section) to add them. While testing, the code or link is shown on screen.'}</p></div>
          <div class="sy-card"><h3>Shortcuts</h3><div class="sy-links"><a class="sy-btn small" href="admin_messages.html">Customer messages</a><a class="sy-btn small ghost" href="admin_payment_setup.html">Payment setup</a><a class="sy-btn small ghost" href="admin_account_management.html">Accounts</a><a class="sy-btn small ghost" href="account.html">My account &amp; 2FA</a></div></div></div>`;
        $('s-save').addEventListener('click', async () => {
            try { await api('/api/admin/store-info', 'PUT', { StoreName: $('s-name').value, StoreAddress: $('s-addr').value, StorePhone: $('s-phone').value, StoreHours: $('s-hours').value, PickupNote: $('s-note').value }); toast('Store details saved.'); }
            catch (e) { toast(e.message, true); }
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
        <p class="sy-sub">GCash orders need the reference number from your GCash receipt. Cash is paid at pick-up.</p></div>
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
