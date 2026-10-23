(async function () {
    const $ = id => document.getElementById(id);
    if (!$('review-table-body')) return;
    let reviews = [], q = '';

    async function load() {
        try {
            reviews = await api('/api/admin/reviews');
            render();
        } catch (e) { toast(e.message, true); }
    }

    function render() {
        const list = reviews.filter(r => (r.product + ' ' + r.username + ' ' + (r.comment || '')).toLowerCase().includes(q));
        $('review-table-body').innerHTML = list.length ? list.map(r => `
      <tr data-id="${r.id}">
        <td>${fmtDate(r.date)}</td>
        <td>${esc(r.product)}</td>
        <td>${esc(r.username)}</td>
        <td><b style="color:#ffb400;">${r.rating} ★</b></td>
        <td style="text-align:left; max-width:400px;">${esc(r.comment || '')}</td>
        <td><button class="sy-x" data-del="1">Delete</button></td>
      </tr>
    `).join('') : '<tr><td colspan="6" class="sy-empty">No reviews found.</td></tr>';
    }

    $('review-table-body').addEventListener('click', async e => {
        if (!e.target.dataset.del) return;
        const row = e.target.closest('tr[data-id]');
        if (confirm('Permanently delete this review? This cannot be undone.')) {
            try {
                await api('/api/admin/reviews/' + row.dataset.id, 'DELETE');
                await load();
                toast('Review deleted.');
            } catch (err) { toast(err.message, true); }
        }
    });

    initHeader(t => { q = t; render(); });
    load();
})();