using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using MySqlConnector;

// ---------------------------------------------------------------------------------------------
// Shared building blocks: store rules from the database, encryption, login protection, seasons,
// audit log and the background job that returns unclaimed orders to stock.
// (Everything here used to be a typed-in number or list inside the program.)
// ---------------------------------------------------------------------------------------------

// SQL snippets that must mean the same thing everywhere (reports, dashboard, insights).
static class Sql
{
    // Money rule: a sale counts only when the store confirmed the items first AND received the payment, and the order was not cancelled or refunded.
    public const string Rev = "o.PaymentStatus='Paid' AND o.ItemsConfirmedAt IS NOT NULL AND o.Status<>'Cancelled' AND o.RefundStatus<>'Refunded'";
    // Demand: units customers asked for (used to rank fast and slow moving items).
    public const string Demand = "o.Status<>'Cancelled' AND o.RefundStatus<>'Refunded'";
    // The day a sale is booked is the day the money was received.
    public const string RevDate = "IFNULL(o.PaidAt,o.CreatedAt)";
    public const string Code = "CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0'))";
}

// Store rules saved in the Settings table (Admin > Settings). A short cache keeps the database from being asked on every click.
static class Cfg
{
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["StoreName"] = "Web-Syscle", ["StoreTagline"] = "Shop & Customize", ["DeliveryFee"] = "100", ["LowStockThreshold"] = "5", ["MaxFeatured"] = "12",
        ["AdminIdleSeconds"] = "300", ["UnpaidOnlineHours"] = "2", ["PickupHoldDays"] = "2", ["RefundWindowDays"] = "7",
        ["DailyExpenses"] = "0", ["DefaultMarginPct"] = "30", ["PromoPopup"] = "1",
        ["DeliveryMaxKm"] = "8", ["DeliveryBaseFee"] = "50", ["DeliveryPerKm"] = "12", ["LalamoveService"] = "MOTORCYCLE", ["AutoPrintReceipt"] = "1", ["CodMax"] = "5000",
        ["ReceiptFooter"] = "Thank you for shopping with us! Please keep this receipt for returns and warranty."
    };
    static readonly ConcurrentDictionary<string, (string v, DateTime at)> Cache = new();
    public static void Clear() => Cache.Clear();
    public static async Task<string> GetAsync(MySqlConnection c, string key)
    {
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromSeconds(20)) return hit.v;
        var v = await c.ExecuteScalarAsync<string>("SELECT SettingValue FROM Settings WHERE SettingKey=@key", new { key });
        if (string.IsNullOrWhiteSpace(v)) v = Defaults.TryGetValue(key, out var d) ? d : "";
        Cache[key] = (v, DateTime.UtcNow); return v;
    }
    public static async Task<int> IntAsync(MySqlConnection c, string key, int min, int max)
    {
        var s = await GetAsync(c, key);
        if (!int.TryParse(s, out var n)) int.TryParse(Defaults.TryGetValue(key, out var d) ? d : "0", out n);
        return Math.Clamp(n, min, max);
    }
    public static async Task<decimal> DecAsync(MySqlConnection c, string key, decimal min, decimal max)
    {
        var s = await GetAsync(c, key);
        if (!decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)) decimal.TryParse(Defaults.TryGetValue(key, out var d) ? d : "0", out n);
        return Math.Clamp(n, min, max);
    }
}

