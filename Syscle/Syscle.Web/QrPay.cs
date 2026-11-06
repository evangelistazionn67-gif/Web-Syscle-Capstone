using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;

// Pay by QR in the shop. The system asks PayMongo for a QR Ph code for one order; the customer scans it with GCash, Maya or any bank app.
// PayMongo sends the money to the store's PayMongo balance (then to the store's bank) and this page marks the order Paid by itself: no reference number to type.
// A code is good for 30 minutes and for one payment.
public class QrPhService
{
    readonly IConfiguration _config;
    public QrPhService(IConfiguration config) { _config = config; }
    string Secret => _config["PayMongo:SecretKey"] ?? "";
    public bool IsReady => Secret.StartsWith("sk_");
    HttpClient Client()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret + ":")));
        return h;
    }
    static string Why(string text)
    {
        try { using var d = JsonDocument.Parse(text); var w = d.RootElement.GetProperty("errors")[0].GetProperty("detail").GetString(); return string.IsNullOrWhiteSpace(w) ? "" : ": " + (w.Length > 180 ? w[..180] : w); }
        catch { return ""; }
    }
    // three calls: payment intent, payment method (qrph), attach. The QR picture comes back as a data:image/png;base64 string.
    public async Task<(string? intentId, string? image, string? err)> CreateAsync(string orderCode, decimal total, string customerName, string email)
    {
        if (!IsReady) return (null, null, "PayMongo is not set up yet (secret key missing).");
        try
        {
            using var http = Client();
            var amount = (int)Math.Round(total * 100m);
            var r1 = await http.PostAsJsonAsync("https://api.paymongo.com/v1/payment_intents", new { data = new { attributes = new { amount, currency = "PHP", payment_method_allowed = new[] { "qrph" }, capture_type = "automatic", description = "Web-Syscle order " + orderCode, metadata = new { order = orderCode } } } });
            var t1 = await r1.Content.ReadAsStringAsync();
            if (!r1.IsSuccessStatusCode) { Console.WriteLine("[Syscle] QR intent " + (int)r1.StatusCode + ": " + t1); return (null, null, "PayMongo could not start the QR payment" + Why(t1)); }
            using var d1 = JsonDocument.Parse(t1); var a1 = d1.RootElement.GetProperty("data"); var id = a1.GetProperty("id").GetString()!; var key = a1.GetProperty("attributes").GetProperty("client_key").GetString();
            var r2 = await http.PostAsJsonAsync("https://api.paymongo.com/v1/payment_methods", new { data = new { attributes = new { type = "qrph", billing = new { name = string.IsNullOrWhiteSpace(customerName) ? "Walk-in" : customerName, email } } } });
            var t2 = await r2.Content.ReadAsStringAsync();
            if (!r2.IsSuccessStatusCode) { Console.WriteLine("[Syscle] QR method " + (int)r2.StatusCode + ": " + t2); return (null, null, "PayMongo could not make the QR code" + Why(t2)); }
            using var d2 = JsonDocument.Parse(t2); var pmId = d2.RootElement.GetProperty("data").GetProperty("id").GetString();
            var r3 = await http.PostAsJsonAsync("https://api.paymongo.com/v1/payment_intents/" + id + "/attach", new { data = new { attributes = new { payment_method = pmId, client_key = key } } });
            var t3 = await r3.Content.ReadAsStringAsync();
            if (!r3.IsSuccessStatusCode) { Console.WriteLine("[Syscle] QR attach " + (int)r3.StatusCode + ": " + t3); return (null, null, "PayMongo could not give the QR code" + Why(t3)); }
            using var d3 = JsonDocument.Parse(t3); var img = d3.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("next_action").GetProperty("code").GetProperty("image_url").GetString();
            if (string.IsNullOrWhiteSpace(img)) return (null, null, "PayMongo did not return a QR picture. Ask PayMongo to switch on QR Ph for your account.");
            return (id, img, null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] QR error: " + ex.Message); return (null, null, "The QR code could not be made. Check the internet and try again."); }
    }
    // "paid", "waiting" or "failed"
    public async Task<string> StatusAsync(string intentId)
    {
        try
        {
            using var http = Client(); var text = await http.GetStringAsync("https://api.paymongo.com/v1/payment_intents/" + Uri.EscapeDataString(intentId));
            using var d = JsonDocument.Parse(text); var st = d.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("status").GetString();
            return st == "succeeded" ? "paid" : "waiting";
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] QR status failed: " + ex.Message); return "waiting"; }
    }
}

class QrOrder { public string Status { get; set; } = ""; public string PaymentStatus { get; set; } = ""; public decimal Total { get; set; } public string? PaymentRef { get; set; } public string? CustomerName { get; set; } public string? Email { get; set; } }

static class QrPay
{
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static IResult Conflict(string m) => Results.Conflict(new { error = m });

