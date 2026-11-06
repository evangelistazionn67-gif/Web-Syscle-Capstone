// replace the browser pop-ups with a notice inside the page
const noticeEl = () => document.getElementById('sy-notice');
function notify(msg, ok) { const n = noticeEl(); if (!n) return; n.textContent = msg; n.className = 'sy-note-in show ' + (ok ? 'ok' : 'err'); n.scrollIntoView({ block: 'nearest' }); }
window.alert = m => notify(m, /created|changed|sent|code|link|request/i.test(m) && !/not|could|fail|invalid|enter|please/i.test(m));
// after login go back to where the visitor was (customers only; admins always land on the dashboard)
const params = new URLSearchParams(location.search);
const go = dest => { const n = params.get('next') || ''; const safe = /^\/Pages\/(homepage|category-view|cart|orders|account|settings)\.html/i.test(n); return safe && !/admin/i.test(dest || '') ? n : dest; };
const boxes = { login: 'login-container', register: 'register-container', forgot: 'forgot-container', twofa: 'twofa-container', sms: 'sms-container' };
function show(which) { Object.entries(boxes).forEach(([k, id]) => { const el = document.getElementById(id); if (el) el.style.display = k === which ? 'block' : 'none'; }); }
const wire = (id, which) => { const el = document.getElementById(id); if (el) el.addEventListener('click', e => { e.preventDefault(); show(which); }); };
wire('link-to-register', 'register'); wire('link-to-login-from-reg', 'login');
wire('link-to-forgot', 'forgot'); wire('link-to-login-from-forgot', 'login'); wire('link-to-sms', 'sms'); wire('link-to-login-from-sms', 'login');

async function post(url, body) {
    const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'syscle' }, body: JSON.stringify(body) });
    let data = {}; try { data = await r.json(); } catch (e) { }
    return { ok: r.ok, data };
}
const val = id => (document.getElementById(id) || {}).value || '';
const on = (id, fn) => { const el = document.getElementById(id); if (el) el.addEventListener('click', e => { e.preventDefault(); fn(); }); };

let temp2faTicket = null;
on('btn-login', async () => {
    const login = val('login-email').trim(), password = val('login-password');
    if (!login || !password) return alert('Please enter your email/username and password.');
    const { ok, data } = await post('/api/auth/login', { login, password });
    if (!ok) return alert(data.error || 'Login failed.');

    if (data.require2fa) {
        temp2faTicket = data.ticket;
        show('twofa');
        return;
    }
    location.href = go(data.redirect);
});
on('btn-login-2fa', async () => {
    const code = val('login-2fa').trim();
    if (!code) return alert('Enter your 2FA code.');
    const { ok, data } = await post('/api/auth/login-2fa', { ticket: temp2faTicket, code });
    if (!ok) return alert(data.error || 'Invalid code.');
    location.href = go(data.redirect);
});
on('btn-register', async () => {
    const username = val('reg-username').trim(), email = val('reg-email').trim(), password = val('reg-password');
    if (!username || !email || !password) return alert('Please fill in all fields.');
    if (!/^[A-Za-z0-9_.-]{3,14}$/.test(username)) return alert('Username must be 3 to 14 letters, numbers, dots, dashes or underscores.');
    if (password.length < 8 || password.length > 14) return alert('Password must be 8 to 14 characters.');
    if (password !== val('reg-confirm')) return alert('Passwords do not match.');
    const { ok, data } = await post('/api/auth/register', { username, email, password });
    if (!ok) return alert(data.error || 'Could not create the account.');
    alert('Account created. You can log in now.'); show('login');
});
on('btn-reset', async () => {
    const email = val('forgot-email').trim();
    if (!email) return alert('Please enter your email address.');
    const { ok, data } = await post('/api/auth/forgot', { email });
    if (!ok) return alert(data.error || 'Could not send the reset link.');
    // When the server has no email set up (local testing), it hands back the link so you can still reset your password.
    if (data.devLink) { if (confirm(data.message + '\n\nOpen the reset page now?')) location.href = data.devLink; show('login'); return; }
    alert(data.message || 'Request sent.'); show('login');
});

// ---- Forgot password by SMS: step 1 sends a code, step 2 checks it and saves the new password ----
let smsTicket = null;
on('btn-sms-send', async () => {
    const phone = val('sms-phone').trim();
    if (!phone) return alert('Please enter your mobile number.');
    const { ok, data } = await post('/api/auth/forgot-sms', { phone });
    if (!ok) return alert(data.error || 'Could not send the code.');
    smsTicket = data.ticket; alert(data.message || 'Code sent.');
    document.getElementById('sms-step1').style.display = 'none'; document.getElementById('sms-step2').style.display = 'block';
});
on('btn-sms-reset', async () => {
    const code = val('sms-code').trim(), password = val('sms-new');
    if (!/^\d{6}$/.test(code)) return alert('Enter the 6-digit code.');
    if (password.length < 8 || password.length > 14) return alert('Password must be 8 to 14 characters.');
    if (password !== val('sms-confirm')) return alert('Passwords do not match.');
    const { ok, data } = await post('/api/auth/reset-sms', { ticket: smsTicket, code, password });
    if (!ok) return alert(data.error || 'Could not change the password.');
    alert('Password changed. You can log in now.'); smsTicket = null;
    document.getElementById('sms-step1').style.display = 'block'; document.getElementById('sms-step2').style.display = 'none'; show('login');
});

fetch('/api/auth/me').then(r => r.ok ? r.json() : null).then(me => {
    if (me) location.href = (me.role === 'Admin' || me.role === 'SuperAdmin') ? '/Pages/adminhomepage.html' : go('/Pages/homepage.html');
}).catch(() => { });

if (params.get('signup')) show('register');
document.querySelectorAll('h1,h2,h3').forEach(h => { if (/Welcome to Syscle/i.test(h.textContent)) h.textContent = h.textContent.replace(/Welcome to Syscle/i, 'Welcome to Web-Syscle'); });
const EYE = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>';
const EYE_OFF = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>';
document.querySelectorAll('input[type="password"]').forEach(inp => {
    const wrap = document.createElement('span'); wrap.className = 'sy-eyewrap';
    inp.parentNode.insertBefore(wrap, inp); wrap.appendChild(inp);
    const b = document.createElement('button'); b.type = 'button'; b.className = 'sy-eye'; b.setAttribute('aria-label', 'Show password'); b.innerHTML = EYE;
    b.addEventListener('click', () => { const show = inp.type === 'password'; inp.type = show ? 'text' : 'password'; b.innerHTML = show ? EYE_OFF : EYE; b.setAttribute('aria-label', show ? 'Hide password' : 'Show password'); });
    wrap.appendChild(b);
});
// shown when the admin portal signed itself out because nobody was working
if (params.get('reason') === 'idle') setTimeout(() => notify('You were signed out because the admin portal was idle. Please log in again.', false), 300);
