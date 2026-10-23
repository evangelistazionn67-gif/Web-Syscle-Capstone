using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using MySqlConnector;
using SendGrid;
using SendGrid.Helpers.Mail;
using QRCoder;
using OtpNet;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 6_000_000; k.Limits.MaxRequestHeadersTotalSize = 32_768; k.Limits.MaxRequestLineSize = 8_192; });
var cs = builder.Configuration.GetConnectionString("Syscle") ?? throw new InvalidOperationException("Missing ConnectionStrings:Syscle in appsettings.json");
builder.Services.AddSingleton(new Db(cs));
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<SmsService>();
// keep the data-protection keys in a folder so sign-in cookies and reset links survive a restart
builder.Services.AddDataProtection().SetApplicationName("Syscle").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "syscle.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { if (c.Request.Path.StartsWithSegments("/api")) { c.Response.StatusCode = 401; if (c.HttpContext.Items["sy-reason"] is string why) c.Response.Headers["X-Session-Reason"] = why; } else c.Response.Redirect("/index.html"); return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = 403; else c.Response.Redirect("/index.html"); return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = Auth.Validate;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("SuperAdmin", p => p.RequireRole("SuperAdmin"))
    .AddPolicy("Admin", p => p.RequireRole("Admin", "SuperAdmin"))
    .AddPolicy("Customer", p => p.RequireRole("Customer"));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler(a => a.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error; if (ex != null) Console.WriteLine("[Syscle] " + ctx.Request.Method + " " + ctx.Request.Path + " failed: " + ex);
    ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "The server could not finish that. Please try again." });
}));
await Seed.RunAsync(cs, builder.Configuration);

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    // normal JSON requests are tiny; cap them so nobody can flood the server with a giant body (uploads use multipart and keep their own limit)
    if (ctx.Request.Path.StartsWithSegments("/api") && (ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
    {
        var lim = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (lim != null && !lim.IsReadOnly) lim.MaxRequestBodySize = 20_000;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-Requested-With"] != "syscle")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Bad request." });
        return;
    }
    var need = Gate.Required(ctx.Request.Path.Value ?? "");
    if (need != null)
    {
        if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.Redirect("/index.html"); return; }
        var role = ctx.User.FindFirstValue(ClaimTypes.Role);
        if (need == "Admin" && role != "Admin" && role != "SuperAdmin") { ctx.Response.Redirect("/Pages/homepage.html"); return; }
        if (need == "SuperAdmin" && role != "SuperAdmin") { ctx.Response.Redirect("/Pages/adminhomepage.html"); return; }
        if (need == "Customer" && role != "Customer") { ctx.Response.Redirect(role == "SuperAdmin" || role == "Admin" ? "/Pages/adminhomepage.html" : "/Pages/homepage.html"); return; }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

IResult Bad(string m) => Results.BadRequest(new { error = m });
IResult Conflict(string m) => Results.Conflict(new { error = m });
int Uid(ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
// A plain Admin may only act on Customers (or on themselves); only a SuperAdmin may touch other Admins / SuperAdmins.
async Task<bool> CanManage(MySqlConnection c, ClaimsPrincipal u, int targetId)
{
    if (u.FindFirstValue(ClaimTypes.Role) == "SuperAdmin" || targetId == Uid(u)) return true;
    return await c.ExecuteScalarAsync<string>("SELECT Role FROM Users WHERE UserId=@targetId", new { targetId }) == "Customer";
}
IResult Denied() => Results.Json(new { error = "Only a SuperAdmin can do that to an admin account." }, statusCode: 403);
// Input limits: usernames and passwords are capped at 14 characters, emails at 50, so nobody can paste a huge value to overload the server.
const int MaxName = 14, MaxPass = 14, MaxEmail = 50;
bool PwOk(string? p) => p != null && p.Length >= 8 && p.Length <= MaxPass;
const string PwMsg = "Password must be 8 to 14 characters.";
string? NormPhone(string? raw)
{
    var d = Regex.Replace(raw ?? "", @"[^\d+]", ""); if (d.StartsWith("+63")) d = "0" + d[3..]; else if (d.StartsWith("63") && d.Length == 12) d = "0" + d[2..];
    return Regex.IsMatch(d, @"^09\d{9}$") ? d : null;
}
bool BadImageExt(string ext) => ext is not (".jpg" or ".jpeg" or ".png" or ".webp");

var auth = app.MapGroup("/api/auth");
var tries = new ConcurrentDictionary<string, List<DateTime>>();

auth.MapPost("/register", async (RegisterReq r, Db db) =>
{
    var username = (r.Username ?? "").Trim(); var email = (r.Email ?? "").Trim();
    if (!Regex.IsMatch(username, @"^[A-Za-z0-9_.-]{3,14}$")) return Bad("Username must be 3 to 14 letters, numbers, dots, dashes or underscores.");
    if (email.Length > MaxEmail || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Bad("Enter a valid email address (up to 50 characters).");
    if (!PwOk(r.Password)) return Bad(PwMsg);
    using var c = db.Open();
    var taken = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@username OR Email=@email", new { username, email });
    if (taken > 0) return Conflict("That username or email is already registered.");
    await c.ExecuteAsync("INSERT INTO Users(Username,Email,PasswordHash,Role) VALUES(@username,@email,@h,'Customer')", new { username, email, h = Pw.Hash(r.Password!) });
    return Results.Ok(new { ok = true });
});

auth.MapPost("/login", async (LoginReq r, Db db, HttpContext ctx) =>
{
    // reject oversized input before doing any work (the password check is deliberately slow, so huge inputs must never reach it)
    if ((r.Login ?? "").Length > MaxEmail || (r.Password ?? "").Length > 64) return Results.Json(new { error = "Invalid credentials. Please try again." }, statusCode: 401);
    var key = ctx.Connection.RemoteIpAddress + "|" + (r.Login ?? "").ToLowerInvariant();
    var list = tries.GetOrAdd(key, _ => new List<DateTime>());
    lock (list)
    {
        list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-10));
        list.Add(DateTime.UtcNow);
        if (list.Count > 8) return Results.Json(new { error = "Too many attempts. Try again in a few minutes." }, statusCode: 429);
    }
    using var c = db.Open();
    var u = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId,Username,Email,PasswordHash,Role,Status,SessionVersion,TwoFactorEnabled FROM Users WHERE Username=@l OR Email=@l LIMIT 1", new { l = (r.Login ?? "").Trim() });
    if (u is null || !Pw.Verify(r.Password ?? "", u.PasswordHash)) return Results.Json(new { error = "Invalid credentials. Please try again." }, statusCode: 401);
    if (u.Status != "Active") return Results.Json(new { error = "This account is suspended. Please contact the store admin." }, statusCode: 403);

    if (u.TwoFactorEnabled)
    {
        var prot = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("syscle.2fa").ToTimeLimitedDataProtector();
        return Results.Ok(new { require2fa = true, ticket = prot.Protect(u.UserId.ToString(), TimeSpan.FromMinutes(5)) });
    }

    await c.ExecuteAsync("UPDATE Users SET SessionVersion = SessionVersion + 1 WHERE UserId=@id", new { id = u.UserId });
    var newVer = await c.ExecuteScalarAsync<int>("SELECT SessionVersion FROM Users WHERE UserId=@id", new { id = u.UserId });
    var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, u.UserId.ToString()), new(ClaimTypes.Name, u.Username), new(ClaimTypes.Role, u.Role), new("ver", newVer.ToString()) };
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { username = u.Username, role = u.Role, redirect = (u.Role == "Admin" || u.Role == "SuperAdmin") ? "Pages/adminhomepage.html" : "Pages/homepage.html" });
});

auth.MapPost("/login-2fa", async (Verify2FAReq r, Db db, HttpContext ctx) =>
{
    // The ticket is only handed out after the password was verified, so the 2FA step cannot be used on its own.
    int userId;
    try { userId = int.Parse(ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("syscle.2fa").ToTimeLimitedDataProtector().Unprotect(r.Ticket ?? "")); }
    catch { return Results.Json(new { error = "Your sign-in expired. Please enter your password again." }, statusCode: 401); }
    var key = "2fa|" + userId; var list = tries.GetOrAdd(key, _ => new List<DateTime>());
    lock (list)
    {
        list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-10)); list.Add(DateTime.UtcNow);
        if (list.Count > 8) return Results.Json(new { error = "Too many attempts. Try again in a few minutes." }, statusCode: 429);
    }
    using var c = db.Open();
    var u = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId,Username,Role,Status,SessionVersion,TwoFactorSecret,TwoFactorEnabled FROM Users WHERE UserId=@id", new { id = userId });
    if (u is null || !u.TwoFactorEnabled || string.IsNullOrEmpty(u.TwoFactorSecret)) return Bad("Invalid request.");
    if (u.Status != "Active") return Results.Json(new { error = "This account is suspended. Please contact the store admin." }, statusCode: 403);
    var totp = new Totp(Base32Encoding.ToBytes(u.TwoFactorSecret));
    if (!totp.VerifyTotp((r.Code ?? "").Trim(), out _, new VerificationWindow(1, 1))) return Bad("Invalid 2FA code.");

    await c.ExecuteAsync("UPDATE Users SET SessionVersion = SessionVersion + 1 WHERE UserId=@id", new { id = u.UserId });
    var newVer = await c.ExecuteScalarAsync<int>("SELECT SessionVersion FROM Users WHERE UserId=@id", new { id = u.UserId });
    var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, u.UserId.ToString()), new(ClaimTypes.Name, u.Username), new(ClaimTypes.Role, u.Role), new("ver", newVer.ToString()) };
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { username = u.Username, role = u.Role, redirect = (u.Role == "Admin" || u.Role == "SuperAdmin") ? "Pages/adminhomepage.html" : "Pages/homepage.html" });
});

auth.MapPost("/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Ok(new { ok = true }); });

auth.MapPost("/forgot", async (ForgotReq r, Db db, EmailService emailer, HttpContext ctx, IWebHostEnvironment env) =>
{
    var email = (r.Email ?? "").Trim().ToLowerInvariant();
    if (email.Length > MaxEmail) return Results.Ok(new { message = "If that email has an account, a password reset link has been sent. It works for 30 minutes." });
    var key = "forgot|" + email; var list = tries.GetOrAdd(key, _ => new List<DateTime>());
    lock (list)
    {
        list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-10)); list.Add(DateTime.UtcNow);
        if (list.Count > 5) return Results.Json(new { error = "Too many requests. Try again in a few minutes." }, statusCode: 429);
    }
    var msg = "If that email has an account, a password reset link has been sent. It works for 30 minutes.";
    using var c = db.Open();
    var user = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId, Username, Email, PasswordHash FROM Users WHERE Email=@email AND Status='Active'", new { email });
    if (user is null) return Results.Ok(new { message = msg });
    // The password is NOT changed here. The person must open the link and choose a new one.
    var link = $"{ctx.Request.Scheme}://{ctx.Request.Host}/reset.html?token={Uri.EscapeDataString(ResetToken.Make(ctx.RequestServices, user))}";
    var sent = false;
    try { sent = await emailer.SendPasswordResetAsync(user.Email, user.Username, link); } catch (Exception ex) { Console.WriteLine("[Syscle] Reset email failed: " + ex.Message); }
    if (!sent)
    {
        Console.WriteLine("[Syscle] Email is not set up. Reset link for " + user.Email + ": " + link);
        if (env.IsDevelopment()) return Results.Ok(new { message = "Email is not set up on this server, so here is your reset link instead.", devLink = link });
    }
    return Results.Ok(new { message = msg });
});

