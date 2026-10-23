// ADMIN INBOX: customers' chat messages. Pick a customer on the left, read, and reply.
(async function () {
    const $ = id => document.getElementById(id);
    if (!$('threads')) return;
    let threads = [], current = null, filter = '';

    function paintThreads() {
        const list = threads.filter(t => !filter || t.username.toLowerCase().includes(filter));
        $('threads').innerHTML = list.length ? list.map(t => `<button class="sy-thread${t.userId === current ? ' on' : ''}" data-id="${t.userId}"><b><span>${esc(t.username)}</span>${+t.unread ? `<span class="sy-unread">${+t.unread}</span>` : ''}</b><small>${esc(t.last || '')}</small></button>`).join('')
            : '<p class="sy-empty">No messages yet.</p>';
    }
    async function loadThreads() { try { threads = await api('/api/admin/messages'); paintThreads(); } catch (e) { toast(e.message, true); } }

    async function openThread(id) {
        current = id; const t = threads.find(x => x.userId === id);
        try {
            const rows = (await api('/api/admin/messages/' + id)).reverse();
            const keep = $('reply-text') ? $('reply-text').value : '';
            $('convo').innerHTML = `<div class="sy-toolbar"><h3 style="margin:0">${esc(t ? t.username : 'Customer')} <small style="font-weight:normal">C-${String(id).padStart(6, '0')}</small></h3><button class="sy-x small" id="del-thread" type="button">Delete conversation</button></div>
              <div class="sx-msgs" id="msgs">${rows.map(m => `<div class="sx-b ${m.sender === 'Staff' ? 'me' : 'them'}">${esc(m.body)}<small>${m.sender === 'Staff' ? 'Store' : 'Customer'} · ${esc(fmtDateTime(m.date))}</small></div>`).join('')}</div>
              <div class="sy-reply"><textarea id="reply-text" maxlength="500" placeholder="Type your reply..."></textarea><button class="sy-btn" id="reply-go" type="button">Send</button></div>`;
            $('reply-text').value = keep; const m = $('msgs'); m.scrollTop = m.scrollHeight;
            $('reply-text').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } });
            $('reply-go').addEventListener('click', send); $('del-thread').addEventListener('click', del);
            await loadThreads();
        } catch (e) { toast(e.message, true); }
    }
    async function send() {
        const text = $('reply-text').value.trim(); if (!text || current == null) return;
        $('reply-go').disabled = true;
        try { await api('/api/admin/messages/' + current, 'POST', { body: text }); $('reply-text').value = ''; await openThread(current); } catch (e) { toast(e.message, true); $('reply-go').disabled = false; }
    }
    async function del() {
        if (!confirm('Delete this whole conversation?')) return;
        try { await api('/api/admin/messages/' + current, 'DELETE'); current = null; $('convo').innerHTML = '<p class="sy-empty">Conversation deleted.</p>'; await loadThreads(); } catch (e) { toast(e.message, true); }
    }
    $('threads').addEventListener('click', e => { const b = e.target.closest('[data-id]'); if (b) openThread(+b.dataset.id); });
    initHeader(t => { filter = t; paintThreads(); });
    await loadThreads();
    // new messages show up by themselves every few seconds
    setInterval(async () => {
        const before = current != null ? threads.find(x => x.userId === current) : null, sig = before ? before.date + '|' + before.last : '';
        await loadThreads();
        const now = current != null ? threads.find(x => x.userId === current) : null;
        if (now && now.date + '|' + now.last !== sig) openThread(current);
    }, 8000);
})();
