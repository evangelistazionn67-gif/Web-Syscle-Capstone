using System.Security.Claims;
using Dapper;
using MySqlConnector;

// Printable receipts. One page (/Pages/receipt.html?order=ID) shows every receipt, for customers (their own orders) and for the admin (any order).
class ReceiptOrder
{
    public int OrderId { get; set; } public int? UserId { get; set; } public string Channel { get; set; } = "Online"; public string? CustomerName { get; set; }
    public DateTime CreatedAt { get; set; } public DateTime? PaidAt { get; set; } public string Status { get; set; } = ""; public string PaymentStatus { get; set; } = "";
    public string? PaymentMethod { get; set; } public string? PaymentRef { get; set; } public string? PromoCode { get; set; }
    public decimal Discount { get; set; } public decimal BundleDiscount { get; set; } public decimal DeliveryFee { get; set; } public decimal Total { get; set; }
    public string FulfillmentType { get; set; } = "Pickup"; public string? DeliveryAddress { get; set; } public string? DeliveryPhone { get; set; } public DateTime? PickupDate { get; set; }
    public string RefundStatus { get; set; } = "None"; public decimal? RefundAmount { get; set; } public string? PaymentConfirmedBy { get; set; }
}
class ReceiptItem { public string ProductName { get; set; } = ""; public string Size { get; set; } = ""; public string Color { get; set; } = ""; public decimal UnitPrice { get; set; } public int Qty { get; set; } }

static class Receipts
{
    static string Code(ReceiptOrder o) => (o.Channel == "Walk-in" ? "WLK-" : "ORD-") + o.OrderId.ToString("D6");

    public static async Task<object?> BuildAsync(MySqlConnection c, int id, int? onlyUser)
    {
        var o = await c.QueryFirstOrDefaultAsync<ReceiptOrder>("SELECT OrderId, UserId, Channel, CustomerName, CreatedAt, PaidAt, Status, PaymentStatus, PaymentMethod, PaymentRef, PromoCode, Discount, BundleDiscount, DeliveryFee, Total, FulfillmentType, DeliveryAddress, DeliveryPhone, PickupDate, RefundStatus, RefundAmount, PaymentConfirmedBy FROM Orders WHERE OrderId=@id", new { id });
        if (o is null || (onlyUser != null && o.UserId != onlyUser)) return null;
        var items = (await c.QueryAsync<ReceiptItem>("SELECT ProductName, Size, Color, UnitPrice, Qty FROM OrderItems WHERE OrderId=@id ORDER BY OrderItemId", new { id })).ToList();
        var subtotal = items.Sum(i => i.UnitPrice * i.Qty);
        // a GCash / PayMongo reference is shown in full on the receipt; it is the customer's own payment
        var shown = o.PaymentStatus == "Paid" ? "PAID" : (o.Status == "Cancelled" ? "CANCELLED" : "UNPAID");
        if (o.RefundStatus == "Refunded") shown = "REFUNDED";
        return new
        {
            code = Code(o), channel = o.Channel, date = o.CreatedAt, paidAt = o.PaidAt, customer = o.CustomerName, cashier = onlyUser == null ? o.PaymentConfirmedBy : null,
            payment = o.PaymentMethod, paymentRef = o.PaymentRef, paymentStatus = shown, status = o.Status,
            fulfillment = o.FulfillmentType, address = o.FulfillmentType == "Delivery" ? o.DeliveryAddress : null, phone = o.FulfillmentType == "Delivery" ? o.DeliveryPhone : null, pickup = o.PickupDate,
            items = items.Select(i => new { name = i.ProductName, size = i.Size, color = i.Color, price = i.UnitPrice, qty = i.Qty, line = i.UnitPrice * i.Qty }),
            subtotal, bundleDiscount = o.BundleDiscount, promoCode = o.PromoCode, discount = o.Discount, deliveryFee = o.DeliveryFee, total = o.Total, refunded = o.RefundStatus == "Refunded" ? o.RefundAmount : null,
            store = new
            {
                name = await Cfg.GetAsync(c, "StoreName"), address = await Cfg.GetAsync(c, "StoreAddress"), phone = await Cfg.GetAsync(c, "StorePhone"), hours = await Cfg.GetAsync(c, "StoreHours"),
                footer = await Cfg.GetAsync(c, "ReceiptFooter"), tin = await Cfg.GetAsync(c, "StoreTIN")
            },
            autoPrint = (await Cfg.GetAsync(c, "AutoPrintReceipt")) != "0"
        };
    }

    public static void Map(WebApplication app)
    {
        var shop = app.MapGroup("/api").RequireAuthorization("Customer");
        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        shop.MapGet("/orders/{id:int}/receipt", async (int id, ClaimsPrincipal u, Db db) =>
        {
            using var c = db.Open(); var r = await BuildAsync(c, id, int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!));
            return r is null ? Results.NotFound(new { error = "Receipt not found." }) : Results.Ok(r);
        });
        admin.MapGet("/orders/{id:int}/receipt", async (int id, Db db) =>
        {
            using var c = db.Open(); var r = await BuildAsync(c, id, null);
            return r is null ? Results.NotFound(new { error = "Receipt not found." }) : Results.Ok(r);
        });
    }
}