// Encrypts small secrets (the 2FA secret) before they are written to the database.
static class SecretBox
{
    static byte[]? _key;
    public static void Init(string material) { _key = SHA256.HashData(Encoding.UTF8.GetBytes(material)); }
    public static string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var data = Encoding.UTF8.GetBytes(plain); var cipher = new byte[data.Length]; var tag = new byte[16];
        using var aes = new AesGcm(_key!, 16); aes.Encrypt(nonce, data, cipher, tag);
        return "enc1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    // Rows saved before encryption existed come back unchanged.
    public static string Reveal(string stored)
    {
        if (!stored.StartsWith("enc1:")) return stored;
        var raw = Convert.FromBase64String(stored.Substring(5)); var nonce = raw.Take(12).ToArray(); var tag = raw.Skip(12).Take(16).ToArray(); var cipher = raw.Skip(28).ToArray(); var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key!, 16); aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

class SecretRow { public int Id { get; set; } public string S { get; set; } = ""; }

static class Security
{
    public static async Task InitAsync(MySqlConnection c, IConfiguration cfg)
    {
        var key = cfg["Security:DataKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Contains("PASTE"))
        {
            key = await c.ExecuteScalarAsync<string>("SELECT SettingValue FROM Settings WHERE SettingKey='DataKey'");
            if (string.IsNullOrWhiteSpace(key))
            {
                key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await c.ExecuteAsync("INSERT INTO Settings(SettingKey,SettingValue) VALUES('DataKey',@key) ON DUPLICATE KEY UPDATE SettingValue=@key", new { key });
            }
            Console.WriteLine("[Syscle] Security:DataKey is not set, so a key was generated and kept in the database. For stronger protection set the Security__DataKey environment variable.");
        }
        SecretBox.Init(key!);
        // 2FA secrets saved in plain text by older versions are encrypted now
        var rows = (await c.QueryAsync<SecretRow>("SELECT UserId AS Id, TwoFactorSecret AS S FROM Users WHERE TwoFactorSecret IS NOT NULL AND TwoFactorSecret NOT LIKE 'enc1:%'")).ToList();
        foreach (var r in rows) await c.ExecuteAsync("UPDATE Users SET TwoFactorSecret=@s WHERE UserId=@id", new { s = SecretBox.Protect(r.S), id = r.Id });
    }
}

// The keys that sign login cookies and reset links live in the database, not in text files on the disk.
class DbXmlRepository : IXmlRepository
{
    readonly Db _db;
    public DbXmlRepository(Db db) { _db = db; }
    public IReadOnlyCollection<System.Xml.Linq.XElement> GetAllElements()
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT Xml FROM DataProtectionKeys ORDER BY KeyId").Select(x => System.Xml.Linq.XElement.Parse(x)).ToList();
    }
    public void StoreElement(System.Xml.Linq.XElement element, string friendlyName)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO DataProtectionKeys(FriendlyName,Xml) VALUES(@f,@x)", new { f = friendlyName, x = element.ToString(System.Xml.Linq.SaveOptions.DisableFormatting) });
    }
}

// Failed sign-ins are counted in the database per account and per address, so a restart or a faked header cannot reset the count.
static class LoginGuard
{
    public static async Task<bool> IsBlockedAsync(MySqlConnection c, string key, int max, int minutes) =>
        await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LoginAttempts WHERE AttemptKey=@key AND AtTime > DATE_SUB(NOW(), INTERVAL @minutes MINUTE)", new { key, minutes }) >= max;
    public static Task FailAsync(MySqlConnection c, string key) => c.ExecuteAsync("INSERT INTO LoginAttempts(AttemptKey) VALUES(@key)", new { key });
    public static Task ClearAsync(MySqlConnection c, string key) => c.ExecuteAsync("DELETE FROM LoginAttempts WHERE AttemptKey=@key", new { key });
    public static Task CleanAsync(MySqlConnection c) => c.ExecuteAsync("DELETE FROM LoginAttempts WHERE AtTime < DATE_SUB(NOW(), INTERVAL 1 DAY)");
}

class TicketRow { public int UserId { get; set; } public string? CodeHash { get; set; } public int Attempts { get; set; } }

