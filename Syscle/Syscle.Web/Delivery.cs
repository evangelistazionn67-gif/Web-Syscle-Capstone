using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;

// Home delivery. The store (admin) handles every delivery itself:
//   1. the customer pins the delivery place on a map; the server measures the distance, refuses places outside the store's range and works out the fee
//   2. delivery orders are paid with GCash or PayMongo up front, or with cash on delivery (up to the CodMax limit). Cash is recorded by the admin when the order is delivered.
//   3. after the items are confirmed and (for GCash / PayMongo) the payment is recorded, the admin books a Lalamove rider with one button (or types in a booking made in the Lalamove app)
//   4. the order follows the Lalamove status (rider found, picked up, delivered) by itself
record DeliveryQuoteReq(double Lat, double Lng);
record LalaBookReq(string? QuotationId, string? PickupStopId, string? DropStopId);
record LalaManualReq(string? Ref, string? Driver, string? Phone, string? Link, decimal? Cost);

static class Geo
{
    // straight-line distance in km (haversine). Roads are longer than a straight line, so RoadKm multiplies it by 1.3.
    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0; double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1); var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
    public static double RoadKm(double lat1, double lon1, double lat2, double lon2) => Math.Round(Km(lat1, lon1, lat2, lon2) * 1.3, 1);
    public static bool ValidPoint(double? lat, double? lng) => lat is >= -90 and <= 90 && lng is >= -180 and <= 180 && !(lat == 0 && lng == 0);
    // Lalamove wants phone numbers like +639171234567
    public static string? E164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("09") && digits.Length == 11) return "+63" + digits[1..];
        if (digits.StartsWith("639") && digits.Length == 12) return "+" + digits;
        if (digits.StartsWith("9") && digits.Length == 10) return "+63" + digits;
        return null;
    }
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
}

class DeliveryRule
{
    public bool Enabled { get; set; } public double StoreLat { get; set; } public double StoreLng { get; set; }
    public decimal MaxKm { get; set; } public decimal Base { get; set; } public decimal PerKm { get; set; }
}
static class DeliveryRules
{
    public static async Task<DeliveryRule> LoadAsync(MySqlConnection c)
    {
        var r = new DeliveryRule();
        var okLat = double.TryParse(await Cfg.GetAsync(c, "StoreLat"), NumberStyles.Float, Geo.Inv, out var la);
        var okLng = double.TryParse(await Cfg.GetAsync(c, "StoreLng"), NumberStyles.Float, Geo.Inv, out var lo);
        r.StoreLat = la; r.StoreLng = lo; r.Enabled = okLat && okLng && Geo.ValidPoint(la, lo);
        r.MaxKm = await Cfg.DecAsync(c, "DeliveryMaxKm", 1, 100); r.Base = await Cfg.DecAsync(c, "DeliveryBaseFee", 0, 5000); r.PerKm = await Cfg.DecAsync(c, "DeliveryPerKm", 0, 500);
        return r;
    }
    // base fee + a price per km, rounded up to the next 5 pesos
    public static decimal Fee(DeliveryRule r, double km) => Math.Ceiling((r.Base + r.PerKm * (decimal)km) / 5m) * 5m;
}

