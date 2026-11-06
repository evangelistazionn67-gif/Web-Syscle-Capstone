using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using QRCoder;

// Newer endpoints: refunds, confirm-items / confirm-payment, review rules, lookups (dropdown lists), seasons, store rules, audit log,
// the 14-day sales trend with its earnings forecast, scan lookups and the test-mode payment page.
record RefundReq(string? Reason);
record RefundDecisionReq(bool Approve, string? Note, bool Restock = true);
record PayConfirmReq(string? Ref);
record LookupReq(string? Kind, string? Value, string? Extra);
record SeasonNewReq(string? Name, string? Kind, string? StartMD, string? EndMD);
record SeasonEditReq(bool? IsOn, string? StartMD, string? EndMD);
class PubPromo { public string Code { get; set; } = ""; public int Percent { get; set; } public string? Season { get; set; } public DateTime? EndsAt { get; set; } public int? MaxUses { get; set; } public int Used { get; set; } public int? MaxPerUser { get; set; } public int Mine { get; set; } }
class PayMethodRow { public string Code { get; set; } = ""; public string Label { get; set; } = ""; public string? AccountNumber { get; set; } public string? AccountName { get; set; } }
class RefundOrder
{
    public int OrderId { get; set; } public int? UserId { get; set; } public string Status { get; set; } = ""; public string PaymentStatus { get; set; } = ""; public string RefundStatus { get; set; } = "None";
    public decimal Total { get; set; } public DateTime CreatedAt { get; set; } public DateTime? PaidAt { get; set; } public DateTime? CompletedAt { get; set; }
    public string? Code { get; set; } public string? Email { get; set; } public string? CustomerName { get; set; } public bool EmailNotify { get; set; } = true; public string? PaymentMethod { get; set; }
}
class ConfirmRow { public string Status { get; set; } = ""; public string PaymentStatus { get; set; } = ""; public DateTime? ItemsConfirmedAt { get; set; } public string? PaymentRef { get; set; } public string? PaymentMethod { get; set; } }
class DayAmount { public DateTime Day { get; set; } public decimal Sales { get; set; } public decimal Cost { get; set; } public decimal ItemRev { get; set; } }