    // Marks the order Paid (once). A walk-in sale is finished at the same moment; an online pick-up/delivery order carries on as normal (the admin still prepares it).
    public static async Task<bool> MarkPaidAsync(MySqlConnection c, int id)
    {
        // Sql.MarkPaid is the shared rule: money that arrives for an already-cancelled order flags the order "Refund requested" instead of being lost
        var n = await c.ExecuteAsync("UPDATE Orders SET " + Sql.MarkPaid + @", PaymentConfirmedBy='PayMongo QR Ph',
            PaymentMethod=IFNULL((SELECT Label FROM PaymentMethods WHERE MethodCode='qrph'), PaymentMethod),
            CompletedAt=IF(Channel='Walk-in' AND Status<>'Cancelled',NOW(),CompletedAt), Status=IF(Channel='Walk-in' AND Status<>'Cancelled','Completed',Status)
            WHERE OrderId=@id AND PaymentStatus<>'Paid'", new { id });
        return n > 0;
    }
    public static async Task<bool> MarkPaidByIntentAsync(MySqlConnection c, string intentId)
    {
        var id = await c.ExecuteScalarAsync<int?>("SELECT OrderId FROM Orders WHERE PaymentRef=@intentId LIMIT 1", new { intentId });
        return id != null && await MarkPaidAsync(c, id.Value);
    }

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        // is QR payment ready on this server? (the sale screen hides the QR option when it is not)
        admin.MapGet("/qr/status", (QrPhService q) => Results.Ok(new { ready = q.IsReady }));

        // make the QR code for an unpaid order
        admin.MapPost("/orders/{id:int}/qr", async (int id, Db db, QrPhService q) =>
        {
            using var c = db.Open();
            var o = await c.QueryFirstOrDefaultAsync<QrOrder>(
                "SELECT o.Status, o.PaymentStatus, o.Total, o.PaymentRef, o.CustomerName, u.Email FROM Orders o LEFT JOIN Users u ON u.UserId=o.UserId WHERE o.OrderId=@id", new { id });
            if (o is null) return Results.NotFound(new { error = "Order not found." });
            var x = o;
            if (x.Status == "Cancelled") return Conflict("This order is cancelled.");
            if (x.PaymentStatus == "Paid") return Conflict("This order is already paid.");
            // a GCash reference typed by the customer is a different payment: do not overwrite it
            if (!string.IsNullOrEmpty(x.PaymentRef) && !(x.PaymentRef.StartsWith("pi_") || x.PaymentRef.StartsWith("cs_") || x.PaymentRef.StartsWith("test_"))) return Conflict("This order has a GCash reference. Use \"Payment received\" after checking it.");
            if (x.Total < 20m) return Bad("QR payment needs at least ₱20.");
            var code = (await c.ExecuteScalarAsync<string>("SELECT CONCAT(IF(Channel='Walk-in','WLK-','ORD-'), LPAD(OrderId,6,'0')) FROM Orders WHERE OrderId=@id", new { id }))!;
            var (pi, img, err) = await q.CreateAsync(code, x.Total, x.CustomerName ?? "", string.IsNullOrWhiteSpace(x.Email) ? "shop@web-syscle.local" : x.Email!);
            if (pi is null) return Bad(err ?? "Could not make the QR code.");
            await c.ExecuteAsync("UPDATE Orders SET PaymentRef=@pi WHERE OrderId=@id AND PaymentStatus<>'Paid'", new { pi, id });
            return Results.Ok(new { image = img, amount = x.Total, code, expiresInSeconds = 1800 });
        });

        // the sale screen asks this every few seconds; PayMongo says whether the customer has paid
        admin.MapGet("/orders/{id:int}/qr-status", async (int id, Db db, QrPhService q) =>
        {
            using var c = db.Open();
            var o = await c.QueryFirstOrDefaultAsync<QrOrder>("SELECT Status, PaymentStatus, PaymentRef FROM Orders WHERE OrderId=@id", new { id });
            if (o is null) return Results.NotFound(new { error = "Order not found." });
            var x = o;
            if (x.PaymentStatus == "Paid") return Results.Ok(new { paid = true });
            if (x.Status == "Cancelled") return Results.Ok(new { paid = false, cancelled = true });
            if (x.PaymentRef != null && x.PaymentRef.StartsWith("pi_") && await q.StatusAsync(x.PaymentRef) == "paid") { await MarkPaidAsync(c, id); return Results.Ok(new { paid = true }); }
            return Results.Ok(new { paid = false });
        });

        // the customer changed their mind or the code ran out: a walk-in sale that was never paid is cancelled and its stock goes back
        admin.MapPost("/orders/{id:int}/qr-cancel", async (int id, Db db, System.Security.Claims.ClaimsPrincipal u) =>
        {
            using var c = db.Open(); using var tx = c.BeginTransaction();
            var st = await c.QueryFirstOrDefaultAsync<QrOrder>("SELECT Status, PaymentStatus FROM Orders WHERE OrderId=@id AND Channel='Walk-in' FOR UPDATE", new { id }, tx);
            if (st is null) return Results.NotFound(new { error = "Walk-in sale not found." });
            if (st.PaymentStatus == "Paid") return Conflict("This sale was already paid.");
            if (st.Status != "Cancelled") { await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name); await c.ExecuteAsync("UPDATE Orders SET Status='Cancelled' WHERE OrderId=@id", new { id }, tx); }
            tx.Commit(); return Results.Ok(new { ok = true });
        });
    }
}
