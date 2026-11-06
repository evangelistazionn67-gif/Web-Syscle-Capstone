# Stock movement: copy-paste patch

The zip you already have (Web-Syscle-v3) does NOT include this. The new zip (Web-Syscle-v4) does. If you would rather paste by hand, do the steps below in order. Nothing else in the project changes.

What it does: every change to stock (online order, walk-in sale, cancelled order, restock +/-, item edited, new item, item deleted) is saved in a new table, and a new page **Stock movement** shows it with filters and CSV export.

## Step 1. Program.cs (backend), 9 small edits
Open `Syscle/Syscle.Web/Program.cs`. Use Ctrl+F for each "FIND" text.

### 1a. Create the table on start
FIND (inside `Seed.Migrate`):
```
        await c.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS Messages (
```
PASTE THIS RIGHT ABOVE IT:
```csharp
        await c.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS StockMovements (
            MovementId INT AUTO_INCREMENT PRIMARY KEY, ProductId INT NULL, ProductName VARCHAR(120) NOT NULL, Delta INT NOT NULL, StockAfter INT NOT NULL,
            Reason VARCHAR(40) NOT NULL, Ref VARCHAR(30) NULL, ByUser VARCHAR(50) NULL, CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            INDEX IX_Stock_Product (ProductId), INDEX IX_Stock_Date (CreatedAt)) ENGINE=InnoDB");
```

### 1b. Helper class + TakeStock
FIND the whole block that starts with `static class Orders` and `public static async Task<string?> TakeStock(` and REPLACE the first lines (up to and including the `if (n == 0) return l.Name;` line) with:
```csharp
// Every change to a product's stock is written here, so the Stock Movement page can show where each unit went.
static class StockLog
{
    public static Task Add(MySqlConnection c, MySqlTransaction? tx, int productId, int delta, string reason, string? reference, string? by) =>
        c.ExecuteAsync("INSERT INTO StockMovements(ProductId,ProductName,Delta,StockAfter,Reason,Ref,ByUser) SELECT ProductId, Name, @delta, Stock, @reason, @reference, @by FROM Products WHERE ProductId=@productId", new { productId, delta, reason, reference, by }, tx);

    // Gives the items of a cancelled order back to stock and records it.
    public static async Task ReturnOrder(MySqlConnection c, MySqlTransaction tx, int orderId, string? by)
    {
        await c.ExecuteAsync("UPDATE Products p JOIN (SELECT ProductId, SUM(Qty) AS q FROM OrderItems WHERE OrderId=@orderId AND ProductId IS NOT NULL GROUP BY ProductId) x ON x.ProductId = p.ProductId SET p.Stock = p.Stock + x.q WHERE 1=1", new { orderId }, tx);
        await c.ExecuteAsync(@"INSERT INTO StockMovements(ProductId,ProductName,Delta,StockAfter,Reason,Ref,ByUser)
            SELECT p.ProductId, p.Name, x.q, p.Stock, 'Order cancelled', (SELECT CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) FROM Orders o WHERE o.OrderId=@orderId), @by
            FROM (SELECT ProductId, SUM(Qty) AS q FROM OrderItems WHERE OrderId=@orderId AND ProductId IS NOT NULL GROUP BY ProductId) x JOIN Products p ON p.ProductId = x.ProductId", new { orderId, by }, tx);
    }
}

static class Orders
{
    public static async Task<string?> TakeStock(MySqlConnection c, MySqlTransaction tx, int orderId, List<Line> lines, string reason = "Online order", string prefix = "ORD-", string? by = null)
    {
        foreach (var l in lines)
        {
            var n = await c.ExecuteAsync("UPDATE Products SET Stock=Stock-@q WHERE ProductId=@id AND Stock>=@q", new { q = l.Qty, id = l.ProductId }, tx); if (n == 0) return l.Name;
            await StockLog.Add(c, tx, l.ProductId, -l.Qty, reason, prefix + orderId.ToString("D6"), by);
```
(The `INSERT INTO OrderItems...` line that follows stays as it is.)

### 1c. Walk-in sale
FIND: `admin.MapPost("/transactions/walkin", async (WalkinReq r, Db db) =>`
REPLACE WITH: `admin.MapPost("/transactions/walkin", async (WalkinReq r, Db db, ClaimsPrincipal u) =>`