// One-time tickets (2FA step, SMS password reset). Only a hash is stored; each ticket allows 5 wrong codes and then dies.
static class Tickets
{
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    public static async Task<string> IssueAsync(MySqlConnection c, int userId, string purpose, int minutes, string? code = null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await c.ExecuteAsync("DELETE FROM AuthTickets WHERE (UserId=@userId AND Purpose=@purpose) OR ExpiresAt < NOW()", new { userId, purpose });
        await c.ExecuteAsync("INSERT INTO AuthTickets(TicketHash,UserId,Purpose,CodeHash,ExpiresAt) VALUES(@h,@userId,@purpose,@ch,DATE_ADD(NOW(), INTERVAL @minutes MINUTE))", new { h = Hash(token), userId, purpose, ch = code == null ? null : Hash(code), minutes });
        return token;
    }
    public static async Task<TicketRow?> FindAsync(MySqlConnection c, string? token, string purpose) =>
        await c.QueryFirstOrDefaultAsync<TicketRow>("SELECT UserId, CodeHash, Attempts FROM AuthTickets WHERE TicketHash=@h AND Purpose=@purpose AND ExpiresAt > NOW() AND Attempts < 5", new { h = Hash(token ?? ""), purpose });
    public static Task WrongAsync(MySqlConnection c, string? token) => c.ExecuteAsync("UPDATE AuthTickets SET Attempts = Attempts + 1 WHERE TicketHash=@h", new { h = Hash(token ?? "") });
    public static Task BurnAsync(MySqlConnection c, string? token) => c.ExecuteAsync("DELETE FROM AuthTickets WHERE TicketHash=@h", new { h = Hash(token ?? "") });
}

// A line in the admin audit log (who changed what).
static class Audit
{
    public static Task AddAsync(MySqlConnection c, string? by, string action, string? detail, MySqlTransaction? tx = null) =>
        c.ExecuteAsync("INSERT INTO AuditLog(ByUser,Action,Detail) VALUES(@by,@action,@detail)", new { by, action, detail = detail != null && detail.Length > 300 ? detail.Substring(0, 300) : detail }, tx);
}

// ---------- seasons: seasonal codes and bundles switch on and off by themselves every year ----------
class SeasonRow { public string Name { get; set; } = ""; public string Kind { get; set; } = "yearly"; public string StartMD { get; set; } = ""; public string EndMD { get; set; } = ""; public bool IsOn { get; set; } }
record SeasonState(string Name, string Kind, string StartMD, string EndMD, bool IsOn, bool Live, DateTime Start, DateTime End);

static class Seasons
{
    // Easter Sunday of a year (the Gregorian "computus"); Holy Week is counted from it.
    public static DateTime Easter(int y)
    {
        var a = y % 19; var b = y / 100; var cc = y % 100; var d = b / 4; var e = b % 4; var f = (b + 8) / 25; var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30; var i = cc / 4; var k = cc % 4; var l = (32 + 2 * e + 2 * i - h - k) % 7; var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31; var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, day);
    }
    static DateTime Clamp(int y, int m, int d) => new DateTime(y, m, Math.Min(d, DateTime.DaysInMonth(y, m)));
    static bool Md(string? s, out int m, out int d)
    {
        m = 0; d = 0; var p = (s ?? "").Split('-');
        return p.Length == 2 && int.TryParse(p[0], out m) && int.TryParse(p[1], out d) && m >= 1 && m <= 12 && d >= 1 && d <= 31;
    }
    // The first window of this season that has not finished yet (so an out-of-season code can say "starts on ...").
    public static (DateTime start, DateTime end)? NextWindow(SeasonRow s, DateTime today)
    {
        var list = new List<(DateTime start, DateTime end)>();
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            if (s.Kind == "easter")
            {
                if (!int.TryParse(s.StartMD, out var offA) || !int.TryParse(s.EndMD, out var offB)) return null;
                var easter = Easter(y); list.Add((easter.AddDays(offA), easter.AddDays(offB)));
            }
            else if (s.Kind == "monthly")
            {
                if (!int.TryParse(s.StartMD, out var dayA) || !int.TryParse(s.EndMD, out var dayB) || dayA < 1 || dayB < 1) return null;
                for (var mo = 1; mo <= 12; mo++)
                {
                    var st = Clamp(y, mo, dayA);
                    var en = dayB >= dayA ? Clamp(y, mo, dayB) : Clamp(mo == 12 ? y + 1 : y, mo == 12 ? 1 : mo + 1, dayB);
                    list.Add((st, en));
                }
            }
            else
            {
                if (!Md(s.StartMD, out var sm, out var sd) || !Md(s.EndMD, out var em, out var ed)) return null;
                var wrap = em < sm || (em == sm && ed < sd);
                list.Add((Clamp(y, sm, sd), Clamp(wrap ? y + 1 : y, em, ed)));
            }
        }
        foreach (var w in list.OrderBy(x => x.start)) if (w.end >= today) return w;
        return null;
    }
    public static async Task<List<SeasonState>> StatesAsync(MySqlConnection c, MySqlTransaction? tx = null)
    {
        var rows = await c.QueryAsync<SeasonRow>("SELECT Name, Kind, StartMD, EndMD, IsOn FROM Seasons ORDER BY Name", null, tx);
        var today = Promos.Today; var list = new List<SeasonState>();
        foreach (var s in rows)
        {
            var w = NextWindow(s, today); var start = w.HasValue ? w.Value.start : today; var end = w.HasValue ? w.Value.end : today;
            list.Add(new SeasonState(s.Name, s.Kind, s.StartMD, s.EndMD, s.IsOn, s.IsOn && w.HasValue && start <= today && today <= end, start, end));
        }
        return list;
    }
    // A season name that is no longer in the list does not block old codes; a known season blocks them outside its dates.
    public static (bool live, string? why) Check(List<SeasonState> states, string? season)
    {
        if (string.IsNullOrWhiteSpace(season)) return (true, null);
        var s = states.FirstOrDefault(x => string.Equals(x.Name, season, StringComparison.OrdinalIgnoreCase));
        if (s is null || s.Live) return (true, null);
        return (false, s.IsOn ? "This code is only for the " + s.Name + " season (starts " + s.Start.ToString("MMM d") + ")." : "The " + s.Name + " season is switched off.");
    }
    public static bool Exists(List<SeasonState> states, string name) => states.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
}