auth.MapPost("/reset", async (ResetReq r, Db db, HttpContext ctx) =>
{
    if (!PwOk(r.Password)) return Bad(PwMsg);
    var t = ResetToken.Read(ctx.RequestServices, r.Token);
    if (t is null) return Bad("This reset link is invalid or has expired. Please request a new one.");
    using var c = db.Open();
    var u = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId, PasswordHash, Status FROM Users WHERE UserId=@id", new { id = t.Value.id });
    if (u is null || u.Status != "Active" || ResetToken.Print(u.PasswordHash) != t.Value.print) return Bad("This reset link is invalid or has already been used. Please request a new one.");
    await c.ExecuteAsync("UPDATE Users SET PasswordHash=@h, SessionVersion=SessionVersion+1 WHERE UserId=@id", new { h = Pw.Hash(r.Password!), id = u.UserId });
    return Results.Ok(new { ok = true });
});

auth.MapGet("/me", async (ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open();
    return Results.Ok(await c.QueryFirstAsync("SELECT UserId AS userId, Username AS username, Email AS email, IFNULL(Phone,'') AS phone, Role AS role, TwoFactorEnabled AS twoFactorEnabled FROM Users WHERE UserId=@id", new { id = Uid(u) }));
}).RequireAuthorization();

auth.MapGet("/ping", () => Results.Ok(new { ok = true })).RequireAuthorization();

var me = app.MapGroup("/api/me").RequireAuthorization();
me.MapPut("/profile", async (ProfileReq r, ClaimsPrincipal u, Db db) =>
{
    var email = (r.Email ?? "").Trim(); var phone = Nz(r.Phone);
    if (email.Length > MaxEmail || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Bad("Enter a valid email address (up to 50 characters).");
    if (phone != null) { phone = NormPhone(phone); if (phone is null) return Bad("Enter a valid mobile number, like 09171234567."); }
    using var c = db.Open();
    if (phone != null && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Phone=@phone AND UserId<>@id", new { phone, id = Uid(u) }) > 0) return Conflict("That mobile number is already used by another account.");
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Email=@email AND UserId<>@id", new { email, id = Uid(u) }) > 0) return Conflict("That email is already used by another account.");
    await c.ExecuteAsync("UPDATE Users SET Email=@email, Phone=@phone WHERE UserId=@id", new { email, phone, id = Uid(u) });
    return Results.Ok(new { ok = true });
});
me.MapPost("/password", async (PasswordReq r, ClaimsPrincipal u, Db db) =>
{
    if (!PwOk(r.Next) || (r.Current ?? "").Length > 64) return Bad(PwMsg);
    using var c = db.Open();
    var hash = await c.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (hash is null || !Pw.Verify(r.Current ?? "", hash)) return Bad("Your current password is not correct.");
    await c.ExecuteAsync("UPDATE Users SET PasswordHash=@h WHERE UserId=@id", new { h = Pw.Hash(r.Next!), id = Uid(u) });
    return Results.Ok(new { ok = true });
});

me.MapGet("/2fa/setup", async (ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open();
    var user = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT Username, TwoFactorEnabled FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (user is null) return Results.NotFound(new { error = "Not found." });
    if (user.TwoFactorEnabled) return Conflict("2FA is already enabled.");
    var key = KeyGeneration.GenerateRandomKey(20);
    var base32Secret = Base32Encoding.ToString(key);
    await c.ExecuteAsync("UPDATE Users SET TwoFactorSecret=@s WHERE UserId=@id", new { s = base32Secret, id = Uid(u) });
    var uri = $"otpauth://totp/Web-Syscle:{user.Username}?secret={base32Secret}&issuer=Web-Syscle";
    using var qrGenerator = new QRCodeGenerator();
    using var qrCodeData = qrGenerator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
    using var qrCode = new PngByteQRCode(qrCodeData);
    return Results.Ok(new { secret = base32Secret, qrCodeImage = Convert.ToBase64String(qrCode.GetGraphic(20)) });
});
me.MapPost("/2fa/enable", async (Enable2FAReq r, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open();
    var secret = await c.ExecuteScalarAsync<string>("SELECT TwoFactorSecret FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (string.IsNullOrEmpty(secret)) return Bad("Generate a 2FA secret first.");
    var totp = new Totp(Base32Encoding.ToBytes(secret));
    if (!totp.VerifyTotp((r.Code ?? "").Trim(), out _, new VerificationWindow(1, 1))) return Bad("Invalid 2FA code.");
    await c.ExecuteAsync("UPDATE Users SET TwoFactorEnabled=1 WHERE UserId=@id", new { id = Uid(u) });
    return Results.Ok(new { ok = true });
});

me.MapPost("/2fa/disable", async (Disable2FAReq r, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open();
    var row = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT PasswordHash, TwoFactorSecret, TwoFactorEnabled FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (row is null || !row.TwoFactorEnabled || string.IsNullOrEmpty(row.TwoFactorSecret)) return Bad("2FA is not turned on.");
    if (!Pw.Verify(r.Password ?? "", row.PasswordHash)) return Bad("Your password is not correct.");
    if (!new Totp(Base32Encoding.ToBytes(row.TwoFactorSecret)).VerifyTotp((r.Code ?? "").Trim(), out _, new VerificationWindow(1, 1))) return Bad("Invalid 2FA code.");
    await c.ExecuteAsync("UPDATE Users SET TwoFactorEnabled=0, TwoFactorSecret=NULL WHERE UserId=@id", new { id = Uid(u) });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/categories", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT CategoryId AS id, Name AS name, ImagePath AS image FROM Categories ORDER BY CategoryId")); });

app.MapGet("/api/products", async (string? category, Db db) =>
{
    using var c = db.Open();
    var prods = (await c.QueryAsync<ProductRow>(@"SELECT p.ProductId AS id, p.Name AS name, p.ItemType AS itemType, p.Price AS price, p.Stock AS stock, p.ImagePath AS image, p.Sizes AS sizes, c.Name AS category FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId WHERE (@category IS NULL OR c.Name = @category) ORDER BY p.Name", new { category = Nz(category) })).ToList();
    var allVariants = (await c.QueryAsync<VariantRow>(@"SELECT VariantId AS id, ProductId AS productId, ColorName AS colorName, ImagePath AS image FROM ProductVariants")).ToList();
    foreach (var p in prods) p.variants = allVariants.Where(v => v.productId == p.id).ToList();
    return Results.Ok(prods);
});

app.MapGet("/api/products/{id:int}/reviews", async (int id, Db db) =>
{
    using var c = db.Open();
    return Results.Ok(await c.QueryAsync("SELECT r.ReviewId AS id, r.Rating AS rating, r.Comment AS comment, r.CreatedAt AS date, u.Username AS username FROM Reviews r JOIN Users u ON u.UserId = r.UserId WHERE r.ProductId=@id ORDER BY r.ReviewId DESC", new { id }));
});

app.MapGet("/api/bundles", async (Db db, IConfiguration cfg) => { using var c = db.Open(); await Bundles.RefreshIfStale(c, cfg.GetValue("Bundles:SlowDays", 30), false); return Results.Ok(await Bundles.ListAsync(c, true)); });
app.MapGet("/api/settings/payment-qr", async (Db db) => { using var c = db.Open(); var show = await c.ExecuteScalarAsync<string>("SELECT SettingValue FROM Settings WHERE SettingKey='ShowPaymentQr'"); var img = await c.ExecuteScalarAsync<string>("SELECT SettingValue FROM Settings WHERE SettingKey='PaymentQrImage'"); return Results.Ok(new { show = show == "1", image = img }); });

var shop = app.MapGroup("/api").RequireAuthorization("Customer");
shop.MapPost("/products/{id:int}/reviews", async (int id, ReviewReq r, ClaimsPrincipal u, Db db) =>
{
    if (r.Rating < 1 || r.Rating > 5) return Bad("Rating must be 1-5.");
    if ((r.Comment ?? "").Length > 1000) return Bad("Review is too long (1000 characters max).");
    using var c = db.Open();
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Products WHERE ProductId=@id", new { id }) == 0) return Results.NotFound(new { error = "Product not found." });
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Reviews WHERE ProductId=@id AND UserId=@uid", new { id, uid = Uid(u) }) > 0) return Conflict("You already reviewed this item.");
    await c.ExecuteAsync("INSERT INTO Reviews(ProductId, UserId, Rating, Comment) VALUES(@id, @uid, @rating, @comment)", new { id, uid = Uid(u), r.Rating, r.Comment });
    return Results.Ok(new { ok = true });
});

shop.MapGet("/cart", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync(@"SELECT p.ProductId AS productId, p.Name AS name, p.ItemType AS itemType, p.Price AS price, p.Stock AS stock, p.ImagePath AS image, ci.Size AS size, ci.Color AS color, ci.Qty AS qty FROM CartItems ci JOIN Products p ON p.ProductId = ci.ProductId WHERE ci.UserId=@uid ORDER BY ci.AddedAt", new { uid = Uid(u) })); });

shop.MapPost("/cart", async (CartReq r, ClaimsPrincipal u, Db db) =>
{
    if (r.Qty < 1 || r.Qty > 999) return Bad("Invalid quantity.");
    using var c = db.Open();
    var prod = await c.QueryFirstOrDefaultAsync<SizedProduct>("SELECT Stock, Sizes FROM Products WHERE ProductId=@id", new { id = r.ProductId });
    if (prod is null) return Results.NotFound(new { error = "Not available." });
    var size = Sizing.Check(prod.Sizes, r.Size);
    if (size is null) return Bad("Please choose a size.");
    var uid = Uid(u); var color = (r.Color ?? "").Trim();
    var have = await c.ExecuteScalarAsync<int?>("SELECT IFNULL(SUM(Qty),0) FROM CartItems WHERE UserId=@uid AND ProductId=@id", new { uid, id = r.ProductId }) ?? 0;
    if (have + r.Qty > prod.Stock) return Conflict($"Only {prod.Stock} in stock.");
    await c.ExecuteAsync("INSERT INTO CartItems(UserId,ProductId,Size,Color,Qty) VALUES(@uid,@id,@size,@color,@q) ON DUPLICATE KEY UPDATE Qty = Qty + @q", new { uid, id = r.ProductId, size, color, q = r.Qty });
    return Results.Ok(new { ok = true });
});