FIND: `var soldOut = await Orders.TakeStock(c, tx, orderId, lines); if (soldOut != null) return Conflict($"Out of stock: {soldOut}");` (the one right above `var orderCode = "WLK-"`; the checkout one stays unchanged)
REPLACE WITH: `var soldOut = await Orders.TakeStock(c, tx, orderId, lines, "Walk-in sale", "WLK-", u.Identity?.Name); if (soldOut != null) return Conflict($"Out of stock: {soldOut}");`

### 1d. Cancelled orders (two places)
FIND (customer cancel, in `shop.MapPost("/orders/{id:int}/cancel"`): the line starting `await c.ExecuteAsync("UPDATE Products p JOIN (SELECT ProductId, SUM(Qty) AS q FROM OrderItems WHERE OrderId=@id ...`
REPLACE THE WHOLE LINE WITH:
```csharp
    await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name);
```
FIND (admin status endpoint): the line starting `if (r.Status == "Cancelled") await c.ExecuteAsync("UPDATE Products p JOIN ...`
REPLACE THE WHOLE LINE WITH:
```csharp
    if (r.Status == "Cancelled") await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name);
```
AND change the endpoint header:
FIND: `admin.MapPut("/transactions/{id:int}/status", async (int id, StatusReq r, Db db, EmailService emailer) =>`
REPLACE WITH: `admin.MapPut("/transactions/{id:int}/status", async (int id, StatusReq r, Db db, EmailService emailer, ClaimsPrincipal u) =>`

### 1e. Add / edit / delete item, and the +/- buttons
1. `admin.MapPost("/products", async (ProductReq r, Db db) =>` becomes `admin.MapPost("/products", async (ProductReq r, Db db, ClaimsPrincipal u) =>`
   and right after the line `var newId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)");` add:
```csharp
    if (r.Stock != 0) await StockLog.Add(c, null, newId, r.Stock, "New item", null, u.Identity?.Name);
```
2. `admin.MapPut("/products/{id:int}", async (int id, ProductReq r, Db db) =>` becomes `admin.MapPut("/products/{id:int}", async (int id, ProductReq r, Db db, ClaimsPrincipal u) =>`
   add this line right BEFORE the line `await c.ExecuteAsync("UPDATE Products SET CategoryId=@CategoryId...`:
```csharp
    var oldStock = await c.ExecuteScalarAsync<int?>("SELECT Stock FROM Products WHERE ProductId=@id", new { id });
```
   and this line right AFTER that UPDATE line (before `DELETE FROM ProductVariants`):
```csharp
    if (oldStock.HasValue && oldStock.Value != r.Stock) await StockLog.Add(c, null, id, r.Stock - oldStock.Value, "Edited by admin", null, u.Identity?.Name);
```
3. REPLACE the whole `admin.MapDelete("/products/{id:int}", ...` line with:
```csharp
admin.MapDelete("/products/{id:int}", async (int id, Db db, ClaimsPrincipal u) => { using var c = db.Open(); await c.ExecuteAsync("INSERT INTO StockMovements(ProductId,ProductName,Delta,StockAfter,Reason,ByUser) SELECT NULL, Name, -Stock, 0, 'Item deleted', @by FROM Products WHERE ProductId=@id AND Stock<>0", new { id, by = u.Identity?.Name }); await c.ExecuteAsync("DELETE FROM Products WHERE ProductId=@id", new { id }); return Results.Ok(new { ok = true }); });
```
4. REPLACE the whole `admin.MapPost("/products/{id:int}/stock", ...` line with this, which also adds the new list endpoint:
```csharp
admin.MapPost("/products/{id:int}/stock", async (int id, StockReq r, Db db, ClaimsPrincipal u) => { using var c = db.Open(); var n = await c.ExecuteAsync("UPDATE Products SET Stock=Stock+@d WHERE ProductId=@id AND Stock+@d>=0", new { d = r.Delta, id }); if (n > 0) await StockLog.Add(c, null, id, r.Delta, r.Delta > 0 ? "Restock (+)" : "Removed (-)", null, u.Identity?.Name); return Results.Ok(new { ok = true }); });
admin.MapGet("/stock-movements", async (string? search, string? reason, string? startDate, string? endDate, Db db) =>
{
    DateTime? fromDate = DateTime.TryParse(startDate, out var d1) ? d1.Date : null; DateTime? toDate = DateTime.TryParse(endDate, out var d2) ? d2.Date.AddDays(1) : null;
    using var c = db.Open();
    return Results.Ok(await c.QueryAsync(@"SELECT MovementId AS id, ProductId AS productId, ProductName AS product, Delta AS delta, StockAfter AS stockAfter, Reason AS reason, Ref AS reference, ByUser AS byUser, CreatedAt AS date FROM StockMovements
        WHERE (@search IS NULL OR ProductName LIKE CONCAT('%', @search, '%') OR Ref LIKE CONCAT('%', @search, '%')) AND (@reason IS NULL OR Reason = @reason) AND (@fromDate IS NULL OR CreatedAt >= @fromDate) AND (@toDate IS NULL OR CreatedAt < @toDate)
        ORDER BY MovementId DESC LIMIT 500", new { search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(), reason = string.IsNullOrWhiteSpace(reason) ? null : reason, fromDate, toDate }));
});
```