// Talks to the Lalamove API (v3). Sandbox keys (pk_test / sk_test) are for practice, production keys (pk_prod / sk_prod) book real riders and charge your Lalamove wallet.
class LalamoveService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    readonly IConfiguration _config;
    public LalamoveService(IConfiguration config) { _config = config; }
    string Key => (_config["Lalamove:ApiKey"] ?? "").Trim();
    string Secret => (_config["Lalamove:Secret"] ?? "").Trim();
    string Market => string.IsNullOrWhiteSpace(_config["Lalamove:Market"]) ? "PH" : _config["Lalamove:Market"]!.Trim();
    public bool Production => string.Equals(_config["Lalamove:Mode"], "production", StringComparison.OrdinalIgnoreCase);
    public bool IsReady => Key.Length > 8 && Secret.Length > 8;
    string BaseUrl => Production ? "https://rest.lalamove.com" : "https://rest.sandbox.lalamove.com";
    public static readonly string[] Final = { "COMPLETED", "CANCELED", "REJECTED", "EXPIRED" };
    public static readonly string[] Dead = { "CANCELED", "REJECTED", "EXPIRED" };

    public static string Label(string? s) => s switch
    {
        "BOOKING" => "Booking a rider...", "ASSIGNING_DRIVER" => "Finding a rider", "ON_GOING" => "Rider is on the way to the store", "PICKED_UP" => "Out for delivery",
        "COMPLETED" => "Delivered", "CANCELED" => "Booking cancelled", "REJECTED" => "Rider rejected the job", "EXPIRED" => "No rider found (expired)", "MANUAL" => "Booked by the store", _ => ""
    };

    // every call is signed: HMAC-SHA256 of "time CRLF METHOD CRLF path CRLF CRLF body" with the API secret
    async Task<(int code, string raw)> CallAsync(HttpMethod method, string path, object? body = null)
    {
        var json = body is null ? "" : JsonSerializer.Serialize(body);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(Geo.Inv);
        var raw = ts + "\r\n" + method.Method + "\r\n" + path + "\r\n\r\n" + json;
        var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("Authorization", "hmac " + Key + ":" + ts + ":" + sig);
        req.Headers.TryAddWithoutValidation("Market", Market);
        req.Headers.TryAddWithoutValidation("Request-ID", Guid.NewGuid().ToString());
        if (body is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req); var text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, text);
    }
    static string Err(string raw, int code)
    {
        try { using var d = JsonDocument.Parse(raw); if (d.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) { var m = e[0].TryGetProperty("message", out var mm) ? mm.GetString() : null; var id = e[0].TryGetProperty("id", out var ii) ? ii.GetString() : null; return "Lalamove says: " + (m ?? id ?? "error " + code); } } catch { }
        if (code == 401) return "Lalamove refused the keys (check the API key and secret, and that Sandbox/Production matches the keys).";
        return "Lalamove error " + code + ".";
    }
    static decimal Dec(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetDecimal();
        return decimal.TryParse(e.GetString(), NumberStyles.Number, Geo.Inv, out var v) ? v : 0m;
    }

    public record Quote(string Id, decimal Price, string Currency, string? ExpiresAt, string PickupStopId, string DropStopId);
    public async Task<(Quote? q, string? err)> QuoteAsync(string service, double sLat, double sLng, string sAddr, double dLat, double dLng, string dAddr)
    {
        if (!IsReady) return (null, "Lalamove is not set up yet. You can still book in the Lalamove app and type the booking in.");
        try
        {
            var body = new { data = new { serviceType = service, language = "en_PH", stops = new[] {
                new { coordinates = new { lat = sLat.ToString("0.######", Geo.Inv), lng = sLng.ToString("0.######", Geo.Inv) }, address = sAddr },
                new { coordinates = new { lat = dLat.ToString("0.######", Geo.Inv), lng = dLng.ToString("0.######", Geo.Inv) }, address = dAddr } } } };
            var (code, raw) = await CallAsync(HttpMethod.Post, "/v3/quotations", body);
            if (code < 200 || code >= 300) { Console.WriteLine("[Syscle] Lalamove quotation " + code + ": " + (raw.Length > 300 ? raw[..300] : raw)); return (null, Err(raw, code)); }
            using var doc = JsonDocument.Parse(raw); var d = doc.RootElement.GetProperty("data"); var stops = d.GetProperty("stops");
            var pb = d.GetProperty("priceBreakdown");
            var q = new Quote(d.GetProperty("quotationId").GetString()!, Dec(pb.GetProperty("total")), pb.TryGetProperty("currency", out var cur) ? (cur.GetString() ?? "PHP") : "PHP",
                d.TryGetProperty("expiresAt", out var ex) ? ex.GetString() : null, stops[0].GetProperty("stopId").GetString()!, stops[1].GetProperty("stopId").GetString()!);
            return (q, null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] Lalamove quotation failed: " + ex.Message); return (null, "Could not reach Lalamove. Try again."); }
    }

    public async Task<(string? orderId, string? link, string? status, decimal? price, string? err)> PlaceAsync(string quotationId, string pickupStop, string dropStop, string senderName, string senderPhone, string toName, string toPhone, string remarks)
    {
        if (!IsReady) return (null, null, null, null, "Lalamove is not set up yet.");
        try
        {
            var body = new { data = new { quotationId, sender = new { stopId = pickupStop, name = senderName, phone = senderPhone },
                recipients = new[] { new { stopId = dropStop, name = toName, phone = toPhone, remarks } }, isPODEnabled = false, metadata = new { source = "web-syscle" } } };
            var (code, raw) = await CallAsync(HttpMethod.Post, "/v3/orders", body);
            if (code < 200 || code >= 300) { Console.WriteLine("[Syscle] Lalamove order " + code + ": " + (raw.Length > 300 ? raw[..300] : raw)); return (null, null, null, null, Err(raw, code)); }
            using var doc = JsonDocument.Parse(raw); var d = doc.RootElement.GetProperty("data");
            decimal? price = d.TryGetProperty("priceBreakdown", out var pb) && pb.TryGetProperty("total", out var tt) ? Dec(tt) : null;
            return (d.GetProperty("orderId").GetString(), d.TryGetProperty("shareLink", out var sl) ? sl.GetString() : null, d.TryGetProperty("status", out var st) ? st.GetString() : "ASSIGNING_DRIVER", price, null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] Lalamove order failed: " + ex.Message); return (null, null, null, null, "Could not reach Lalamove. Check the Lalamove app before booking again so a rider is not booked twice."); }
    }

    public async Task<(string? status, string? driverId, string? link, decimal? price, string? err)> GetAsync(string orderId)
    {
        try
        {
            var (code, raw) = await CallAsync(HttpMethod.Get, "/v3/orders/" + Uri.EscapeDataString(orderId));
            if (code < 200 || code >= 300) return (null, null, null, null, Err(raw, code));
            using var doc = JsonDocument.Parse(raw); var d = doc.RootElement.GetProperty("data");
            decimal? price = d.TryGetProperty("priceBreakdown", out var pb) && pb.TryGetProperty("total", out var tt) ? Dec(tt) : null;
            return (d.TryGetProperty("status", out var st) ? st.GetString() : null, d.TryGetProperty("driverId", out var dr) ? dr.GetString() : null, d.TryGetProperty("shareLink", out var sl) ? sl.GetString() : null, price, null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] Lalamove status failed: " + ex.Message); return (null, null, null, null, "Could not reach Lalamove."); }
    }

    // "Name, plate, phone" of the rider (only available once a rider took the job)
    public async Task<string?> DriverTextAsync(string orderId, string driverId)
    {
        try
        {
            var (code, raw) = await CallAsync(HttpMethod.Get, "/v3/orders/" + Uri.EscapeDataString(orderId) + "/drivers/" + Uri.EscapeDataString(driverId));
            if (code < 200 || code >= 300) return null;
            using var doc = JsonDocument.Parse(raw); var d = doc.RootElement.GetProperty("data");
            string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var parts = new[] { S("name"), S("plateNumber"), S("phone") }.Where(x => !string.IsNullOrWhiteSpace(x));
            var text = string.Join(" | ", parts); return text.Length == 0 ? null : (text.Length > 120 ? text[..120] : text);
        }
        catch { return null; }
    }

    public async Task<string?> CancelAsync(string orderId)
    {
        if (!IsReady) return "Lalamove is not set up yet.";
        try
        {
            var (code, raw) = await CallAsync(HttpMethod.Delete, "/v3/orders/" + Uri.EscapeDataString(orderId));
            if (code == 204 || (code >= 200 && code < 300)) return null;
            if (code == 409) return "Lalamove no longer allows cancelling this job (a rider already picked it up). Cancel it in the Lalamove app or call the rider.";
            return Err(raw, code);
        }
        catch { return "Could not reach Lalamove."; }
    }

    // the list of cities and vehicle types for this market (used once to find the right service name)
    public async Task<string> CitiesAsync()
    {
        if (!IsReady) return "{\"error\":\"Lalamove is not set up yet.\"}";
        try { var (_, raw) = await CallAsync(HttpMethod.Get, "/v3/cities"); return raw; } catch (Exception ex) { return "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }
    }
}

class DelRow
{
    public int OrderId { get; set; } public int? UserId { get; set; } public string Channel { get; set; } = "Online"; public string? CustomerName { get; set; }
    public string Status { get; set; } = ""; public string PaymentStatus { get; set; } = ""; public string? PaymentMethod { get; set; } public bool IsCash { get; set; } public decimal Total { get; set; } public DateTime? ItemsConfirmedAt { get; set; } public string RefundStatus { get; set; } = "None";
    public string FulfillmentType { get; set; } = "Pickup"; public string? DeliveryAddress { get; set; } public string? DeliveryPhone { get; set; }
    public decimal? DeliveryLat { get; set; } public decimal? DeliveryLng { get; set; } public decimal? DeliveryKm { get; set; } public decimal DeliveryFee { get; set; }
    public string? LalamoveOrderId { get; set; } public string? LalamoveStatus { get; set; } public string? LalamoveLink { get; set; } public string? LalamoveDriver { get; set; }
    public decimal? LalamoveCost { get; set; } public DateTime? LalamoveCheckedAt { get; set; } public string? Code { get; set; }
}

static class DeliveryEndpoints
{
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static IResult Conflict(string m) => Results.Conflict(new { error = m });
    static int Uid(ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    const string Cols = "o.OrderId, o.UserId, o.Channel, o.CustomerName, o.Status, o.PaymentStatus, o.PaymentMethod, EXISTS(SELECT 1 FROM PaymentMethods pm WHERE pm.Label=o.PaymentMethod AND pm.MethodCode='cash') AS IsCash, o.Total, o.ItemsConfirmedAt, o.RefundStatus, o.FulfillmentType, o.DeliveryAddress, o.DeliveryPhone, o.DeliveryLat, o.DeliveryLng, o.DeliveryKm, o.DeliveryFee, " +
        "o.LalamoveOrderId, o.LalamoveStatus, o.LalamoveLink, o.LalamoveDriver, o.LalamoveCost, o.LalamoveCheckedAt, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS Code";
    public static Task<DelRow?> LoadAsync(MySqlConnection c, int id) => c.QueryFirstOrDefaultAsync<DelRow>("SELECT " + Cols + " FROM Orders o WHERE o.OrderId=@id", new { id });

    // asks Lalamove for the newest status of a booked rider and keeps the order in step (picked up -> Out for Delivery, delivered -> Completed once paid)
    public static async Task<DelRow?> RefreshAsync(MySqlConnection c, LalamoveService lala, int id, bool force = false)
    {
        var r = await LoadAsync(c, id);
        if (r is null || r.LalamoveOrderId is null || r.LalamoveStatus is null || r.LalamoveStatus is "MANUAL" or "BOOKING" || LalamoveService.Final.Contains(r.LalamoveStatus) || !lala.IsReady) return r;
        if (!force && r.LalamoveCheckedAt != null && (DateTime.Now - r.LalamoveCheckedAt.Value).TotalSeconds < 25) return r;
        var (st, drv, link, price, err) = await lala.GetAsync(r.LalamoveOrderId);
        if (err != null || st is null) return r;
        var driver = r.LalamoveDriver;
        if (!string.IsNullOrEmpty(drv) && string.IsNullOrEmpty(driver) && (st == "ON_GOING" || st == "PICKED_UP" || st == "COMPLETED")) driver = await lala.DriverTextAsync(r.LalamoveOrderId, drv) ?? driver;
        await c.ExecuteAsync("UPDATE Orders SET LalamoveStatus=@st, LalamoveLink=IFNULL(@link,LalamoveLink), LalamoveCost=IFNULL(@price,LalamoveCost), LalamoveDriver=@driver, LalamoveCheckedAt=NOW() WHERE OrderId=@id", new { st, link, price, driver, id });
        if (st == "PICKED_UP" && (r.Status == "Pending" || r.Status == "Preparing" || r.Status == "Ready") && r.ItemsConfirmedAt != null)
            await c.ExecuteAsync("UPDATE Orders SET Status='Out for Delivery', ReadyAt=IFNULL(ReadyAt,NOW()) WHERE OrderId=@id", new { id });
        if (st == "COMPLETED" && r.PaymentStatus == "Paid" && r.Status != "Completed" && r.Status != "Cancelled" && r.ItemsConfirmedAt != null)
            await c.ExecuteAsync("UPDATE Orders SET Status='Completed', CompletedAt=NOW(), ReadyAt=IFNULL(ReadyAt,NOW()) WHERE OrderId=@id", new { id });
        return await LoadAsync(c, id);
    }

    static object View(DelRow r, LalamoveService lala, bool admin)
    {
        var st = r.LalamoveStatus;
        return new
        {
            id = r.OrderId, code = r.Code, address = r.DeliveryAddress, phone = r.DeliveryPhone, km = r.DeliveryKm, fee = r.DeliveryFee,
            cod = r.IsCash, total = r.Total, paid = r.PaymentStatus == "Paid", lat = admin ? r.DeliveryLat : null, lng = admin ? r.DeliveryLng : null,
            booked = st != null && !LalamoveService.Dead.Contains(st), lalamoveStatus = st, label = LalamoveService.Label(st), link = r.LalamoveLink, driver = r.LalamoveDriver,
            cost = admin ? r.LalamoveCost : null, bookingRef = admin ? r.LalamoveOrderId : null,
            canBook = admin && r.FulfillmentType == "Delivery" && r.Status != "Cancelled" && r.Status != "Completed" && (r.PaymentStatus == "Paid" || r.IsCash) && r.ItemsConfirmedAt != null && r.RefundStatus != "Requested" && r.RefundStatus != "Refunded"
                      && Geo.ValidPoint((double?)r.DeliveryLat, (double?)r.DeliveryLng) && (st == null || LalamoveService.Dead.Contains(st)),
            lalamoveReady = admin ? lala.IsReady : (bool?)null
        };
    }

    public static void Map(WebApplication app)
    {
        var shop = app.MapGroup("/api").RequireAuthorization("Customer");
        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        // what the cart needs to show the map and the fee (the store location is public: it is the shop's address)
        app.MapGet("/api/delivery/config", async (Db db) =>
        {
            using var c = db.Open(); var r = await DeliveryRules.LoadAsync(c);
            return Results.Ok(new { enabled = r.Enabled, storeLat = r.Enabled ? r.StoreLat : (double?)null, storeLng = r.Enabled ? r.StoreLng : (double?)null, maxKm = r.MaxKm, baseFee = r.Base, perKm = r.PerKm, codMax = await Cfg.IntAsync(c, "CodMax", 0, 1000000) });
        });
        shop.MapPost("/delivery/quote", async (DeliveryQuoteReq r, Db db) =>
        {
            if (!Geo.ValidPoint(r.Lat, r.Lng)) return Bad("Pin your delivery place on the map.");
            using var c = db.Open(); var rule = await DeliveryRules.LoadAsync(c);
            if (!rule.Enabled) return Bad("Home delivery is not available right now. Please choose Store pick-up.");
            var km = Geo.RoadKm(rule.StoreLat, rule.StoreLng, r.Lat, r.Lng);
            return Results.Ok(new { km, maxKm = rule.MaxKm, inRange = (decimal)km <= rule.MaxKm, fee = DeliveryRules.Fee(rule, km) });
        });
        // the customer's order card asks for the rider status (refreshed from Lalamove at most every 25 seconds)
        shop.MapGet("/orders/{id:int}/delivery", async (int id, ClaimsPrincipal u, Db db, LalamoveService lala) =>
        {
            using var c = db.Open(); var own = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE OrderId=@id AND UserId=@uid AND FulfillmentType='Delivery'", new { id, uid = Uid(u) });
            if (own == 0) return Results.NotFound(new { error = "Not found." });
            var r = await RefreshAsync(c, lala, id); return r is null ? Results.NotFound(new { error = "Not found." }) : Results.Ok(View(r, lala, false));
        });

        admin.MapGet("/delivery/status", async (Db db, LalamoveService lala) =>
        {
            using var c = db.Open(); var rule = await DeliveryRules.LoadAsync(c);
            return Results.Ok(new { lalamoveReady = lala.IsReady, production = lala.Production, service = await Cfg.GetAsync(c, "LalamoveService"), storeSet = rule.Enabled, maxKm = rule.MaxKm, baseFee = rule.Base, perKm = rule.PerKm });
        });
        // money picture of deliveries (last 30 days): what customers paid for delivery vs what Lalamove charged the store
        admin.MapGet("/delivery/summary", async (Db db) =>
        {
            using var c = db.Open();
            var s = await c.QueryFirstAsync("SELECT COUNT(*) AS orders, IFNULL(SUM(o.DeliveryFee),0) AS collected, IFNULL(SUM(o.LalamoveCost),0) AS paidToLalamove, IFNULL(SUM(CASE WHEN o.LalamoveCost IS NOT NULL THEN o.DeliveryFee ELSE 0 END),0) AS collectedBooked, " +
                "IFNULL(SUM(CASE WHEN o.LalamoveStatus IS NULL OR o.LalamoveStatus IN ('CANCELED','REJECTED','EXPIRED') THEN 1 ELSE 0 END),0) AS toBook " +
                "FROM Orders o WHERE o.FulfillmentType='Delivery' AND o.PaymentStatus='Paid' AND o.RefundStatus<>'Refunded' AND o.Status<>'Cancelled' AND o.CreatedAt >= DATE_SUB(NOW(), INTERVAL 30 DAY)");
            return Results.Ok(s);
        });
        admin.MapGet("/delivery/cities", async (LalamoveService lala) => Results.Content(await lala.CitiesAsync(), "application/json"));

        admin.MapGet("/orders/{id:int}/delivery", async (int id, bool? refresh, Db db, LalamoveService lala) =>
        {
            using var c = db.Open(); var r = await RefreshAsync(c, lala, id, refresh == true);
            return r is null || r.FulfillmentType != "Delivery" ? Results.NotFound(new { error = "This is not a delivery order." }) : Results.Ok(View(r, lala, true));
        });

        // step 1: ask Lalamove what it would charge (nothing is booked yet)
        admin.MapPost("/orders/{id:int}/lalamove/quote", async (int id, Db db, LalamoveService lala) =>
        {
            using var c = db.Open(); var r = await LoadAsync(c, id); var rule = await DeliveryRules.LoadAsync(c);
            if (r is null || r.FulfillmentType != "Delivery") return Results.NotFound(new { error = "This is not a delivery order." });
            if (!rule.Enabled) return Bad("Set the store location in Settings first.");
            if (!Geo.ValidPoint((double?)r.DeliveryLat, (double?)r.DeliveryLng)) return Bad("This order has no pinned location. Use the Lalamove app and type the booking in.");
            var storeAddr = await Cfg.GetAsync(c, "StoreAddress"); if (string.IsNullOrWhiteSpace(storeAddr)) storeAddr = await Cfg.GetAsync(c, "StoreName");
            var (q, err) = await lala.QuoteAsync(await Cfg.GetAsync(c, "LalamoveService"), rule.StoreLat, rule.StoreLng, storeAddr, (double)r.DeliveryLat!, (double)r.DeliveryLng!, r.DeliveryAddress ?? "");
            if (q is null) return Bad(err ?? "Could not get a price.");
            return Results.Ok(new { quotationId = q.Id, price = q.Price, currency = q.Currency, expiresAt = q.ExpiresAt, pickupStopId = q.PickupStopId, dropStopId = q.DropStopId, feeCollected = r.DeliveryFee, difference = r.DeliveryFee - q.Price });
        });

        // step 2: book the rider (money: Lalamove charges the store's Lalamove wallet; the customer already paid the delivery fee inside the order total)
        admin.MapPost("/orders/{id:int}/lalamove/book", async (int id, LalaBookReq b, Db db, LalamoveService lala, ClaimsPrincipal u) =>
        {
            if (string.IsNullOrWhiteSpace(b.QuotationId) || string.IsNullOrWhiteSpace(b.PickupStopId) || string.IsNullOrWhiteSpace(b.DropStopId)) return Bad("Get the price first.");
            using var c = db.Open(); var r = await LoadAsync(c, id);
            if (r is null || r.FulfillmentType != "Delivery") return Results.NotFound(new { error = "This is not a delivery order." });
            if (r.Status == "Cancelled" || r.Status == "Completed") return Conflict("This order is already finished.");
            if (r.PaymentStatus != "Paid" && !r.IsCash) return Conflict("Record the payment first. A rider is booked only for a paid order (or a cash-on-delivery order).");
            if (r.ItemsConfirmedAt == null) return Conflict("Confirm the items first.");
            if (r.RefundStatus == "Requested" || r.RefundStatus == "Refunded") return Conflict("This order has a refund. Do not book a rider.");
            var sender = Geo.E164(await Cfg.GetAsync(c, "StorePhone")); if (sender is null) return Bad("Put the store phone number (like 09171234567) in Settings. Lalamove needs it for the pick-up.");
            var toPhone = Geo.E164(r.DeliveryPhone); if (toPhone is null) return Bad("The customer's phone number is not a valid mobile number. Use the Lalamove app and type the booking in.");
            // the order is "locked" first so a double click can never book two riders (and charge twice)
            var prev = r.LalamoveStatus;
            var got = await c.ExecuteAsync("UPDATE Orders SET LalamoveStatus='BOOKING' WHERE OrderId=@id AND (LalamoveOrderId IS NULL OR LalamoveStatus IS NULL OR LalamoveStatus IN ('CANCELED','REJECTED','EXPIRED'))", new { id });
            if (got == 0) return Conflict("A rider is already booked for this order.");
            var storeName = await Cfg.GetAsync(c, "StoreName");
            var (oid, link, st, price, err) = await lala.PlaceAsync(b.QuotationId!, b.PickupStopId!, b.DropStopId!, storeName.Length > 0 ? storeName : "Web-Syscle", sender, string.IsNullOrWhiteSpace(r.CustomerName) ? "Customer" : r.CustomerName!, toPhone, "Order " + r.Code + ". Please call the customer on arrival.");
            if (oid is null)
            {
                await c.ExecuteAsync("UPDATE Orders SET LalamoveStatus=@prev WHERE OrderId=@id AND LalamoveStatus='BOOKING'", new { prev, id });
                return Bad(err ?? "Lalamove could not book the rider.");
            }
            await c.ExecuteAsync("UPDATE Orders SET LalamoveOrderId=@oid, LalamoveStatus=@st, LalamoveLink=@link, LalamoveCost=@price, LalamoveDriver=NULL, LalamoveCheckedAt=NOW(), Courier='Lalamove', TrackingNo=LEFT(@oid,60), " +
                "Status=IF(Status IN ('Pending','Preparing'),'Ready',Status), ReadyAt=IFNULL(ReadyAt,NOW()) WHERE OrderId=@id", new { oid, st = st ?? "ASSIGNING_DRIVER", link, price, id });
            await Audit.AddAsync(c, u.Identity?.Name, "Lalamove booked " + oid + (price != null ? " (PHP " + price + ")" : ""), "Order " + id);
            return Results.Ok(new { ok = true });
        });

        // for a booking the admin made in the Lalamove app (or while the API is not set up): the order still follows the delivery steps
        admin.MapPost("/orders/{id:int}/lalamove/manual", async (int id, LalaManualReq b, Db db, ClaimsPrincipal u) =>
        {
            var reference = Nz(b.Ref); if (reference is null || reference.Length > 40) return Bad("Type the Lalamove booking number (or any reference).");
            var driver = Nz(b.Driver); var phone = Nz(b.Phone); var link = Nz(b.Link);
            if (link != null && (link.Length > 400 || !(link.StartsWith("https://") || link.StartsWith("http://")))) return Bad("The tracking link must start with https://");
            if (b.Cost != null && (b.Cost < 0 || b.Cost > 20000)) return Bad("Check the Lalamove price.");
            var text = string.Join(" | ", new[] { driver, phone }.Where(x => x != null)); if (text.Length > 120) text = text[..120];
            using var c = db.Open(); var r = await LoadAsync(c, id);
            if (r is null || r.FulfillmentType != "Delivery") return Results.NotFound(new { error = "This is not a delivery order." });
            if (r.Status == "Cancelled" || r.Status == "Completed") return Conflict("This order is already finished.");
            if (r.PaymentStatus != "Paid" && !r.IsCash) return Conflict("Record the payment first.");
            if (r.ItemsConfirmedAt == null) return Conflict("Confirm the items first.");
            if (r.LalamoveStatus != null && !LalamoveService.Dead.Contains(r.LalamoveStatus)) return Conflict("A rider is already booked for this order.");
            await c.ExecuteAsync("UPDATE Orders SET LalamoveOrderId=@reference, LalamoveStatus='MANUAL', LalamoveLink=@link, LalamoveCost=@cost, LalamoveDriver=@text, Courier='Lalamove', TrackingNo=LEFT(@reference,60), Status='Out for Delivery', ReadyAt=IFNULL(ReadyAt,NOW()) WHERE OrderId=@id",
                new { reference, link, cost = b.Cost, text = text.Length == 0 ? null : text, id });
            await Audit.AddAsync(c, u.Identity?.Name, "Lalamove booking typed in " + reference, "Order " + id);
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/orders/{id:int}/lalamove/cancel", async (int id, Db db, LalamoveService lala, ClaimsPrincipal u) =>
        {
            using var c = db.Open(); var r = await LoadAsync(c, id);
            if (r is null || r.LalamoveOrderId is null || r.LalamoveStatus is null) return Results.NotFound(new { error = "No rider is booked for this order." });
            if (LalamoveService.Final.Contains(r.LalamoveStatus)) return Conflict("This booking is already finished.");
            if (r.LalamoveStatus == "MANUAL") { await c.ExecuteAsync("UPDATE Orders SET LalamoveStatus='CANCELED' WHERE OrderId=@id", new { id }); return Results.Ok(new { ok = true }); }
            var err = await lala.CancelAsync(r.LalamoveOrderId); if (err != null) return Conflict(err);
            await c.ExecuteAsync("UPDATE Orders SET LalamoveStatus='CANCELED', Courier=NULL, TrackingNo=NULL, Status=IF(Status='Out for Delivery','Ready',Status) WHERE OrderId=@id", new { id });
            await Audit.AddAsync(c, u.Identity?.Name, "Lalamove cancelled " + r.LalamoveOrderId, "Order " + id);
            return Results.Ok(new { ok = true });
        });
    }
}