shop.MapPut("/cart/{productId:int}", async (int productId, QtyReq r, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open(); var uid = Uid(u); var size = (r.Size ?? "").Trim(); var color = (r.Color ?? "").Trim();
    if (r.Qty <= 0) { await c.ExecuteAsync("DELETE FROM CartItems WHERE UserId=@uid AND ProductId=@productId AND Size=@size AND Color=@color", new { uid, productId, size, color }); return Results.Ok(new { ok = true }); }
    var stock = await c.ExecuteScalarAsync<int?>("SELECT Stock FROM Products WHERE ProductId=@productId", new { productId });
    var others = await c.ExecuteScalarAsync<int?>("SELECT IFNULL(SUM(Qty),0) FROM CartItems WHERE UserId=@uid AND ProductId=@productId AND (Size<>@size OR Color<>@color)", new { uid, productId, size, color }) ?? 0;
    if (others + r.Qty > stock) return Conflict($"Only {stock} in stock.");
    await c.ExecuteAsync("UPDATE CartItems SET Qty=@qty WHERE UserId=@uid AND ProductId=@productId AND Size=@size AND Color=@color", new { qty = r.Qty, uid, productId, size, color });
    return Results.Ok(new { ok = true });
});

shop.MapPost("/cart/bundle", async (BundleAddReq r, ClaimsPrincipal u, Db db) =>
{
    if (r.Qty < 1 || r.Qty > 20) return Bad("Invalid quantity.");
    using var c = db.Open(); using var tx = c.BeginTransaction(); var uid = Uid(u);
    var items = (await c.QueryAsync<BundleStock>("SELECT bi.ProductId, bi.Qty, p.Stock, p.Name, p.Sizes FROM BundleItems bi JOIN Products p ON p.ProductId = bi.ProductId WHERE bi.BundleId=@id", new { id = r.BundleId }, tx)).ToList();
    var chosen = new Dictionary<int, string>();
    foreach (var i in items)
    {
        string? pick = null; r.Sizes?.TryGetValue(i.ProductId.ToString(), out pick);
        var size = Sizing.Check(i.Sizes, pick); if (size is null) return Bad($"Choose size for {i.Name}.");
        chosen[i.ProductId] = size;
        var have = await c.ExecuteScalarAsync<int?>("SELECT IFNULL(SUM(Qty),0) FROM CartItems WHERE UserId=@uid AND ProductId=@pid", new { uid, pid = i.ProductId }, tx) ?? 0;
        if (have + i.Qty * r.Qty > i.Stock) return Conflict($"Not enough stock for {i.Name}.");
    }
    foreach (var i in items) await c.ExecuteAsync("INSERT INTO CartItems(UserId,ProductId,Size,Color,Qty) VALUES(@uid,@pid,@size,'',@q) ON DUPLICATE KEY UPDATE Qty = Qty + @q", new { uid, pid = i.ProductId, size = chosen[i.ProductId], q = i.Qty * r.Qty }, tx);
    await c.ExecuteAsync("INSERT INTO CartBundles(UserId,BundleId,Qty) VALUES(@uid,@bid,@q) ON DUPLICATE KEY UPDATE Qty = Qty + @q", new { uid, bid = r.BundleId, q = r.Qty }, tx);
    tx.Commit(); return Results.Ok(new { ok = true });
});

shop.MapGet("/cart/bundles", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); var (discount, applied) = await Bundles.ForCart(c, Uid(u), null); return Results.Ok(new { discount, applied }); });
shop.MapPost("/promo", async (PromoReq r, Db db) => { using var c = db.Open(); var pct = await c.ExecuteScalarAsync<int?>("SELECT DiscountPercent FROM PromoCodes WHERE Code=@code AND IsActive=1", new { code = (r.Code ?? "").Trim() }); return pct is null ? Results.NotFound(new { error = "Invalid code." }) : Results.Ok(new { percent = pct }); });
shop.MapGet("/payment-methods", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT MethodCode AS code, Label AS label, AccountNumber AS accountNumber, AccountName AS accountName FROM PaymentMethods WHERE IsOn=1")); });

shop.MapPost("/checkout", async (CheckoutReq r, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open(); using var tx = c.BeginTransaction(); var uid = Uid(u);
    var lines = (await c.QueryAsync<Line>("SELECT p.ProductId, p.Name, p.Price, ci.Qty, ci.Size, ci.Color FROM CartItems ci JOIN Products p ON p.ProductId=ci.ProductId WHERE ci.UserId=@uid", new { uid }, tx)).ToList();
    if (lines.Count == 0) return Bad("Cart empty.");
    var method = await c.QueryFirstOrDefaultAsync<string>("SELECT Label FROM PaymentMethods WHERE MethodCode=@m AND IsOn=1", new { m = r.PaymentMethod }, tx);
    if (method is null) return Bad("Invalid payment.");
    // GCash: the customer pays the store's GCash number first, then types the reference number from their GCash receipt. The admin checks it before completing the order.
    string? payRef = null;
    if (r.PaymentMethod == "gcash")
    {
        payRef = Regex.Replace((r.PaymentRef ?? "").Trim(), @"\s", "");
        if (!Regex.IsMatch(payRef, @"^[A-Za-z0-9]{8,20}$")) return Bad("Enter the GCash reference number from your receipt (8 to 20 letters or numbers).");
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE PaymentRef=@payRef AND Status<>'Cancelled'", new { payRef }, tx) > 0) return Conflict("That GCash reference number was already used for another order.");
    }
    if (r.PickupDate is null || r.PickupDate.Value.Date < DateTime.Today || r.PickupDate.Value.Date > DateTime.Today.AddDays(30)) return Bad("Invalid date.");
    var pct = 0; string? code = null;
    if (!string.IsNullOrWhiteSpace(r.PromoCode)) { code = r.PromoCode.Trim(); var found = await c.ExecuteScalarAsync<int?>("SELECT DiscountPercent FROM PromoCodes WHERE Code=@code AND IsActive=1", new { code }, tx); if (found is null) return Bad("Invalid promo."); pct = found.Value; }
    var sub = lines.Sum(l => l.Price * l.Qty); var (bundleDiscount, _) = await Bundles.ForCart(c, uid, tx); var discount = Math.Round((sub - bundleDiscount) * pct / 100m, 2); var total = sub - bundleDiscount - discount;
    var name = await c.ExecuteScalarAsync<string>("SELECT Username FROM Users WHERE UserId=@uid", new { uid }, tx);
    await c.ExecuteAsync(@"INSERT INTO Orders(UserId,CustomerName,Channel,PaymentMethod,PaymentRef,PromoCode,Discount,BundleDiscount,Total,Status,PickupDate) VALUES(@uid,@name,'Online',@method,@payRef,@code,@discount,@bundleDiscount,@total,'Pending',@pickup)", new { uid, name, method, payRef, code, discount, bundleDiscount, total, pickup = r.PickupDate.Value.Date }, tx);
    var orderId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)", null, tx);
    var soldOut = await Orders.TakeStock(c, tx, orderId, lines);
    if (soldOut != null) return Conflict($"Out of stock: {soldOut}");
    await c.ExecuteAsync("DELETE FROM CartItems WHERE UserId=@uid", new { uid }, tx); await c.ExecuteAsync("DELETE FROM CartBundles WHERE UserId=@uid", new { uid }, tx);
    var orderCode = "ORD-" + orderId.ToString("D6"); tx.Commit(); return Results.Ok(new { orderCode, total });
});

shop.MapGet("/orders", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync(@"SELECT o.OrderId AS id, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS code, o.CreatedAt AS date, o.PickupDate AS pickup, o.PaymentMethod AS payment, o.Total AS total, o.Status AS status, (SELECT GROUP_CONCAT(CONCAT(oi.ProductName, IF(oi.Size <> '', CONCAT(' (', oi.Size, ')'), ''), IF(oi.Color <> '', CONCAT(' [', oi.Color, ']'), ''), ' x', oi.Qty) SEPARATOR ', ') FROM OrderItems oi WHERE oi.OrderId = o.OrderId) AS items FROM Orders o WHERE o.UserId=@uid ORDER BY o.OrderId DESC", new { uid = Uid(u) })); });
shop.MapPost("/orders/{id:int}/cancel", async (int id, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var st = await c.QueryFirstOrDefaultAsync<string>("SELECT Status FROM Orders WHERE OrderId=@id AND UserId=@uid FOR UPDATE", new { id, uid = Uid(u) }, tx);
    if (st is null) return Results.NotFound(new { error = "Not found." }); if (st != "Pending") return Conflict("Can only cancel pending.");
    await c.ExecuteAsync("UPDATE Products p JOIN (SELECT ProductId, SUM(Qty) AS q FROM OrderItems WHERE OrderId=@id AND ProductId IS NOT NULL GROUP BY ProductId) x ON x.ProductId = p.ProductId SET p.Stock = p.Stock + x.q WHERE 1=1", new { id }, tx);
    await c.ExecuteAsync("UPDATE Orders SET Status='Cancelled' WHERE OrderId=@id", new { id }, tx); tx.Commit(); return Results.Ok(new { ok = true });
});

var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");


// ---------- store info (shown in the help window; edited by admins in Settings) ----------
string[] StoreKeys = { "StoreName", "StoreAddress", "StorePhone", "StoreHours", "PickupNote" };
async Task<Dictionary<string, string>> StoreInfo(MySqlConnection c)
{
    var rows = await c.QueryAsync<KV>("SELECT SettingKey AS K, IFNULL(SettingValue,'') AS V FROM Settings WHERE SettingKey IN @keys", new { keys = StoreKeys });
    var d = StoreKeys.ToDictionary(k => k, _ => ""); foreach (var row in rows) d[row.K] = row.V; if (d["StoreName"] == "") d["StoreName"] = "Web-Syscle"; return d;
}
app.MapGet("/api/store-info", async (Db db) => { using var c = db.Open(); return Results.Ok(await StoreInfo(c)); });