## Step 2. Two new files
Create these two files exactly as in the zip: `wwwroot/Pages/admin_stock.html` and `wwwroot/JavaScripts/admin_stock.js`. (Their full text is below.)

## Step 3. Menu link
In `wwwroot/JavaScripts/shell.js`, FIND the line starting `['Store-front', 'admin_storefront.html'` and paste this line right above it:
```js
    ['Stock movement', 'admin_stock.html', ic('<path d="M17 1l4 4-4 4"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><path d="M7 23l-4-4 4-4"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/>')],
```
Optional: in `Pages/admin_inventory.html`, put `<a class="sy-btn ghost" href="admin_stock.html">Stock movement</a>` next to the Add item button.

## Step 4. Database
Nothing to do: the app creates the table when it starts. If you prefer, run this once in Workbench:
```sql
CREATE TABLE IF NOT EXISTS StockMovements (
    MovementId INT AUTO_INCREMENT PRIMARY KEY, ProductId INT NULL, ProductName VARCHAR(120) NOT NULL, Delta INT NOT NULL, StockAfter INT NOT NULL,
    Reason VARCHAR(40) NOT NULL, Ref VARCHAR(30) NULL, ByUser VARCHAR(50) NULL, CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_Stock_Product (ProductId), INDEX IX_Stock_Date (CreatedAt)) ENGINE=InnoDB;
```

## How to test (2 minutes)
1. Admin > Inventory: press + on an item. Open Stock movement: a "Restock (+)" row appears.
2. As a customer place an order; then in Orders cancel it. You see "Online order" (minus) and "Order cancelled" (plus).
3. Do a walk-in sale: "Walk-in sale" row.

## Full text of the two new files
### wwwroot/Pages/admin_stock.html
```html
<!DOCTYPE html>
<html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><title>Syscle - Stock Movement</title><link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Barlow+Condensed:wght@600;700&family=Inter:wght@400;500;600;700;800&display=swap"><link rel="stylesheet" href="../Styles/ui.css"></head>
<body data-area="admin" data-search="Search item or order number...">
<main class="sy-page">
  <div class="sy-toolbar"><div><h1>Stock movement</h1><p class="sy-sub" style="margin:0">Every unit that came in or went out: sales, cancellations, restocks and edits.</p></div>
    <div class="sy-pactions"><a class="sy-btn ghost small" href="admin_inventory.html">&larr; Inventory</a><button class="sy-btn ghost small" id="sm-csv" type="button">Export CSV</button></div></div>
  <div class="sy-cards"><div class="sy-stat">Movements<b id="sm-n">0</b></div><div class="sy-stat">Units in<b id="sm-in" class="sy-ok">0</b></div><div class="sy-stat">Units out<b id="sm-out" class="sy-red">0</b></div><div class="sy-stat">Net change<b id="sm-net">0</b></div></div>
  <div class="sy-filters">
    <select id="sm-reason" class="sy-input" aria-label="Reason"><option value="">All reasons</option><option>Online order</option><option>Walk-in sale</option><option>Order cancelled</option><option>Restock (+)</option><option>Removed (-)</option><option>Edited by admin</option><option>New item</option><option>Item deleted</option></select>
    <label for="sm-from">From</label><input type="date" id="sm-from" class="sy-input"><label for="sm-to">To</label><input type="date" id="sm-to" class="sy-input">
    <button class="sy-btn" id="sm-go" type="button">Filter</button><button class="sy-btn ghost" id="sm-reset" type="button">Reset</button></div>
  <div class="sy-scroll"><table class="sy-table"><thead><tr><th>Date</th><th>Item</th><th>Reason</th><th>Order</th><th class="r">Change</th><th class="r">Stock after</th><th>By</th></tr></thead><tbody id="sm-body"><tr><td colspan="7" class="sy-empty">Loading...</td></tr></tbody></table></div>
  <p class="sy-sub" style="margin-top:12px">Shows the latest 500 movements. History starts from the day this feature was added.</p>
</main>
<script src="../JavaScripts/shell.js"></script><script src="../JavaScripts/api.js"></script><script src="../JavaScripts/admin_stock.js"></script><script src="../JavaScripts/support.js"></script>
</body></html>
```

