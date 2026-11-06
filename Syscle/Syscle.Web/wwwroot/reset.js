// Reset page: the token comes from the link in the email (or from the link shown on screen while email is not set up).
const token = new URLSearchParams(location.search).get('token') || '';
const $ = id => document.getElementById(id);
if (!token) { alert('This page needs the link from your reset email. Please request a new one.'); location.href = '/login.html'; }
$('btn-save-password').addEventListener('click', async e => {
    e.preventDefault();
    const password = $('new-password').value;
    if (password.length < 8 || password.length > 14) return alert('Password must be 8 to 14 characters.');
    if (password !== $('new-confirm').value) return alert('Passwords do not match.');
    const r = await fetch('/api/auth/reset', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'syscle' }, body: JSON.stringify({ token, password }) });
    let data = {}; try { data = await r.json(); } catch (err) { }
    if (!r.ok) return alert(data.error || 'Could not change the password.');
    alert('Password changed. You can log in now.'); location.href = '/login.html';
});