// ---------- SMS recovery: forgot password with a mobile number ----------
auth.MapPost("/forgot-sms", async (SmsForgotReq r, Db db, SmsService sms, HttpContext ctx, IWebHostEnvironment env) =>
{
    var phone = NormPhone(r.Phone); if (phone is null) return Bad("Enter your mobile number, like 09171234567.");
    var key = "sms|" + phone; var list = tries.GetOrAdd(key, _ => new List<DateTime>());
    lock (list)
    {
        list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-10)); list.Add(DateTime.UtcNow);
        if (list.Count > 3) return Results.Json(new { error = "Too many requests. Try again in a few minutes." }, statusCode: 429);
    }
    var prot = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("syscle.sms").ToTimeLimitedDataProtector();
    var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    using var c = db.Open();
    var user = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId, PasswordHash FROM Users WHERE Phone=@phone AND Status='Active' LIMIT 1", new { phone });
    // the same answer is given whether or not the number exists, so nobody can use this form to find out who is registered
    var ticket = prot.Protect((user?.UserId ?? 0) + "|" + code + "|" + (user is null ? "-" : ResetToken.Print(user.PasswordHash)), TimeSpan.FromMinutes(10));
    var msg = "If that number is registered, a 6-digit code was sent by SMS. It works for 10 minutes.";
    if (user is null) return Results.Ok(new { message = msg, ticket });
    var sent = false;
    try { sent = await sms.SendAsync(phone, $"Web-Syscle code: {code}. Valid for 10 minutes. Do not share it with anyone."); } catch (Exception ex) { Console.WriteLine("[Syscle] SMS failed: " + ex.Message); }
    if (!sent)
    {
        Console.WriteLine($"[Syscle] SMS is not set up. Recovery code for {phone}: {code}");
        if (env.IsDevelopment()) return Results.Ok(new { message = "SMS is not set up on this server, so here is your test code instead: " + code, ticket, devCode = code });
    }
    return Results.Ok(new { message = msg, ticket });
});
auth.MapPost("/reset-sms", async (SmsResetReq r, Db db, HttpContext ctx) =>
{
    if (!PwOk(r.Password)) return Bad(PwMsg);
    string[] p;
    try { p = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("syscle.sms").ToTimeLimitedDataProtector().Unprotect(r.Ticket ?? "").Split('|'); }
    catch { return Bad("This code expired. Please request a new one."); }
    var uid = int.Parse(p[0]);
    var key = "smsverify|" + (r.Ticket ?? "").GetHashCode(); var list = tries.GetOrAdd(key, _ => new List<DateTime>());
    lock (list)
    {
        list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-10)); list.Add(DateTime.UtcNow);
        if (list.Count > 5) return Results.Json(new { error = "Too many wrong codes. Please request a new one." }, statusCode: 429);
    }
    if (uid == 0 || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes((r.Code ?? "").Trim()), System.Text.Encoding.UTF8.GetBytes(p[1]))) return Bad("That code is not correct.");
    using var c = db.Open();
    var u = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId, PasswordHash, Status FROM Users WHERE UserId=@uid", new { uid });
    if (u is null || u.Status != "Active" || ResetToken.Print(u.PasswordHash) != p[2]) return Bad("This code was already used. Please request a new one.");
    await c.ExecuteAsync("UPDATE Users SET PasswordHash=@h, SessionVersion=SessionVersion+1 WHERE UserId=@uid", new { h = Pw.Hash(r.Password!), uid });
    return Results.Ok(new { ok = true });
});

// ---------- customer <-> store chat ----------
me.MapGet("/prefs", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); return Results.Ok(new { emailNotify = await c.ExecuteScalarAsync<bool>("SELECT IFNULL(EmailNotify,1) FROM Users WHERE UserId=@id", new { id = Uid(u) }) }); });
me.MapPut("/prefs", async (PrefsReq r, ClaimsPrincipal u, Db db) => { using var c = db.Open(); await c.ExecuteAsync("UPDATE Users SET EmailNotify=@n WHERE UserId=@id", new { n = r.EmailNotify, id = Uid(u) }); return Results.Ok(new { ok = true }); });

var chat = app.MapGroup("/api/messages").RequireAuthorization("Customer");
chat.MapGet("", async (ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open();
    await c.ExecuteAsync("UPDATE Messages SET IsRead=1 WHERE ThreadUserId=@id AND Sender='Staff' AND IsRead=0", new { id = Uid(u) });
    return Results.Ok(await c.QueryAsync("SELECT MessageId AS id, Sender AS sender, Body AS body, CreatedAt AS date FROM Messages WHERE ThreadUserId=@id ORDER BY MessageId DESC LIMIT 100", new { id = Uid(u) }));
});
chat.MapGet("/unread", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); return Results.Ok(new { count = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Messages WHERE ThreadUserId=@id AND Sender='Staff' AND IsRead=0", new { id = Uid(u) }) }); });
chat.MapPost("", async (MessageReq r, ClaimsPrincipal u, Db db) =>
{
    var body = (r.Body ?? "").Trim(); if (body.Length == 0) return Bad("Type a message first."); if (body.Length > 500) return Bad("Messages can be up to 500 characters.");
    var key = "msg|" + Uid(u); var list = tries.GetOrAdd(key, _ => new List<DateTime>());
    lock (list) { list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-1)); list.Add(DateTime.UtcNow); if (list.Count > 10) return Results.Json(new { error = "You are sending messages too fast. Please wait a moment." }, statusCode: 429); }
    using var c = db.Open(); await c.ExecuteAsync("INSERT INTO Messages(ThreadUserId,Sender,Body) VALUES(@id,'Customer',@body)", new { id = Uid(u), body });
    return Results.Ok(new { ok = true });
});