### wwwroot/JavaScripts/admin_stock.js
```js
// STOCK MOVEMENT: a log of every change to stock (sales, cancellations, restocks, edits).
(async function () {
  const $ = id => document.getElementById(id); let rows = [], q = '';
  const tone = r => /cancel|restock|new item/i.test(r) ? 'ok' : /deleted|removed/i.test(r) ? 'bad' : /edited/i.test(r) ? 'info' : 'warn';
  async function load() {
    const qs = new URLSearchParams(); [['search', q], ['reason', $('sm-reason').value], ['startDate', $('sm-from').value], ['endDate', $('sm-to').value]].forEach(([k, v]) => { if (v) qs.set(k, v); });
    try { rows = await api('/api/admin/stock-movements?' + qs); } catch (e) { return toast(e.message, true); }
    const inn = rows.filter(r => r.delta > 0).reduce((s, r) => s + r.delta, 0), out = rows.filter(r => r.delta < 0).reduce((s, r) => s - r.delta, 0);
    $('sm-n').textContent = rows.length; $('sm-in').textContent = '+' + inn; $('sm-out').textContent = '-' + out; $('sm-net').textContent = (inn - out > 0 ? '+' : '') + (inn - out);
    $('sm-body').innerHTML = rows.length ? rows.map(r => `<tr><td>${fmtDateTime(r.date)}</td><td><b>${esc(r.product)}</b></td><td><span class="sy-chip ${tone(r.reason)}">${esc(r.reason)}</span></td><td>${esc(r.reference || '-')}</td>
      <td class="r"><b class="${r.delta > 0 ? 'sy-ok' : 'sy-red'}">${r.delta > 0 ? '+' : ''}${r.delta}</b></td><td class="r">${r.stockAfter}</td><td>${esc(r.byUser || (r.reason === 'Online order' ? 'Customer' : '-'))}</td></tr>`).join('') : '<tr><td colspan="7" class="sy-empty">No stock movements match.</td></tr>';
  }
  $('sm-go').addEventListener('click', load); $('sm-reason').addEventListener('change', load); $('sm-from').addEventListener('change', load); $('sm-to').addEventListener('change', load);
  $('sm-reset').addEventListener('click', () => { q = ''; const i = $('main-search-input'); if (i) i.value = ''; $('sm-reason').value = $('sm-from').value = $('sm-to').value = ''; load(); });
  $('sm-csv').addEventListener('click', () => {
    if (!rows.length) return toast('Nothing to export.', true); const e = v => '"' + String(v ?? '').replace(/"/g, '""') + '"';
    const csv = ['Date,Item,Reason,Order,Change,Stock after,By'].concat(rows.map(r => [fmtDateTime(r.date), r.product, r.reason, r.reference || '', r.delta, r.stockAfter, r.byUser || ''].map(e).join(','))).join('\r\n');
    const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' })); a.download = 'stock-movement.csv'; a.click(); URL.revokeObjectURL(a.href);
  });
  initHeader(t => { q = t; clearTimeout(window.__smT); window.__smT = setTimeout(load, 300); }); load();
})();
```