static class Features
{
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static IResult Conflict(string m) => Results.Conflict(new { error = m });
    static int Uid(ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // A customer may review an item only after an order with that item was completed and paid (and not refunded).
    public static async Task<bool> BoughtAsync(MySqlConnection c, int userId, int productId) =>
        await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders o JOIN OrderItems oi ON oi.OrderId=o.OrderId WHERE o.UserId=@userId AND oi.ProductId=@productId AND o.Status='Completed' AND o.PaymentStatus='Paid' AND o.RefundStatus<>'Refunded'", new { userId, productId }) > 0;

    public static void Map(WebApplication app)
    {
        var shop = app.MapGroup("/api").RequireAuthorization("Customer");
        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        // ---------- public: a QR code picture for a short text (order numbers, item SKUs). It holds no private data. ----------
        app.MapGet("/api/qr", (string? text) =>
        {
            var t = (text ?? "").Trim(); if (t.Length == 0 || t.Length > 120) return Results.BadRequest(new { error = "Bad text." });
            using var gen = new QRCodeGenerator(); using var data = gen.CreateQrCode(t, QRCodeGenerator.ECCLevel.M); var png = new PngByteQRCode(data).GetGraphic(10);
            return Results.File(png, "image/png");
        });

        // ---------- public: popup data (promo codes + live bundles) ----------
        app.MapGet("/api/promotions", async (Db db, ClaimsPrincipal u) =>
        {
            using var c = db.Open(); var today = Promos.Today; var states = await Seasons.StatesAsync(c);
            // a signed-in customer only sees the codes they can still use (a code limited to one use per customer disappears once used)
            int? uid = u.Identity?.IsAuthenticated == true && u.IsInRole("Customer") ? Uid(u) : null;
            var rows = (await c.QueryAsync<PubPromo>("SELECT Code, DiscountPercent AS Percent, Season, EndsAt, MaxUses, MaxPerUser, (SELECT COUNT(*) FROM Orders o WHERE o.PromoCode=PromoCodes.Code AND o.Status<>'Cancelled') AS Used, (SELECT COUNT(*) FROM Orders o2 WHERE o2.PromoCode=PromoCodes.Code AND o2.UserId=@uid AND o2.Status<>'Cancelled') AS Mine FROM PromoCodes WHERE IsActive=1 AND IsPublic=1 AND (StartsAt IS NULL OR StartsAt<=@today) AND (EndsAt IS NULL OR EndsAt>=@today) ORDER BY DiscountPercent DESC", new { today, uid })).ToList();
            var codes = rows.Where(p => (p.MaxUses == null || p.Used < p.MaxUses) && (p.MaxPerUser == null || p.Mine < p.MaxPerUser) && Seasons.Check(states, p.Season).live)
                .Select(p => new { code = p.Code, percent = p.Percent, season = p.Season, endsAt = p.EndsAt?.ToString("yyyy-MM-dd"), left = p.MaxUses == null ? (int?)null : p.MaxUses - p.Used, perUser = p.MaxPerUser, myLeft = p.MaxPerUser == null ? (int?)null : p.MaxPerUser - p.Mine }).ToList();
            var bundles = (await Bundles.ListAsync(c, true)).Take(4).ToList();
            return Results.Ok(new { popup = await Cfg.GetAsync(c, "PromoPopup") == "1", codes, bundles });
        });

        // ---------- public: the dropdown lists (item types, size sets, colors, bike types) ----------
        app.MapGet("/api/lookups", async (Db db) =>
        {
            using var c = db.Open();
            var rows = (await c.QueryAsync<(string Kind, string Value, string? Extra)>("SELECT Kind, Value, Extra FROM Lookups ORDER BY Kind, SortOrder, Value")).ToList();
            return Results.Ok(new
            {
                itemTypes = rows.Where(r => r.Kind == "ItemType").Select(r => r.Value),
                colors = rows.Where(r => r.Kind == "Color").Select(r => r.Value),
                bikeTypes = rows.Where(r => r.Kind == "BikeType").Select(r => r.Value),
                sizeSets = rows.Where(r => r.Kind == "SizeSet").Select(r => new { label = r.Value, sizes = r.Extra })
            });
        });

        // ---------- customer: reviews only after buying ----------
        shop.MapGet("/products/{id:int}/review-status", async (int id, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open(); var bought = await BoughtAsync(c, Uid(u), id);
            var done = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Reviews WHERE ProductId=@id AND UserId=@uid", new { id, uid = Uid(u) }) > 0;
            return Results.Ok(new { bought, reviewed = done, canReview = bought && !done });
        });
        shop.MapGet("/orders/{id:int}/items", async (int id, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open();
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE OrderId=@id AND UserId=@uid", new { id, uid = Uid(u) }) == 0) return Results.NotFound(new { error = "Not found." });
            return Results.Ok(await c.QueryAsync(@"SELECT oi.ProductId AS productId, oi.ProductName AS name, oi.Size AS size, oi.Color AS color, oi.Qty AS qty, oi.UnitPrice AS price,
                (SELECT COUNT(*) FROM Reviews r WHERE r.ProductId=oi.ProductId AND r.UserId=@uid) AS reviewed
                FROM OrderItems oi WHERE oi.OrderId=@id ORDER BY oi.OrderItemId", new { id, uid = Uid(u) }));
        });

        // ---------- customer: ask for a refund ----------
        shop.MapPost("/orders/{id:int}/refund", async (int id, RefundReq r, ClaimsPrincipal u, Db db) =>
        {
            var reason = (r.Reason ?? "").Trim(); if (reason.Length < 5 || reason.Length > 300) return Bad("Tell us why in 5 to 300 characters.");
            using var c = db.Open(); using var tx = c.BeginTransaction();
            var o = await c.QueryFirstOrDefaultAsync<RefundOrder>("SELECT OrderId, Status, PaymentStatus, RefundStatus, Total, CreatedAt, PaidAt, CompletedAt FROM Orders WHERE OrderId=@id AND UserId=@uid FOR UPDATE", new { id, uid = Uid(u) }, tx);
            if (o is null) return Results.NotFound(new { error = "Not found." });
            if (o.PaymentStatus != "Paid") return Conflict("Only paid orders can be refunded. An unpaid order can simply be cancelled.");
            if (o.Status == "Cancelled") return Conflict("This order was already cancelled.");
            if (o.RefundStatus != "None") return Conflict(o.RefundStatus == "Refunded" ? "This order was already refunded." : o.RefundStatus == "Requested" ? "You already asked for a refund. The store will answer soon." : "Your refund request was declined. Please message the store.");
            var days = await Cfg.IntAsync(c, "RefundWindowDays", 0, 90);
            if (o.Status == "Completed" && o.CompletedAt != null && (Promos.Now - o.CompletedAt.Value).TotalDays > days) return Conflict($"Refunds can be asked within {days} days after the order is completed.");
            await c.ExecuteAsync("UPDATE Orders SET RefundReason=@reason, RefundAmount=Total, RefundRequestedAt=NOW(), RefundStatus='Requested' WHERE OrderId=@id", new { reason, id }, tx);
            await Audit.AddAsync(c, u.Identity?.Name, "Refund requested", "Order " + id, tx);
            tx.Commit(); return Results.Ok(new { ok = true });
        });

        // ---------- admin: confirm the items first, then the money ----------
        admin.MapPost("/transactions/{id:int}/confirm-items", async (int id, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open(); using var tx = c.BeginTransaction();
            var o = await c.QueryFirstOrDefaultAsync<ConfirmRow>("SELECT Status, PaymentStatus, ItemsConfirmedAt, PaymentRef, PaymentMethod FROM Orders WHERE OrderId=@id FOR UPDATE", new { id }, tx);
            if (o is null) return Results.NotFound(new { error = "Not found." });
            if (o.Status is "Cancelled" or "Completed") return Conflict("This order is already finished.");
            if (o.ItemsConfirmedAt != null) return Conflict("The items were already confirmed.");
            await c.ExecuteAsync("UPDATE Orders SET ItemsConfirmedAt=NOW(), ItemsConfirmedBy=@by, Status=IF(Status='Pending','Preparing',Status) WHERE OrderId=@id", new { by = u.Identity?.Name, id }, tx);
            await Audit.AddAsync(c, u.Identity?.Name, "Items confirmed", "Order " + id, tx);
            tx.Commit(); return Results.Ok(new { ok = true });
        });
        admin.MapPost("/transactions/{id:int}/confirm-payment", async (int id, PayConfirmReq r, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open(); using var tx = c.BeginTransaction();
            var o = await c.QueryFirstOrDefaultAsync<ConfirmRow>("SELECT Status, PaymentStatus, ItemsConfirmedAt, PaymentRef, PaymentMethod FROM Orders WHERE OrderId=@id FOR UPDATE", new { id }, tx);
            if (o is null) return Results.NotFound(new { error = "Not found." });
            if (o.Status == "Cancelled") return Conflict("This order was cancelled.");
            if (o.PaymentStatus == "Paid") return Conflict("The payment was already recorded.");
            if (o.ItemsConfirmedAt == null) return Conflict("Confirm the items first. Money is recorded only after the items are checked.");
            if (o.PaymentRef != null && (o.PaymentRef.StartsWith("cs_") || o.PaymentRef.StartsWith("pp_") || o.PaymentRef.StartsWith("test_"))) return Conflict("This is an online payment. It is recorded automatically when the payment provider confirms it.");
            await c.ExecuteAsync("UPDATE Orders SET PaymentStatus='Paid', PaidAt=NOW(), PaymentConfirmedBy=@by WHERE OrderId=@id", new { by = u.Identity?.Name, id }, tx);
            await Audit.AddAsync(c, u.Identity?.Name, "Payment confirmed", "Order " + id, tx);
            tx.Commit(); return Results.Ok(new { ok = true });
        });

        // ---------- admin: answer a refund request ----------
        admin.MapPost("/transactions/{id:int}/refund-decision", async (int id, RefundDecisionReq r, ClaimsPrincipal u, Db db, EmailService emailer) =>
        {
            using var c = db.Open(); using var tx = c.BeginTransaction();
            var o = await c.QueryFirstOrDefaultAsync<RefundOrder>(@"SELECT o.OrderId, o.Status, o.PaymentStatus, o.RefundStatus, o.Total, o.CustomerName, u.Email, IFNULL(u.EmailNotify,1) AS EmailNotify,
                CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS Code FROM Orders o LEFT JOIN Users u ON u.UserId=o.UserId WHERE o.OrderId=@id FOR UPDATE", new { id }, tx);
            if (o is null) return Results.NotFound(new { error = "Not found." });
            if (o.RefundStatus != "Requested") return Conflict("There is no open refund request for this order.");
            if (r.Approve && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE OrderId=@id AND LalamoveStatus IN ('BOOKING','ASSIGNING_DRIVER','ON_GOING','PICKED_UP','MANUAL')", new { id }, tx) > 0)
                return Conflict("A Lalamove rider is booked for this order. Cancel the booking first (Delivery button on the order), then approve the refund.");
            var note = Nz(r.Note); if (note != null && note.Length > 300) note = note[..300];
            if (!r.Approve)
            {
                await c.ExecuteAsync("UPDATE Orders SET RefundStatus='Rejected', RefundNote=@note WHERE OrderId=@id", new { note, id }, tx);
            }
            else
            {
                // an order that is still open is cancelled and its items go back to stock; a completed one goes back only when the goods were returned
                if (o.Status is not ("Cancelled" or "Completed")) { await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name); await c.ExecuteAsync("UPDATE Orders SET Status='Cancelled', CancelReason='Refunded' WHERE OrderId=@id", new { id }, tx); }
                else if (o.Status == "Completed" && r.Restock) await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name);
                await c.ExecuteAsync("UPDATE Orders SET RefundedAt=NOW(), RefundNote=@note, RefundAmount=IFNULL(RefundAmount,Total), RefundStatus='Refunded' WHERE OrderId=@id", new { note, id }, tx);
            }
            await Audit.AddAsync(c, u.Identity?.Name, r.Approve ? "Refund approved" : "Refund declined", "Order " + id, tx);
            tx.Commit();
            if (o.Email != null && o.EmailNotify) { try { await emailer.SendOrderStatusEmailAsync(o.Email, o.CustomerName ?? "", o.Code ?? "", r.Approve ? "Refunded" : "Refund declined"); } catch { } }
            return Results.Ok(new { ok = true });
        });

        // ---------- admin: scanning ----------
        admin.MapGet("/products/by-code/{code}", async (string code, Db db) =>
        {
            code = (code ?? "").Trim(); if (code.Length is 0 or > 40) return Bad("Scan a product label.");
            using var c = db.Open();
            var p = await c.QueryFirstOrDefaultAsync("SELECT ProductId AS id, Name AS name, Price AS price, Stock AS stock, Sizes AS sizes, ImagePath AS image, Sku AS sku, ItemType AS itemType FROM Products WHERE Sku=@code", new { code });
            return p is null ? Results.NotFound(new { error = "No product has the code " + code + "." }) : Results.Ok(p);
        });
        admin.MapGet("/orders/by-code/{code}", async (string code, Db db) =>
        {
            var m = Regex.Match((code ?? "").Trim().ToUpperInvariant(), @"^(ORD|WLK)-(\d{1,9})$"); if (!m.Success) return Bad("That is not an order code.");
            using var c = db.Open(); var id = int.Parse(m.Groups[2].Value);
            var o = await c.QueryFirstOrDefaultAsync("SELECT OrderId AS id, CustomerName AS customer, Total AS total, Status AS status, PaymentStatus AS paymentStatus, ItemsConfirmedAt AS itemsConfirmedAt, Channel AS channel FROM Orders WHERE OrderId=@id", new { id });
            return o is null ? Results.NotFound(new { error = "No order has that code." }) : Results.Ok(o);
        });

        // ---------- admin: pop-up alerts for new online orders ----------
        // Every admin page asks this every few seconds. With after=0 it only returns the newest order number (the starting point, so old orders are never announced).
        // With after=N it returns the online orders placed since N (at most 10 shown, plus the true count) so the page can pop up a card for each one.
        admin.MapGet("/orders/new-since", async (int? after, Db db) =>
        {
            using var c = db.Open();
            var maxId = await c.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(OrderId),0) FROM Orders");
            if (after is null or <= 0 || after >= maxId) return Results.Ok(new { maxId, count = 0, orders = Array.Empty<object>() });
            var count = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE OrderId>@after AND Channel='Online'", new { after });
            var rows = await c.QueryAsync("SELECT o.OrderId AS id, " + Sql.Code + " AS code, o.CustomerName AS customer, o.Total AS total, o.PaymentMethod AS method, o.FulfillmentType AS fulfillment, IFNULL((SELECT SUM(i.Qty) FROM OrderItems i WHERE i.OrderId=o.OrderId),0) AS items FROM Orders o WHERE o.OrderId>@after AND o.Channel='Online' ORDER BY o.OrderId LIMIT 10", new { after });
            return Results.Ok(new { maxId, count, orders = rows });
        });

        // ---------- admin: dropdown lists ----------
        string[] kinds = { "ItemType", "SizeSet", "Color", "BikeType" };
        admin.MapGet("/lookups", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT LookupId AS id, Kind AS kind, Value AS value, Extra AS extra FROM Lookups ORDER BY Kind, SortOrder, Value")); });
        admin.MapPost("/lookups", async (LookupReq r, ClaimsPrincipal u, Db db) =>
        {
            var kind = (r.Kind ?? "").Trim(); var value = (r.Value ?? "").Trim(); var extra = Nz(r.Extra);
            if (!kinds.Contains(kind)) return Bad("Unknown list."); if (value.Length < 1 || value.Length > 80) return Bad("Enter 1 to 80 characters.");
            if (kind == "SizeSet") { extra = Sizing.Normalize(extra); if (extra is null) return Bad("Enter the sizes, separated by commas."); }
            else extra = null;
            using var c = db.Open();
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Lookups WHERE Kind=@kind AND LOWER(Value)=LOWER(@value)", new { kind, value }) > 0) return Conflict("That entry already exists.");
            await c.ExecuteAsync("INSERT INTO Lookups(Kind,Value,Extra,SortOrder) VALUES(@kind,@value,@extra,(SELECT IFNULL(MAX(SortOrder),0)+1 FROM (SELECT SortOrder FROM Lookups WHERE Kind=@kind) t))", new { kind, value, extra });
            await Audit.AddAsync(c, u.Identity?.Name, "List entry added", kind + ": " + value);
            return Results.Ok(new { ok = true });
        });
        admin.MapDelete("/lookups/{id:int}", async (int id, ClaimsPrincipal u, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Lookups WHERE LookupId=@id", new { id }); await Audit.AddAsync(c, u.Identity?.Name, "List entry removed", "#" + id); return Results.Ok(new { ok = true }); });

        // ---------- admin: seasons ----------
        bool MdOk(string kind, string? v) => kind switch
        {
            "yearly" => Regex.IsMatch(v ?? "", @"^(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$"),
            "monthly" => int.TryParse(v, out var d) && d is >= 1 and <= 31,
            "easter" => int.TryParse(v, out var o) && o is >= -60 and <= 60,
            _ => false
        };
        admin.MapGet("/seasons", async (Db db) =>
        {
            using var c = db.Open(); var states = await Seasons.StatesAsync(c);
            return Results.Ok(states.Select(s => new { name = s.Name, kind = s.Kind, startMD = s.StartMD, endMD = s.EndMD, isOn = s.IsOn, live = s.Live, start = s.Start.ToString("yyyy-MM-dd"), end = s.End.ToString("yyyy-MM-dd") }));
        });
        admin.MapPost("/seasons", async (SeasonNewReq r, ClaimsPrincipal u, Db db) =>
        {
            var name = (r.Name ?? "").Trim(); var kind = r.Kind is "monthly" or "easter" ? r.Kind : "yearly";
            if (name.Length < 2 || name.Length > 30) return Bad("The season name must be 2 to 30 characters.");
            if (!MdOk(kind, r.StartMD) || !MdOk(kind, r.EndMD)) return Bad(kind == "yearly" ? "Dates must look like 12-25 (month-day)." : kind == "monthly" ? "Use a day of the month, 1 to 31." : "Use day offsets from Easter, like -7 and 0.");
            using var c = db.Open();
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Seasons WHERE LOWER(Name)=LOWER(@name)", new { name }) > 0) return Conflict("That season already exists.");
            await c.ExecuteAsync("INSERT INTO Seasons(Name,Kind,StartMD,EndMD) VALUES(@name,@kind,@s,@e)", new { name, kind, s = r.StartMD, e = r.EndMD });
            await Audit.AddAsync(c, u.Identity?.Name, "Season added", name); return Results.Ok(new { ok = true });
        });
        admin.MapPut("/seasons/{name}", async (string name, SeasonEditReq r, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open(); var kind = await c.ExecuteScalarAsync<string>("SELECT Kind FROM Seasons WHERE Name=@name", new { name });
            if (kind is null) return Results.NotFound(new { error = "Not found." });
            if (r.StartMD != null && !MdOk(kind, r.StartMD)) return Bad("That start date is not valid."); if (r.EndMD != null && !MdOk(kind, r.EndMD)) return Bad("That end date is not valid.");
            await c.ExecuteAsync("UPDATE Seasons SET IsOn=IFNULL(@on,IsOn), StartMD=IFNULL(@s,StartMD), EndMD=IFNULL(@e,EndMD) WHERE Name=@name", new { name, on = r.IsOn, s = r.StartMD, e = r.EndMD });
            await Audit.AddAsync(c, u.Identity?.Name, "Season changed", name); return Results.Ok(new { ok = true });
        });
        admin.MapDelete("/seasons/{name}", async (string name, ClaimsPrincipal u, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Seasons WHERE Name=@name", new { name }); await Audit.AddAsync(c, u.Identity?.Name, "Season removed", name); return Results.Ok(new { ok = true }); });

        // ---------- admin: store rules (nothing here is fixed in the program) ----------
        var rules = new Dictionary<string, (decimal min, decimal max)> { ["LowStockThreshold"] = (0, 1000), ["MaxFeatured"] = (1, 50), ["AdminIdleSeconds"] = (30, 3600), ["UnpaidOnlineHours"] = (1, 168), ["PickupHoldDays"] = (1, 30), ["RefundWindowDays"] = (0, 90), ["DailyExpenses"] = (0, 10_000_000), ["DefaultMarginPct"] = (1, 90), ["PromoPopup"] = (0, 1) };
        admin.MapGet("/rules", async (Db db) => { using var c = db.Open(); var d = new Dictionary<string, string>(); foreach (var k in rules.Keys) d[k] = await Cfg.GetAsync(c, k); return Results.Ok(d); });
        admin.MapPut("/rules", async (Dictionary<string, string?> r, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open();
            foreach (var (k, v) in r)
            {
                if (!rules.TryGetValue(k, out var lim)) continue;
                if (!decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < lim.min || n > lim.max) return Bad($"{k}: enter a number from {lim.min} to {lim.max}.");
                await c.ExecuteAsync("INSERT INTO Settings(SettingKey,SettingValue) VALUES(@k,@v) ON DUPLICATE KEY UPDATE SettingValue=@v", new { k, v = n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            Cfg.Clear(); await Audit.AddAsync(c, u.Identity?.Name, "Store rules changed", string.Join(", ", r.Keys.Where(rules.ContainsKey))); return Results.Ok(new { ok = true });
        });

        admin.MapGet("/audit", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT AuditId AS id, ByUser AS byUser, Action AS action, Detail AS detail, CreatedAt AS date FROM AuditLog ORDER BY AuditId DESC LIMIT 200")); });

        // ---------- admin: the last 14 days, today, and the expected earnings (or loss) for the next 7 days ----------
        admin.MapGet("/trend", async (Db db) =>
        {
            using var c = db.Open(); var today = Promos.Today; var from = today.AddDays(-27);
            var rows = (await c.QueryAsync<DayAmount>(@"SELECT DATE(" + Sql.RevDate + @") AS Day, SUM(o.Total) AS Sales,
                IFNULL(SUM((SELECT SUM(IFNULL(oi.UnitCost,0)*oi.Qty) FROM OrderItems oi WHERE oi.OrderId=o.OrderId AND oi.UnitCost IS NOT NULL)),0) AS Cost,
                IFNULL(SUM((SELECT SUM(oi.UnitPrice*oi.Qty) FROM OrderItems oi WHERE oi.OrderId=o.OrderId AND oi.UnitCost IS NOT NULL)),0) AS ItemRev
                FROM Orders o WHERE " + Sql.Rev + " AND " + Sql.RevDate + @" >= @from GROUP BY DATE(" + Sql.RevDate + ")", new { from })).ToDictionary(x => x.Day.Date);
            var expenses = await Cfg.DecAsync(c, "DailyExpenses", 0, 10_000_000); var defMargin = await Cfg.DecAsync(c, "DefaultMarginPct", 1, 90) / 100m;
            // the margin comes from the real cost prices when they are filled in, otherwise from the store's default margin
            var costItemRev = rows.Values.Sum(x => x.ItemRev); var cost = rows.Values.Sum(x => x.Cost);
            var margin = costItemRev > 0 ? Math.Clamp(1m - cost / costItemRev, 0m, 0.95m) : defMargin; var realMargin = costItemRev > 0;
            decimal S(DateTime d) => rows.TryGetValue(d.Date, out var v) ? v.Sales : 0m;
            var hist = Enumerable.Range(0, 28).Select(i => (day: from.AddDays(i), sales: S(from.AddDays(i)))).ToList();
            var last14 = hist.Skip(14).ToList(); var avg14 = last14.Sum(h => h.sales) / 14m; var prev14 = hist.Take(14).Sum(h => h.sales);
            var overall = hist.Sum(h => h.sales) / 28m; var ratio = prev14 > 0 ? (double)(last14.Sum(h => h.sales) / prev14) : 1.0; var factor = (decimal)(1 + (Math.Clamp(ratio, 0.7, 1.3) - 1) / 2);
            var dow = new decimal[7]; for (var d = 0; d < 7; d++) { var xs = hist.Where(h => (int)h.day.DayOfWeek == d).ToList(); dow[d] = xs.Count == 0 ? overall : xs.Sum(h => h.sales) / xs.Count; }
            decimal Pred(DateTime d) => Math.Round((dow[(int)d.DayOfWeek] * 0.5m + overall * 0.5m) * factor, 2);
            var days = last14.Select(h => new { label = h.day.ToString("yyyy-MM-dd"), sales = h.sales, profit = Math.Round(h.sales * margin - expenses, 2) }).ToList();
            var next = Enumerable.Range(1, 7).Select(i => { var d = today.AddDays(i); var s = Pred(d); return new { label = d.ToString("yyyy-MM-dd"), sales = s, profit = Math.Round(s * margin - expenses, 2) }; }).ToList();
            var next7 = next.Sum(x => x.sales); var next7Profit = next.Sum(x => x.profit); var todaySales = S(today); var daysWithSales = hist.Count(h => h.sales > 0);
            var verdict = next7Profit > 0 ? "earnings" : next7Profit < 0 ? "loss" : "break-even";
            return Results.Ok(new { days, todaySales, avg14 = Math.Round(avg14, 2), todayVsAvgPct = avg14 > 0 ? Math.Round((todaySales - avg14) / avg14 * 100m) : (decimal?)null, marginPct = Math.Round(margin * 100m), marginFromCosts = realMargin, expenses, forecast = next, next7, next7Profit, verdict, trendPct = Math.Round((ratio - 1) * 100), confidence = daysWithSales < 7 ? "low" : daysWithSales < 18 ? "fair" : "good" });
        });
    }
}