admin.MapGet("/messages", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync(@"SELECT u.UserId AS userId, u.Username AS username, (SELECT m2.Body FROM Messages m2 WHERE m2.ThreadUserId=u.UserId ORDER BY m2.MessageId DESC LIMIT 1) AS last, MAX(m.CreatedAt) AS date, SUM(CASE WHEN m.Sender='Customer' AND m.IsRead=0 THEN 1 ELSE 0 END) AS unread FROM Messages m JOIN Users u ON u.UserId=m.ThreadUserId GROUP BY u.UserId, u.Username ORDER BY unread DESC, date DESC LIMIT 200")); });
admin.MapGet("/messages/unread", async (Db db) => { using var c = db.Open(); return Results.Ok(new { count = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Messages WHERE Sender='Customer' AND IsRead=0") }); });
admin.MapGet("/messages/{userId:int}", async (int userId, Db db) =>
{
    using var c = db.Open();
    await c.ExecuteAsync("UPDATE Messages SET IsRead=1 WHERE ThreadUserId=@userId AND Sender='Customer' AND IsRead=0", new { userId });
    return Results.Ok(await c.QueryAsync("SELECT MessageId AS id, Sender AS sender, Body AS body, CreatedAt AS date FROM Messages WHERE ThreadUserId=@userId ORDER BY MessageId DESC LIMIT 200", new { userId }));
});
admin.MapPost("/messages/{userId:int}", async (int userId, MessageReq r, Db db) =>
{
    var body = (r.Body ?? "").Trim(); if (body.Length == 0) return Bad("Type a reply first."); if (body.Length > 500) return Bad("Replies can be up to 500 characters.");
    using var c = db.Open(); if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE UserId=@userId AND Role='Customer'", new { userId }) == 0) return Results.NotFound(new { error = "Customer not found." });
    await c.ExecuteAsync("INSERT INTO Messages(ThreadUserId,Sender,Body,IsRead) VALUES(@userId,'Staff',@body,0)", new { userId, body }); return Results.Ok(new { ok = true });
});
admin.MapDelete("/messages/{userId:int}", async (int userId, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Messages WHERE ThreadUserId=@userId", new { userId }); return Results.Ok(new { ok = true }); });

admin.MapGet("/store-info", async (Db db, SmsService sms, EmailService mail) => { using var c = db.Open(); return Results.Ok(new { info = await StoreInfo(c), smsReady = sms.IsReady, emailReady = mail.IsReady }); });
admin.MapPut("/store-info", async (Dictionary<string, string?> r, Db db) =>
{
    using var c = db.Open();
    foreach (var k in StoreKeys) if (r.TryGetValue(k, out var v))
    {
        v = (v ?? "").Trim(); if (v.Length > 200) return Bad("Each field can be up to 200 characters.");
        await c.ExecuteAsync("INSERT INTO Settings(SettingKey,SettingValue) VALUES(@k,@v) ON DUPLICATE KEY UPDATE SettingValue=@v", new { k, v });
    }
    return Results.Ok(new { ok = true });
});

admin.MapPost("/settings/payment-qr", async (HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    var form = await req.ReadFormAsync(); var show = form["show"].ToString() == "true" ? "1" : "0"; await using var c = db.Open();
    await c.ExecuteAsync("UPDATE Settings SET SettingValue=@show WHERE SettingKey='ShowPaymentQr'", new { show });
    var file = form.Files["image"];
    if (file is { Length: > 0 })
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant(); if (BadImageExt(ext)) return Bad("Invalid file."); var dir = Path.Combine(env.WebRootPath, "Images"); Directory.CreateDirectory(dir); var fn = $"qr_code{ext}";
        await using (var fs = File.Create(Path.Combine(dir, fn))) await file.CopyToAsync(fs);
        await c.ExecuteAsync("UPDATE Settings SET SettingValue=@val WHERE SettingKey='PaymentQrImage'", new { val = "/Images/" + fn });
    }
    return Results.Ok(new { ok = true });
});

admin.MapPost("/categories", async (HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    var form = await req.ReadFormAsync(); var name = form["name"].ToString().Trim(); string? path = null; var file = form.Files["image"];
    if (file is { Length: > 0 }) { var ext = Path.GetExtension(file.FileName).ToLowerInvariant(); if (BadImageExt(ext)) return Bad("Invalid file."); var dir = Path.Combine(env.WebRootPath, "Icons_and_Buttons"); Directory.CreateDirectory(dir); var fn = $"cat_{Guid.NewGuid():N}{ext}"; await using (var fs = File.Create(Path.Combine(dir, fn))) await file.CopyToAsync(fs); path = "../Icons_and_Buttons/" + fn; }
    using var c = db.Open(); await c.ExecuteAsync("INSERT INTO Categories(Name,ImagePath) VALUES(@name,@path)", new { name, path }); return Results.Ok(new { ok = true });
});
admin.MapDelete("/categories/{id:int}", async (int id, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Categories WHERE CategoryId=@id", new { id }); return Results.Ok(new { ok = true }); });
admin.MapPost("/categories/{id:int}/image", async (int id, HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    using var c = db.Open(); var (path, error) = await Pics.SaveAsync(req, env.WebRootPath, "Icons_and_Buttons", "cat", 3_000_000);
    if (error != null) return Bad(error); await c.ExecuteAsync("UPDATE Categories SET ImagePath=@path WHERE CategoryId=@id", new { path, id }); return Results.Ok(new { ok = true });
});

admin.MapGet("/stats", async (Db db) =>
{
    using var c = db.Open();
    var orders = await c.QueryFirstAsync(@"SELECT IFNULL(SUM(CASE WHEN Status<>'Cancelled' AND DATE(CreatedAt)=CURDATE() THEN Total END),0) AS todaySales, IFNULL(SUM(CASE WHEN DATE(CreatedAt)=CURDATE() THEN 1 ELSE 0 END),0) AS todayOrders, IFNULL(SUM(CASE WHEN Status='Pending' THEN 1 ELSE 0 END),0) AS pendingOrders, IFNULL(SUM(CASE WHEN Status<>'Cancelled' THEN Total END),0) AS totalSales FROM Orders");
    var prods = await c.QueryFirstAsync(@"SELECT COUNT(*) AS products, IFNULL(SUM(CASE WHEN Stock>0 AND Stock<=5 THEN 1 ELSE 0 END),0) AS lowStock, IFNULL(SUM(CASE WHEN Stock=0 THEN 1 ELSE 0 END),0) AS outOfStock FROM Products");
    var customers = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Role='Customer'");
    var low = await c.QueryAsync("SELECT ProductId AS id, Name AS name, Stock AS stock, CategoryId AS categoryId FROM Products WHERE Stock<=5 ORDER BY Stock, Name LIMIT 6");
    var recent = await c.QueryAsync(@"SELECT CONCAT(IF(Channel='Walk-in','WLK-','ORD-'), LPAD(OrderId,6,'0')) AS code, CustomerName AS customer, Total AS total, Status AS status FROM Orders ORDER BY OrderId DESC LIMIT 6");
    return Results.Ok(new { orders, prods, customers, low, recent });
});

admin.MapGet("/bundles", async (Db db, IConfiguration cfg) => { using var c = db.Open(); return Results.Ok(new { slowDays = cfg.GetValue("Bundles:SlowDays", 30), bundles = await Bundles.ListAsync(c, false) }); });
admin.MapPost("/bundles/generate", async (Db db, IConfiguration cfg) => { using var c = db.Open(); return Results.Ok(new { made = await Bundles.GenerateAsync(c, cfg.GetValue("Bundles:SlowDays", 30)) }); });
admin.MapPost("/bundles", async (BundleNewReq r, Db db) =>
{
    // the same product listed twice means "2 of it" (Buy 1 Get 1); it is stored as ONE row with Qty 2
    var groups = (r.Products ?? new List<int>()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
    var totalQty = groups.Values.Sum();
    if (totalQty < 2) return Bad("Select at least 2 items (or use Make BOGO).");
    if (totalQty > 20) return Bad("Too many items in one bundle.");
    if (r.DiscountPercent < 1 || r.DiscountPercent > 100) return Bad("Discount must be 1-100.");
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var found = (await c.QueryAsync<BundleProd>("SELECT ProductId, Name FROM Products WHERE ProductId IN @ids", new { ids = groups.Keys.ToList() }, tx)).ToList();
    if (found.Count != groups.Count) return Bad("One of the selected products no longer exists.");
    var isBogo = groups.Count == 1 && totalQty == 2;
    if (!r.Force)
    {
        var sig = string.Join(",", groups.OrderBy(g => g.Key).Select(g => g.Key + "x" + g.Value));
        var existing = (await c.QueryAsync<BundleItemSig>("SELECT BundleId, ProductId, Qty FROM BundleItems", null, tx)).ToList();
        var same = existing.GroupBy(x => x.BundleId).Any(g => string.Join(",", g.OrderBy(x => x.ProductId).Select(x => x.ProductId + "x" + x.Qty)) == sig);
        if (same) return Results.Conflict(new { error = isBogo ? "A Buy 1 Get 1 for this item is already created." : "A bundle with these exact items already exists.", duplicate = true });
    }
    var name = Nz(r.Name) ?? (groups.Count == 1 ? (isBogo && r.DiscountPercent == 50 ? "Buy 1 Get 1: " : "Buy " + totalQty + " ") + found[0].Name : string.Join(" + ", found.Select(f => f.Name)));
    if (name.Length > 160) name = name[..160];
    await c.ExecuteAsync("INSERT INTO Bundles(Name,DiscountPercent,IsActive,AutoMade) VALUES(@name,@pct,1,0)", new { name, pct = r.DiscountPercent }, tx);
    var id = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)", null, tx);
    foreach (var g in groups) await c.ExecuteAsync("INSERT INTO BundleItems(BundleId,ProductId,Qty) VALUES(@id,@pid,@q)", new { id, pid = g.Key, q = g.Value }, tx);
    tx.Commit(); return Results.Ok(new { ok = true, id });
});
admin.MapPut("/bundles/{id:int}", async (int id, BundleEditReq r, Db db) =>
{
    using var c = db.Open(); var n = await c.ExecuteAsync("UPDATE Bundles SET DiscountPercent = IFNULL(@pct, DiscountPercent), IsActive = IFNULL(@act, IsActive) WHERE BundleId=@id", new { id, pct = r.DiscountPercent, act = r.IsActive });
    return n == 0 ? Results.NotFound(new { error = "Not found." }) : Results.Ok(new { ok = true });
});
admin.MapDelete("/bundles/{id:int}", async (int id, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Bundles WHERE BundleId=@id", new { id }); return Results.Ok(new { ok = true }); });

admin.MapGet("/products", async (Db db) =>
{
    using var c = db.Open();
    var prods = (await c.QueryAsync<ProductRow>(@"SELECT p.ProductId AS id, p.CategoryId AS categoryId, c.Name AS category, p.ItemType AS itemType, p.Name AS name, p.Price AS price, p.Stock AS stock, p.ImagePath AS image, p.Sizes AS sizes FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId ORDER BY c.Name, p.Name")).ToList();
    var allVariants = (await c.QueryAsync<VariantRow>(@"SELECT VariantId AS id, ProductId AS productId, ColorName AS colorName, ImagePath AS image FROM ProductVariants")).ToList();
    foreach (var p in prods) p.variants = allVariants.Where(v => v.productId == p.id).ToList();
    return Results.Ok(prods);
});
string? CheckProduct(ProductReq r) => string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100 ? "Invalid name." : string.IsNullOrWhiteSpace(r.ItemType) || r.ItemType.Trim().Length > 60 ? "Invalid item type." : r.Price < 0 ? "Invalid price." : r.Stock < 0 ? "Invalid stock." : null;
admin.MapPost("/products", async (ProductReq r, Db db) =>
{
    var err = CheckProduct(r); if (err != null) return Bad(err);
    using var c = db.Open();
    await c.ExecuteAsync("INSERT INTO Products(CategoryId,ItemType,Name,Price,Stock,Sizes) VALUES(@CategoryId,@t,@n,@Price,@Stock,@sz)", new { r.CategoryId, t = r.ItemType!.Trim(), n = r.Name!.Trim(), r.Price, r.Stock, sz = Sizing.Normalize(r.Sizes) });
    var newId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)");
    if (r.Variants != null) foreach (var v in r.Variants) await c.ExecuteAsync("INSERT INTO ProductVariants(ProductId,ColorName,ImagePath) VALUES(@newId,@color,@image)", new { newId, color = v.ColorName, image = v.ImagePath });
    return Results.Ok(new { ok = true, id = newId });
});
admin.MapPut("/products/{id:int}", async (int id, ProductReq r, Db db) =>
{
    var err = CheckProduct(r); if (err != null) return Bad(err);
    using var c = db.Open();
    await c.ExecuteAsync("UPDATE Products SET CategoryId=@CategoryId, ItemType=@t, Name=@n, Price=@Price, Stock=@Stock, Sizes=@sz WHERE ProductId=@id", new { id, r.CategoryId, t = r.ItemType!.Trim(), n = r.Name!.Trim(), r.Price, r.Stock, sz = Sizing.Normalize(r.Sizes) });
    await c.ExecuteAsync("DELETE FROM ProductVariants WHERE ProductId=@id", new { id });
    if (r.Variants != null) foreach (var v in r.Variants) await c.ExecuteAsync("INSERT INTO ProductVariants(ProductId,ColorName,ImagePath) VALUES(@id,@color,@image)", new { id, color = v.ColorName, image = v.ImagePath });
    return Results.Ok(new { ok = true });
});
admin.MapDelete("/products/{id:int}", async (int id, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Products WHERE ProductId=@id", new { id }); return Results.Ok(new { ok = true }); });
admin.MapPost("/products/{id:int}/stock", async (int id, StockReq r, Db db) => { using var c = db.Open(); await c.ExecuteAsync("UPDATE Products SET Stock=Stock+@d WHERE ProductId=@id AND Stock+@d>=0", new { d = r.Delta, id }); return Results.Ok(new { ok = true }); });
admin.MapPost("/products/{id:int}/image", async (int id, HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    using var c = db.Open(); var (path, error) = await Pics.SaveAsync(req, env.WebRootPath, "Products", "prod", 3_000_000);
    if (error != null) return Bad(error); await c.ExecuteAsync("UPDATE Products SET ImagePath=@p WHERE ProductId=@id", new { p = path, id }); return Results.Ok(new { ok = true });
});
admin.MapPost("/products/{id:int}/variant-image", async (int id, HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    using var c = db.Open(); var (path, error) = await Pics.SaveAsync(req, env.WebRootPath, "Products", "var", 3_000_000);
    if (error != null) return Bad(error); return Results.Ok(new { path });
});

admin.MapGet("/transactions", async (string? search, string? status, string? startDate, string? endDate, Db db) =>
{
    // the ALL DATES buttons send empty dates; binding "" straight to DateTime? made the server fail, so read them as text and parse here
    DateTime? fromDate = DateTime.TryParse(startDate, out var d1) ? d1.Date : null; DateTime? toDate = DateTime.TryParse(endDate, out var d2) ? d2.Date.AddDays(1) : null;
    if ((search ?? "").Length > 60) search = search![..60];
    status = (status == "Pending" || status == "Completed" || status == "Cancelled") ? status : null;
    using var c = db.Open();
    var p = new { search = Nz(search), like = "%" + Nz(search) + "%", status, startDate = fromDate, endDate = toDate };
    const string filter = @"(@search IS NULL OR CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) LIKE @like OR o.CustomerName LIKE @like) AND (@startDate IS NULL OR o.CreatedAt >= @startDate) AND (@endDate IS NULL OR o.CreatedAt < @endDate)";
    var rows = await c.QueryAsync(@"SELECT o.OrderId AS id, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS code, o.CreatedAt AS date, o.CustomerName AS customer, o.UserId AS userId, o.PaymentMethod AS payment, o.PaymentRef AS paymentRef, o.PickupDate AS pickup, o.Total AS total, o.Status AS status, (SELECT GROUP_CONCAT(CONCAT(oi.ProductName, IF(oi.Size <> '', CONCAT(' (', oi.Size, ')'), ''), IF(oi.Color <> '', CONCAT(' [', oi.Color, ']'), ''), ' x', oi.Qty) SEPARATOR ', ') FROM OrderItems oi WHERE oi.OrderId = o.OrderId) AS items FROM Orders o WHERE " + filter + " AND (@status IS NULL OR o.Status = @status) ORDER BY o.OrderId DESC", p);
    // the four counters follow the same date range and search as the list, so the numbers always match what is on screen
    var summary = await c.QueryFirstAsync(@"SELECT COUNT(*) AS total, IFNULL(SUM(CASE WHEN o.Status='Completed' THEN 1 ELSE 0 END),0) AS completed, IFNULL(SUM(CASE WHEN o.Status='Pending' THEN 1 ELSE 0 END),0) AS pending, IFNULL(SUM(CASE WHEN o.Status='Cancelled' THEN 1 ELSE 0 END),0) AS cancelled FROM Orders o WHERE " + filter, new { p.search, p.like, p.startDate, p.endDate });
    return Results.Ok(new { summary, rows });
});
admin.MapPost("/transactions/walkin", async (WalkinReq r, Db db) =>
{
    if (r.Items == null || r.Items.Count == 0) return Bad("Add items.");
    if (r.Items.Any(i => i.Qty < 1 || i.Qty > 999)) return Bad("Invalid quantity.");
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var method = await c.QueryFirstOrDefaultAsync<string>("SELECT Label FROM PaymentMethods WHERE MethodCode=@m AND IsOn=1", new { m = r.PaymentMethod }, tx);
    if (method is null) return Bad("Invalid payment.");
    var lines = new List<Line>();
    foreach (var g in r.Items.GroupBy(i => new { i.ProductId, Size = (i.Size ?? "").Trim(), Color = (i.Color ?? "").Trim() }))
    {
        var p = await c.QueryFirstOrDefaultAsync<Line>("SELECT ProductId, Name, Price, 0 AS Qty FROM Products WHERE ProductId=@id", new { id = g.Key.ProductId }, tx);
        if (p is null) return Results.NotFound(new { error = "A product in the sale no longer exists." });
        var csv = await c.ExecuteScalarAsync<string>("SELECT Sizes FROM Products WHERE ProductId=@id", new { id = g.Key.ProductId }, tx); var size = Sizing.Check(csv, g.Key.Size); if (size is null) return Bad($"Choose size for {p.Name}.");
        p.Size = size; p.Color = g.Key.Color; p.Qty = g.Sum(x => x.Qty); lines.Add(p);
    }
    var total = lines.Sum(l => l.Price * l.Qty); var cust = Nz(r.CustomerName) ?? "Walk-in";
    await c.ExecuteAsync(@"INSERT INTO Orders(UserId,CustomerName,Channel,PaymentMethod,Discount,Total,Status) VALUES(NULL,@cust,'Walk-in',@method,0,@total,'Completed')", new { cust, method, total }, tx);
    var orderId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)", null, tx);
    var soldOut = await Orders.TakeStock(c, tx, orderId, lines); if (soldOut != null) return Conflict($"Out of stock: {soldOut}");
    var orderCode = "WLK-" + orderId.ToString("D6"); tx.Commit(); return Results.Ok(new { orderCode, total });
});
admin.MapPut("/transactions/{id:int}/status", async (int id, StatusReq r, Db db, EmailService emailer) =>
{
    if (r.Status is not ("Completed" or "Cancelled")) return Bad("Unknown.");
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var cur = await c.QueryFirstOrDefaultAsync<string>("SELECT Status FROM Orders WHERE OrderId=@id FOR UPDATE", new { id }, tx);
    if (cur is null) return Results.NotFound(); if (cur == "Cancelled" || cur == "Completed") return Conflict("Finalized.");
    if (r.Status == "Cancelled") await c.ExecuteAsync("UPDATE Products p JOIN (SELECT ProductId, SUM(Qty) AS q FROM OrderItems WHERE OrderId=@id AND ProductId IS NOT NULL GROUP BY ProductId) x ON x.ProductId = p.ProductId SET p.Stock = p.Stock + x.q WHERE 1=1", new { id }, tx);
    await c.ExecuteAsync("UPDATE Orders SET Status=@s WHERE OrderId=@id", new { s = r.Status, id }, tx);
    var orderInfo = await c.QueryFirstOrDefaultAsync<OrderEmailInfo>("SELECT o.CustomerName, u.Email, IFNULL(u.EmailNotify,1) AS EmailNotify, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS code FROM Orders o LEFT JOIN Users u ON u.UserId = o.UserId WHERE o.OrderId=@id", new { id }, tx);
    tx.Commit();
    if (orderInfo != null && orderInfo.Email != null && orderInfo.EmailNotify) { try { await emailer.SendOrderStatusEmailAsync(orderInfo.Email, orderInfo.CustomerName!, orderInfo.Code!, r.Status); } catch { } }
    return Results.Ok(new { ok = true });
});

admin.MapDelete("/reviews/{id:int}", async (int id, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Reviews WHERE ReviewId=@id", new { id }); return Results.Ok(new { ok = true }); });
admin.MapGet("/reviews", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT r.ReviewId AS id, r.Rating AS rating, r.Comment AS comment, r.CreatedAt AS date, u.Username AS username, p.Name AS product FROM Reviews r JOIN Users u ON u.UserId=r.UserId JOIN Products p ON p.ProductId=r.ProductId ORDER BY r.ReviewId DESC")); });

admin.MapGet("/promos", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT Code AS code, DiscountPercent AS percent, IsActive AS isActive FROM PromoCodes ORDER BY Code")); });
admin.MapPost("/promos", async (PromoNewReq r, Db db) =>
{
    var code = (r.Code ?? "").Trim().ToUpperInvariant();
    if (!Regex.IsMatch(code, @"^[A-Z0-9_-]{3,30}$")) return Bad("Code must be 3-30 letters, numbers, dashes or underscores.");
    if (r.Percent < 1 || r.Percent > 90) return Bad("Discount must be 1-90%.");
    using var c = db.Open();
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PromoCodes WHERE Code=@code", new { code }) > 0) return Conflict("That code already exists.");
    await c.ExecuteAsync("INSERT INTO PromoCodes(Code,DiscountPercent,IsActive) VALUES(@code,@p,1)", new { code, p = r.Percent });
    return Results.Ok(new { ok = true });
});
admin.MapPut("/promos/{code}", async (string code, PromoEditReq r, Db db) =>
{
    if (r.Percent is < 1 or > 90) return Bad("Discount must be 1-90%.");
    using var c = db.Open();
    var n = await c.ExecuteAsync("UPDATE PromoCodes SET IsActive = IFNULL(@act, IsActive), DiscountPercent = IFNULL(@pct, DiscountPercent) WHERE Code=@code", new { code, act = r.IsActive, pct = r.Percent });
    return n == 0 ? Results.NotFound(new { error = "Not found." }) : Results.Ok(new { ok = true });
});
admin.MapDelete("/promos/{code}", async (string code, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM PromoCodes WHERE Code=@code", new { code }); return Results.Ok(new { ok = true }); });

admin.MapGet("/payment-methods", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT MethodCode AS code, Label AS label, IsOn AS isOn, IFNULL(AccountNumber,'') AS accountNumber, IFNULL(AccountName,'') AS accountName FROM PaymentMethods ORDER BY MethodCode")); });
admin.MapPut("/payment-methods", async (List<MethodReq> list, Db db) => { using var c = db.Open(); foreach (var m in list) await c.ExecuteAsync("UPDATE PaymentMethods SET IsOn=@IsOn, AccountNumber=@num, AccountName=@name WHERE MethodCode=@Code", new { m.Code, m.IsOn, num = Nz(m.AccountNumber), name = Nz(m.AccountName) }); return Results.Ok(new { ok = true }); });

admin.MapGet("/accounts", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync(@"SELECT UserId AS id, Username AS username, Email AS email, IFNULL(Phone,'') AS phone, Role AS role, Status AS status, IF(Status='Active' AND LastSeen > DATE_SUB(NOW(), INTERVAL 5 MINUTE), 1, 0) AS online, FpRequest AS fpRequest FROM Users ORDER BY Role, Username")); });
admin.MapPost("/accounts", async (NewAdminReq r, Db db) =>
{
    var username = (r.Username ?? "").Trim(); var email = (r.Email ?? "").Trim();
    if (!Regex.IsMatch(username, @"^[A-Za-z0-9_.-]{3,14}$")) return Bad("Username must be 3 to 14 letters, numbers, dots, dashes or underscores.");
    if (email.Length > MaxEmail || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Bad("Enter a valid email address (up to 50 characters).");
    if (!PwOk(r.Password)) return Bad(PwMsg);
    using var c = db.Open(); if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@username OR Email=@email", new { username, email }) > 0) return Conflict("Exists.");
    await c.ExecuteAsync("INSERT INTO Users(Username,Email,PasswordHash,Role) VALUES(@username,@email,@h,'Admin')", new { username, email, h = Pw.Hash(r.Password!) }); return Results.Ok(new { ok = true });
});
admin.MapPost("/accounts/{id:int}/role", async (int id, RoleReq r, ClaimsPrincipal u, Db db) =>
{
    if (u.FindFirstValue(ClaimTypes.Role) != "SuperAdmin") return Denied();
    if (r.Role is not ("Admin" or "Customer" or "SuperAdmin")) return Bad("Unknown role.");
    using var c = db.Open(); if (id == Uid(u)) return Bad("Can't change own role.");
    await c.ExecuteAsync("UPDATE Users SET Role=@r, SessionVersion=SessionVersion+1 WHERE UserId=@id", new { r = r.Role, id }); return Results.Ok(new { ok = true });
});
admin.MapPost("/accounts/{id:int}/suspend", async (int id, SuspendReq r, ClaimsPrincipal u, Db db) => { if (id == Uid(u)) return Bad("Can't suspend self."); using var c = db.Open(); if (!await CanManage(c, u, id)) return Denied(); await c.ExecuteAsync("UPDATE Users SET Status=@s, SessionVersion=SessionVersion+1 WHERE UserId=@id", new { s = r.Suspend ? "Suspended" : "Active", id }); return Results.Ok(new { ok = true }); });
admin.MapPost("/accounts/{id:int}/logout", async (int id, ClaimsPrincipal u, Db db) => { using var c = db.Open(); if (!await CanManage(c, u, id)) return Denied(); await c.ExecuteAsync("UPDATE Users SET SessionVersion=SessionVersion+1 WHERE UserId=@id", new { id }); return Results.Ok(new { ok = true }); });
admin.MapPost("/accounts/{id:int}/password", async (int id, PasswordSetReq r, ClaimsPrincipal u, Db db) => { if (!PwOk(r.Password)) return Bad(PwMsg); using var c = db.Open(); if (!await CanManage(c, u, id)) return Denied(); var bump = id == Uid(u) ? 0 : 1; await c.ExecuteAsync("UPDATE Users SET PasswordHash=@h, SessionVersion=SessionVersion+@bump WHERE UserId=@id", new { h = Pw.Hash(r.Password!), bump, id }); return Results.Ok(new { ok = true }); });
admin.MapDelete("/accounts/{id:int}", async (int id, ClaimsPrincipal u, Db db) => { if (id == Uid(u)) return Bad("Can't delete self."); using var c = db.Open(); if (!await CanManage(c, u, id)) return Denied(); await c.ExecuteAsync("DELETE FROM Users WHERE UserId=@id", new { id }); return Results.Ok(new { ok = true }); });

app.Run();

record RegisterReq(string? Username, string? Email, string? Password);
record LoginReq(string? Login, string? Password);
record Verify2FAReq(string? Ticket, string? Code);
record Enable2FAReq(string? Code);
record ForgotReq(string? Email);
record SmsForgotReq(string? Phone);
record SmsResetReq(string? Ticket, string? Code, string? Password);
record MessageReq(string? Body);
record PrefsReq(bool EmailNotify);
record ResetReq(string? Token, string? Password);
record Disable2FAReq(string? Password, string? Code);
record PromoNewReq(string? Code, int Percent);
record PromoEditReq(bool? IsActive, int? Percent);
record ProfileReq(string? Email, string? Phone);
record PasswordReq(string? Current, string? Next);
record ReviewReq(int Rating, string? Comment);
record VariantReq(string? ColorName, string? ImagePath);
record CartReq(int ProductId, int Qty, string? Size, string? Color);
record QtyReq(int Qty, string? Size, string? Color);
record PromoReq(string? Code);
record CheckoutReq(DateTime? PickupDate, string? PromoCode, string? PaymentMethod, string? PaymentRef = null);
record ItemReq(int ProductId, int Qty, string? Size, string? Color);
record WalkinReq(string? CustomerName, string? PaymentMethod, List<ItemReq>? Items);
record StatusReq(string? Status);
record ProductReq(int CategoryId, string? ItemType, string? Name, decimal Price, int Stock, string? Sizes, List<VariantReq>? Variants);
record StockReq(int Delta);
record MethodReq(string Code, bool IsOn, string? AccountNumber, string? AccountName);
record NewAdminReq(string? Username, string? Email, string? Password);
record SuspendReq(bool Suspend);
record PasswordSetReq(string? Password);
record RoleReq(string Role);
record BundleAddReq(int BundleId, int Qty, Dictionary<string, string>? Sizes);
record BundleNewReq(List<int>? Products, int DiscountPercent, string? Name, bool Force = false);
record BundleEditReq(int? DiscountPercent, bool? IsActive);

class UserRow { public int UserId { get; set; } public string Username { get; set; } = ""; public string PasswordHash { get; set; } = ""; public string Role { get; set; } = ""; public string Status { get; set; } = ""; public int SessionVersion { get; set; } public string Email { get; set; } = ""; public bool TwoFactorEnabled { get; set; } public string? TwoFactorSecret { get; set; } }
class KV { public string K { get; set; } = ""; public string V { get; set; } = ""; }
class SessionRow { public string Role { get; set; } = ""; public string Status { get; set; } = ""; public int SessionVersion { get; set; } }
class Line { public int ProductId { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } public int Qty { get; set; } public string Size { get; set; } = ""; public string Color { get; set; } = ""; }
class Db { private readonly string _cs; public Db(string cs) { _cs = cs; } public MySqlConnection Open() { var c = new MySqlConnection(_cs); c.Open(); return c; } }
class BundleRow { public int BundleId { get; set; } public string Name { get; set; } = ""; public int DiscountPercent { get; set; } public bool IsActive { get; set; } public bool AutoMade { get; set; } }
class BundleItemRow { public int BundleId { get; set; } public int ProductId { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } public int Stock { get; set; } public string? Image { get; set; } public int Qty { get; set; } public string? Sizes { get; set; } }
class BundleStock { public int ProductId { get; set; } public int Qty { get; set; } public int Stock { get; set; } public string Name { get; set; } = ""; public string? Sizes { get; set; } }
class BundleItemSig { public int BundleId { get; set; } public int ProductId { get; set; } public int Qty { get; set; } }
class BundleProd { public int ProductId { get; set; } public string Name { get; set; } = ""; }
class SizedProduct { public int Stock { get; set; } public string? Sizes { get; set; } }
class BundlePriceRow { public int ProductId { get; set; } public int Qty { get; set; } public decimal Price { get; set; } }
class CartQty { public int ProductId { get; set; } public long Qty { get; set; } }
class CartBundleRow { public int BundleId { get; set; } public int Qty { get; set; } public string Name { get; set; } = ""; public int DiscountPercent { get; set; } }
class Cand { public int ProductId { get; set; } public string Name { get; set; } = ""; public string ItemType { get; set; } = ""; public decimal Price { get; set; } public int Stock { get; set; } public int IdleDays { get; set; } public decimal Sold { get; set; } }
class OrderEmailInfo { public bool EmailNotify { get; set; } = true; public string? CustomerName { get; set; } public string? Email { get; set; } public string? Code { get; set; } }
class ProductRow { public int id { get; set; } public int categoryId { get; set; } public string category { get; set; } = ""; public string itemType { get; set; } = ""; public string name { get; set; } = ""; public decimal price { get; set; } public int stock { get; set; } public string? image { get; set; } public string? sizes { get; set; } public List<VariantRow> variants { get; set; } = new(); }
class VariantRow { public int id { get; set; } public int productId { get; set; } public string colorName { get; set; } = ""; public string? image { get; set; } }

static class Bundles
{
    static readonly SemaphoreSlim Lock = new(1, 1); static DateTime _last = DateTime.MinValue;
    public static async Task RefreshIfStale(MySqlConnection c, int slowDays, bool force)
    {
        if (!force && DateTime.UtcNow - _last < TimeSpan.FromMinutes(10)) return; await Lock.WaitAsync();
        try { if (!force && DateTime.UtcNow - _last < TimeSpan.FromMinutes(10)) return; await GenerateAsync(c, slowDays); _last = DateTime.UtcNow; } finally { Lock.Release(); }
    }
    public static async Task<int> GenerateAsync(MySqlConnection c, int slowDays)
    {
        var all = (await c.QueryAsync<Cand>(@"SELECT p.ProductId, p.Name, p.ItemType, p.Price, p.Stock, DATEDIFF(NOW(), IFNULL((SELECT MAX(o.CreatedAt) FROM OrderItems oi JOIN Orders o ON o.OrderId = oi.OrderId WHERE oi.ProductId = p.ProductId AND o.Status <> 'Cancelled'), p.AddedAt)) AS IdleDays, IFNULL((SELECT SUM(oi.Qty) FROM OrderItems oi JOIN Orders o ON o.OrderId = oi.OrderId WHERE oi.ProductId = p.ProductId AND o.Status <> 'Cancelled'), 0) AS Sold FROM Products p WHERE p.Stock > 0")).ToList();
        var used = (await c.QueryAsync<int>("SELECT DISTINCT ProductId FROM BundleItems")).ToHashSet(); var slow = all.Where(p => p.IdleDays >= slowDays && !used.Contains(p.ProductId)).OrderByDescending(p => p.IdleDays).Take(6).ToList(); var made = 0;
        foreach (var s in slow)
        {
            if (used.Contains(s.ProductId)) continue; var others = all.Where(p => p.ProductId != s.ProductId && !used.Contains(p.ProductId)).ToList();
            var partner = others.Where(p => p.ItemType != s.ItemType).OrderByDescending(p => p.Sold).ThenBy(p => p.IdleDays).ThenBy(p => p.Price).FirstOrDefault() ?? others.OrderByDescending(p => p.Sold).ThenBy(p => p.IdleDays).FirstOrDefault(); if (partner is null) continue;
            var pct = Math.Min(10, 5 + Math.Max(0, s.IdleDays - slowDays) / 10); var name = s.Name + " + " + partner.Name;
            await c.ExecuteAsync("INSERT INTO Bundles(Name,DiscountPercent,IsActive,AutoMade) VALUES(@name,@pct,1,1)", new { name, pct }); var id = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)");
            await c.ExecuteAsync("INSERT INTO BundleItems(BundleId,ProductId,Qty) VALUES(@id,@a,1),(@id,@b,1)", new { id, a = s.ProductId, b = partner.ProductId }); used.Add(s.ProductId); used.Add(partner.ProductId); made++;
        }
        return made;
    }
    public static async Task<List<object>> ListAsync(MySqlConnection c, bool activeOnly)
    {
        var heads = (await c.QueryAsync<BundleRow>("SELECT BundleId, Name, DiscountPercent, IsActive, AutoMade FROM Bundles ORDER BY BundleId DESC")).ToList();
        var items = (await c.QueryAsync<BundleItemRow>("SELECT bi.BundleId, p.ProductId, p.Name, p.Price, p.Stock, p.ImagePath AS Image, p.Sizes AS Sizes, bi.Qty FROM BundleItems bi JOIN Products p ON p.ProductId = bi.ProductId")).ToList();
        var list = new List<object>();
        foreach (var h in heads)
        {
            var its = items.Where(i => i.BundleId == h.BundleId).ToList(); if (its.Sum(i => i.Qty) < 2) continue; var inStock = its.All(i => i.Stock >= i.Qty);
            if (activeOnly && (!h.IsActive || !inStock)) continue; var listPrice = its.Sum(i => i.Price * i.Qty); var price = Math.Round(listPrice * (100 - h.DiscountPercent) / 100m, 2);
            list.Add(new { id = h.BundleId, name = h.Name, discountPercent = h.DiscountPercent, isActive = h.IsActive, auto = h.AutoMade, inStock, listPrice, price, saving = listPrice - price, items = its.Select(i => new { id = i.ProductId, name = i.Name, price = i.Price, image = i.Image, sizes = i.Sizes, qty = i.Qty }).ToList() });
        }
        return list;
    }
    public static async Task<(decimal discount, List<object> applied)> ForCart(MySqlConnection c, int uid, MySqlTransaction? tx)
    {
        var cart = (await c.QueryAsync<CartQty>("SELECT ProductId, CAST(SUM(Qty) AS SIGNED) AS Qty FROM CartItems WHERE UserId=@uid GROUP BY ProductId", new { uid }, tx)).ToDictionary(x => x.ProductId, x => (int)x.Qty);
        var cbs = (await c.QueryAsync<CartBundleRow>("SELECT cb.BundleId, cb.Qty, b.Name, b.DiscountPercent FROM CartBundles cb JOIN Bundles b ON b.BundleId = cb.BundleId WHERE cb.UserId=@uid AND b.IsActive=1 ORDER BY cb.BundleId", new { uid }, tx)).ToList();
        decimal total = 0; var applied = new List<object>();
        foreach (var cb in cbs)
        {
            var its = (await c.QueryAsync<BundlePriceRow>("SELECT bi.ProductId, bi.Qty, p.Price FROM BundleItems bi JOIN Products p ON p.ProductId = bi.ProductId WHERE bi.BundleId=@id", new { id = cb.BundleId }, tx)).ToList();
            if (its.Sum(i => i.Qty) < 2) continue; var n = cb.Qty; foreach (var i in its) n = Math.Min(n, (cart.TryGetValue(i.ProductId, out var have) ? have : 0) / i.Qty); if (n <= 0) continue;
            foreach (var i in its) cart[i.ProductId] -= i.Qty * n; var d = Math.Round(its.Sum(i => i.Price * i.Qty) * n * cb.DiscountPercent / 100m, 2); total += d;
            applied.Add(new { bundleId = cb.BundleId, name = cb.Name, qty = n, percent = cb.DiscountPercent, discount = d });
        }
        return (total, applied);
    }
}

static class Sizing
{
    public static List<string> Parse(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
    public static string? Check(string? csv, string? chosen) { var list = Parse(csv); if (list.Count == 0) return ""; var c = (chosen ?? "").Trim(); return list.Contains(c) ? c : null; }
    public static string? Normalize(string? s) { var l = Parse(s).Select(x => x.Length > 30 ? x[..30] : x).Distinct().ToList(); return l.Count == 0 ? null : string.Join(",", l); }
}

static class Pics
{
    public static async Task<(string? path, string? error)> SaveAsync(HttpRequest req, string webRoot, string folder, string prefix, long maxBytes)
    {
        var form = await req.ReadFormAsync(); var file = form.Files["image"]; if (file is null || file.Length == 0) return (null, "Choose pic.");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant(); if (!new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(ext) || file.Length > maxBytes) return (null, "Invalid file.");
        var dir = Path.Combine(webRoot, folder); Directory.CreateDirectory(dir); var fn = $"{prefix}_{Guid.NewGuid():N}{ext}"; await using (var fs = File.Create(Path.Combine(dir, fn))) await file.CopyToAsync(fs);
        return ("/" + folder + "/" + fn, null);
    }
}

static class Orders
{
    public static async Task<string?> TakeStock(MySqlConnection c, MySqlTransaction tx, int orderId, List<Line> lines)
    {
        foreach (var l in lines)
        {
            var n = await c.ExecuteAsync("UPDATE Products SET Stock=Stock-@q WHERE ProductId=@id AND Stock>=@q", new { q = l.Qty, id = l.ProductId }, tx); if (n == 0) return l.Name;
            await c.ExecuteAsync("INSERT INTO OrderItems(OrderId,ProductId,ProductName,Size,Color,UnitPrice,Qty) VALUES(@orderId,@id,@name,@size,@color,@price,@q)", new { orderId, id = l.ProductId, name = l.Name, size = l.Size, color = l.Color, price = l.Price, q = l.Qty }, tx);
        }
        return null;
    }
}

static class Gate
{
    public static string? Required(string path)
    {
        var p = path.ToLowerInvariant(); string file = p.Substring(p.LastIndexOf('/') + 1);
        if (p.StartsWith("/pages/")) { if (file == "error.html") return null; if (file.StartsWith("admin")) return "Admin"; if (file == "account.html" || file == "settings.html") return "Any"; return "Customer"; }
        if (p.StartsWith("/javascripts/")) { if (file == "api.js") return null; if (file.StartsWith("admin")) return "Admin"; if (file == "account.js" || file == "support.js" || file == "settings.js") return "Any"; return "Customer"; }
        return null;
    }
}

static class Pw
{
    public static string Hash(string pw) { var salt = RandomNumberGenerator.GetBytes(16); var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100_000, HashAlgorithmName.SHA256, 32); return $"v1.100000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(h)}"; }
    public static bool Verify(string pw, string stored) { var p = stored.Split('.'); if (p.Length != 4) return false; var salt = Convert.FromBase64String(p[2]); var expected = Convert.FromBase64String(p[3]); var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, int.Parse(p[1]), HashAlgorithmName.SHA256, expected.Length); return CryptographicOperations.FixedTimeEquals(h, expected); }
}

static class Auth
{
    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier); var db = ctx.HttpContext.RequestServices.GetRequiredService<Db>(); SessionRow? s = null;
        if (int.TryParse(id, out var uid)) { using var c = db.Open(); s = await c.QueryFirstOrDefaultAsync<SessionRow>("SELECT Role, Status, SessionVersion FROM Users WHERE UserId=@uid", new { uid }); await c.ExecuteAsync("UPDATE Users SET LastSeen = NOW() WHERE UserId=@uid AND (LastSeen IS NULL OR LastSeen < DATE_SUB(NOW(), INTERVAL 1 MINUTE))", new { uid }); }
        if (s is null || s.Status != "Active" || s.Role != ctx.Principal!.FindFirstValue(ClaimTypes.Role) || s.SessionVersion.ToString() != ctx.Principal.FindFirstValue("ver"))
        {
            ctx.HttpContext.Items["sy-reason"] = s is null ? "gone" : s.Status != "Active" ? "suspended" : s.SessionVersion.ToString() != ctx.Principal.FindFirstValue("ver") ? "replaced" : "changed";
            ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}

public class SmsService
{
    private readonly IConfiguration _config;
    public SmsService(IConfiguration config) { _config = config; }
    string Key => _config["SmsSettings:SemaphoreApiKey"] ?? "";
    public bool IsReady => !string.IsNullOrWhiteSpace(Key) && !Key.Contains("PASTE");
    // Sends through Semaphore (a Philippine SMS service). Returns false when no key is set up.
    public async Task<bool> SendAsync(string phone, string text)
    {
        if (!IsReady) return false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var res = await http.PostAsync("https://api.semaphore.co/api/v4/messages", new FormUrlEncodedContent(new Dictionary<string, string> { ["apikey"] = Key, ["number"] = phone, ["message"] = text, ["sendername"] = _config["SmsSettings:SenderName"] ?? "" }));
        return res.IsSuccessStatusCode;
    }
}

public class EmailService
{
    private readonly IConfiguration _config;
    public EmailService(IConfiguration config) { _config = config; }
    public bool IsReady => !string.IsNullOrWhiteSpace(_config["EmailSettings:SmtpHost"]) || RealSendGridKey();
    bool RealSendGridKey() { var k = _config["EmailSettings:SendGridApiKey"]; return !string.IsNullOrWhiteSpace(k) && k.StartsWith("SG.") && !k.Contains("PASTE"); }
    // Two ways to send: SMTP (for example a Gmail App Password) or SendGrid. Returns false when neither is set up or the send failed.
    public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent)
    {
        var senderEmail = _config["EmailSettings:SenderEmail"] ?? ""; var senderName = _config["EmailSettings:SenderName"] ?? "Web-Syscle";
        var host = _config["EmailSettings:SmtpHost"];
        if (!string.IsNullOrWhiteSpace(host))
        {
            var msg = new MimeKit.MimeMessage();
            var smtpUser = _config["EmailSettings:SmtpUser"];
            msg.From.Add(new MimeKit.MailboxAddress(senderName, string.IsNullOrWhiteSpace(senderEmail) ? smtpUser : senderEmail));
            msg.To.Add(MimeKit.MailboxAddress.Parse(toEmail)); msg.Subject = subject; msg.Body = new MimeKit.BodyBuilder { HtmlBody = htmlContent }.ToMessageBody();
            using var smtp = new MailKit.Net.Smtp.SmtpClient();
            await smtp.ConnectAsync(host, _config.GetValue("EmailSettings:SmtpPort", 587), MailKit.Security.SecureSocketOptions.StartTls);
            if (!string.IsNullOrEmpty(smtpUser)) await smtp.AuthenticateAsync(smtpUser, _config["EmailSettings:SmtpPassword"]);
            await smtp.SendAsync(msg); await smtp.DisconnectAsync(true);
            return true;
        }
        if (RealSendGridKey())
        {
            var client = new SendGridClient(_config["EmailSettings:SendGridApiKey"]);
            var m = MailHelper.CreateSingleEmail(new EmailAddress(senderEmail, senderName), new EmailAddress(toEmail), subject, "", htmlContent);
            var res = await client.SendEmailAsync(m);
            return res.IsSuccessStatusCode;
        }
        return false;
    }
    public Task<bool> SendPasswordResetAsync(string toEmail, string username, string link)
    {
        var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(username)},</p><p>We received a request to reset your Web-Syscle password. Click the button below to choose a new one. The link works for 30 minutes and only once.</p><p><a href=\"{link}\" style=\"background:#ff5a1f;color:#fff;padding:10px 18px;border-radius:20px;text-decoration:none;font-weight:bold\">Reset my password</a></p><p>If you did not ask for this, you can ignore this email. Your password has not been changed.</p>";
        return SendEmailAsync(toEmail, "Web-Syscle Password Reset", html);
    }
    public Task<bool> SendOrderStatusEmailAsync(string toEmail, string username, string orderCode, string status)
    {
        var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(username)},</p><p>Your order <b>{orderCode}</b> has been marked as <b>{status}</b>.</p>";
        return SendEmailAsync(toEmail, $"Order {orderCode} Update", html);
    }
}

static class ResetToken
{
    static ITimeLimitedDataProtector P(IServiceProvider sp) => sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("syscle.reset").ToTimeLimitedDataProtector();
    // a short fingerprint of the current password hash: once the password changes, the old link stops working (single use)
    public static string Print(string hash) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hash)))[..16];
    public static string Make(IServiceProvider sp, UserRow u) => P(sp).Protect(u.UserId + "|" + Print(u.PasswordHash), TimeSpan.FromMinutes(30));
    public static (int id, string print)? Read(IServiceProvider sp, string? token)
    {
        try { var p = P(sp).Unprotect(token ?? "").Split('|'); return (int.Parse(p[0]), p[1]); } catch { return null; }
    }
}

static class Seed
{
    // Adds the newer tables and columns by itself when the app starts, so nobody has to run another SQL file.
    static async Task AddColumn(MySqlConnection c, string table, string column, string ddl)
    {
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column", new { table, column }) == 0)
            await c.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {ddl}");
    }
    static async Task Migrate(MySqlConnection c)
    {
        await AddColumn(c, "Orders", "PaymentRef", "VARCHAR(40) NULL");
        await AddColumn(c, "Users", "EmailNotify", "TINYINT(1) NOT NULL DEFAULT 1");
        await c.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS Messages (
            MessageId INT AUTO_INCREMENT PRIMARY KEY, ThreadUserId INT NOT NULL, Sender ENUM('Customer','Staff') NOT NULL, Body VARCHAR(500) NOT NULL,
            IsRead TINYINT(1) NOT NULL DEFAULT 0, CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX IX_Messages_Thread (ThreadUserId, MessageId),
            FOREIGN KEY (ThreadUserId) REFERENCES Users(UserId) ON DELETE CASCADE) ENGINE=InnoDB");
    }
    public static async Task RunAsync(string cs, IConfiguration cfg)
    {
        // the database container can need a few seconds after a restart, so try for about 40 seconds before giving up
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                using var c = new MySqlConnection(cs); await c.OpenAsync(); await Migrate(c); if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users") > 0) return;
                var adminPw = cfg["Seed:AdminPassword"] ?? "admin123"; var custPw = cfg["Seed:CustomerPassword"] ?? "password123";
                if (cfg["ASPNETCORE_ENVIRONMENT"] == "Production" && adminPw == "admin123") { Console.WriteLine("[Syscle] Set Seed__AdminPassword to a strong password before the first start. No accounts were created."); return; }
                await c.ExecuteAsync("INSERT INTO Users(Username,Email,PasswordHash,Role) VALUES ('admin','admin@syscle.com',@a,'SuperAdmin')", new { a = Pw.Hash(adminPw) });
                if (custPw != "none") await c.ExecuteAsync("INSERT INTO Users(Username,Email,PasswordHash,Role) VALUES ('customer1','customer@syscle.com',@b,'Customer')", new { b = Pw.Hash(custPw) });
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Syscle] Startup seed attempt {attempt}/20 failed (database not ready, or schema out of date - run Database/Update_Schema.sql): " + e.Message);
                if (attempt < 20) await Task.Delay(2000);
            }
        }
    }
}