// ---------- background job: unclaimed orders go back to stock by themselves ----------
class ExpireRow { public int OrderId { get; set; } public string Code { get; set; } = ""; public string? CustomerName { get; set; } public string? Email { get; set; } public bool EmailNotify { get; set; } = true; public string Why { get; set; } = ""; }

class OrderExpiryService : BackgroundService
{
    readonly Db _db; readonly EmailService _mail; readonly LalamoveService _lala;
    public OrderExpiryService(Db db, EmailService mail, LalamoveService lala) { _db = db; _mail = mail; _lala = lala; }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await RunOnceAsync(); } catch (Exception ex) { Console.WriteLine("[Syscle] Order expiry check failed: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (OperationCanceledException) { break; }
        }
    }
    // Rule 1: an online payment (PayMongo / PayPal) that is not finished in a few hours cancels the order.
    // Rule 2: an order for pick-up that is not collected a few days after the chosen pick-up date is cancelled.
    // Both rules give the items back to stock and write it in the stock movement log. Hours and days are store rules in Settings.
    public async Task RunOnceAsync()
    {
        using var c = _db.Open();
        await LoginGuard.CleanAsync(c);
        // booked Lalamove riders: keep the order status in step even when nobody has the order page open
        foreach (var oid in (await c.QueryAsync<int>("SELECT OrderId FROM Orders WHERE LalamoveStatus IN ('ASSIGNING_DRIVER','ON_GOING','PICKED_UP') LIMIT 50")).ToList())
        { try { await DeliveryEndpoints.RefreshAsync(c, _lala, oid, true); } catch (Exception ex) { Console.WriteLine("[Syscle] Lalamove refresh failed: " + ex.Message); } }
        var unpaidHours = await Cfg.IntAsync(c, "UnpaidOnlineHours", 1, 720); var holdDays = await Cfg.IntAsync(c, "PickupHoldDays", 0, 90);
        var cut = Promos.Today.AddDays(-holdDays);
        const string head = "SELECT o.OrderId, CONCAT('ORD-', LPAD(o.OrderId,6,'0')) AS Code, o.CustomerName, u.Email, IFNULL(u.EmailNotify,1) AS EmailNotify, ";
        const string from = " FROM Orders o LEFT JOIN Users u ON u.UserId=o.UserId WHERE o.Channel='Online' AND o.Status IN ('Pending','Preparing','Ready') AND ";
        var due = (await c.QueryAsync<ExpireRow>(
            head + "'unpaid' AS Why" + from + "o.PaymentStatus<>'Paid' AND o.PaymentMethod IN (SELECT Label FROM PaymentMethods WHERE MethodCode IN ('paymongo','paypal')) AND o.CreatedAt < DATE_SUB(NOW(), INTERVAL @unpaidHours HOUR) " +
            "UNION ALL " +
            head + "'pickup' AS Why" + from + "o.FulfillmentType='Pickup' AND o.PickupDate IS NOT NULL AND o.PickupDate < @cut", new { unpaidHours, cut })).ToList();
        foreach (var r in due.GroupBy(x => x.OrderId).Select(g => g.First()))
        {
            var reason = r.Why == "unpaid" ? "Auto-cancelled: the online payment was not completed in time" : "Auto-cancelled: not picked up in time";
            using (var tx = c.BeginTransaction())
            {
                var st = await c.QueryFirstOrDefaultAsync<string>("SELECT Status FROM Orders WHERE OrderId=@id FOR UPDATE", new { id = r.OrderId }, tx);
                if (st != "Pending" && st != "Preparing" && st != "Ready") { tx.Rollback(); continue; }
                await StockLog.ReturnOrder(c, tx, r.OrderId, "system");
                // an order that was already paid is flagged for a refund (RefundStatus is set last on purpose: MySQL reads the new value in the next assignment)
                await c.ExecuteAsync("UPDATE Orders SET Status='Cancelled', CancelReason=@reason, RefundReason=IF(PaymentStatus='Paid' AND RefundStatus='None',@reason,RefundReason), RefundRequestedAt=IF(PaymentStatus='Paid' AND RefundStatus='None',NOW(),RefundRequestedAt), RefundStatus=IF(PaymentStatus='Paid' AND RefundStatus='None','Requested',RefundStatus) WHERE OrderId=@id", new { reason, id = r.OrderId }, tx);
                await Audit.AddAsync(c, "system", "auto-cancel", r.Code + ": " + reason, tx);
                tx.Commit();
            }
            if (r.EmailNotify && !string.IsNullOrEmpty(r.Email))
            {
                try { await _mail.SendEmailAsync(r.Email, "Order " + r.Code + " was cancelled", "<p>Hello " + System.Net.WebUtility.HtmlEncode(r.CustomerName ?? "") + ",</p><p>Order <b>" + r.Code + "</b> was cancelled automatically (" + reason.Replace("Auto-cancelled: ", "") + "). The items went back to stock. If you already paid, please open My orders and request a refund, or message the store.</p>"); } catch { }
            }
            Console.WriteLine("[Syscle] " + r.Code + " " + reason);
        }
    }
}

// ---------- two separate sign-ins: the shop (customers) and the admin portal ----------
// Each portal has its own cookie, so an admin and a customer can be signed in at the same time in one browser
// without one replacing the other. Which cookie a request uses depends on the page or API it asks for.
static class Schemes
{
    public const string Customer = "syscle.auth", Admin = "syscle.admin";
    public static string Pick(HttpContext ctx)
    {
        var p = (ctx.Request.Path.Value ?? "").ToLowerInvariant();
        if (p.StartsWith("/api/admin") || p.StartsWith("/pages/admin") || p.StartsWith("/javascripts/admin")) return Admin;
        return ctx.Request.Headers["X-Area"].ToString() == "admin" ? Admin : Customer;
    }
    public static void Cookie(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions o, string name)
    {
        o.Cookie.Name = name;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { if (c.Request.Path.StartsWithSegments("/api")) { c.Response.StatusCode = 401; if (c.HttpContext.Items["sy-reason"] is string why) c.Response.Headers["X-Session-Reason"] = why; } else c.Response.Redirect("/login.html"); return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = 403; else c.Response.Redirect("/login.html"); return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = Auth.Validate;
    }
}
