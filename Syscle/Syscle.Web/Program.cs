using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 6_000_000; k.Limits.MaxRequestHeadersTotalSize = 32_768; k.Limits.MaxRequestLineSize = 8_192; });
var cs = builder.Configuration.GetConnectionString("Syscle") ?? throw new InvalidOperationException("Missing ConnectionStrings:Syscle in appsettings.json");
builder.Services.AddSingleton(new Db(cs));
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddSingleton<PayMongoService>();
builder.Services.AddSingleton<LalamoveService>();
builder.Services.AddSingleton<QrPhService>();
builder.Services.AddSingleton<PayPalService>();
builder.Services.AddHostedService<OrderExpiryService>();
// the keys that sign login cookies and reset links are kept in the database (table DataProtectionKeys), not in text files on the disk
builder.Services.AddDataProtection().SetApplicationName("Syscle");
builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(o => o.XmlRepository = new DbXmlRepository(sp.GetRequiredService<Db>())));

// Security:TrustProxy (env Security__TrustProxy). true = the site sits behind a reverse proxy / hosting platform (Render, Nginx, Cloudflare...) that adds the real visitor address.
// false = visitors connect straight to this app (for example "docker compose" with port 8080 published): then X-Forwarded-* headers typed by a visitor must be ignored,
// otherwise anyone can fake their address and get around the per-address login limit.
var trustProxy = builder.Configuration.GetValue("Security:TrustProxy", true);
// Security:SecureCookies (env Security__SecureCookies): set true on the live HTTPS site so sign-in cookies are never sent over plain HTTP. Leave false for http://localhost testing.
var secureCookies = builder.Configuration.GetValue("Security:SecureCookies", false);
if (trustProxy) builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    o.ForwardLimit = 1;   // only the address added by the closest proxy is used, so a header typed by the visitor cannot fake it
});

builder.Services.AddAuthentication(o => { o.DefaultScheme = "area"; o.DefaultChallengeScheme = "area"; o.DefaultForbidScheme = "area"; })
    .AddPolicyScheme("area", "customer or admin sign-in", o => { o.ForwardDefaultSelector = Schemes.Pick; })
    .AddCookie(Schemes.Customer, o => Schemes.Cookie(o, Schemes.Customer, secureCookies))
    .AddCookie(Schemes.Admin, o => Schemes.Cookie(o, Schemes.Admin, secureCookies));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("SuperAdmin", p => p.RequireRole("SuperAdmin"))
    .AddPolicy("Admin", p => p.RequireRole("Admin", "SuperAdmin"))
    .AddPolicy("Customer", p => p.RequireRole("Customer"));

var app = builder.Build();
if (trustProxy) app.UseForwardedHeaders();
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
    if (ctx.Request.IsHttps) ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";   // once a browser has seen the HTTPS site it refuses to use plain HTTP for a year
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";   // the admin "see the customer side" preview is a frame of this same site
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(self)";   // camera: scanning order QR codes. location: "use my location" when pinning a delivery place
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers["Cache-Control"] = "no-store";
    // normal JSON requests are tiny; cap them so nobody can flood the server with a giant body (uploads use multipart and keep their own limit)
    if (ctx.Request.Path.StartsWithSegments("/api") && (ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
    {
        var lim = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (lim != null && !lim.IsReadOnly) lim.MaxRequestBodySize = 20_000;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !ctx.Request.Path.StartsWithSegments("/api/webhooks") && ctx.Request.Headers["X-Requested-With"] != "syscle")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Bad request." });
        return;
    }
    var need = Gate.Required(ctx.Request.Path.Value ?? "");
    if (need != null)
    {
        // "Any" pages (account, settings) accept whichever portal is signed in; the other pages look only at their own portal's cookie
        var who = need == "Any" ? ((await ctx.AuthenticateAsync(Schemes.Admin)) is { Succeeded: true } a1 ? a1 : await ctx.AuthenticateAsync(Schemes.Customer)) : null;
        var user = who?.Principal ?? ctx.User;
        if (user.Identity?.IsAuthenticated != true) { ctx.Response.Redirect("/login.html"); return; }
        var role = user.FindFirstValue(ClaimTypes.Role);
        if (need == "Admin" && role != "Admin" && role != "SuperAdmin") { ctx.Response.Redirect("/Pages/homepage.html"); return; }
        if (need == "SuperAdmin" && role != "SuperAdmin") { ctx.Response.Redirect("/Pages/adminhomepage.html"); return; }
        if (need == "Customer" && role != "Customer") { ctx.Response.Redirect(role == "SuperAdmin" || role == "Admin" ? "/Pages/adminhomepage.html" : "/Pages/homepage.html"); return; }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = r =>
{
    // pages, scripts and styles are re-checked on every visit (a quick "not changed" answer when nothing changed), so an update shows without a hard refresh
    var n = r.File.Name.ToLowerInvariant(); if (n.EndsWith(".html") || n.EndsWith(".js") || n.EndsWith(".css")) r.Context.Response.Headers["Cache-Control"] = "no-cache";
} });

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
const int MaxName = 14, MaxPass = 64, MaxEmail = 50;
bool PwOk(string? p) => p != null && p.Length >= 8 && p.Length <= MaxPass;
const string PwMsg = "Password must be 8 to 64 characters.";
// The address put into emailed reset links. It comes from the settings (App__PublicUrl), never from the visitor's Host header: otherwise anyone could ask for a reset on someone else's
// account with a forged Host header and the victim would receive a genuine email whose link opens the attacker's site. In Development the request address is fine.
string? PublicBase(HttpContext ctx, IConfiguration cfg, IWebHostEnvironment env)
{
    var set = (cfg["App:PublicUrl"] ?? "").Trim().TrimEnd('/');
    if (set.Length > 0) return Uri.TryCreate(set, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? set : null;
    return env.IsDevelopment() ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : null;
}
string? NormPhone(string? raw)
{
    var d = Regex.Replace(raw ?? "", @"[^\d+]", ""); if (d.StartsWith("+63")) d = "0" + d[3..]; else if (d.StartsWith("63") && d.Length == 12) d = "0" + d[2..];
    return Regex.IsMatch(d, @"^09\d{9}$") ? d : null;
}
bool BadImageExt(string ext) => ext is not (".jpg" or ".jpeg" or ".png" or ".webp");

var auth = app.MapGroup("/api/auth");
var tries = new ConcurrentDictionary<string, List<DateTime>>();

auth.MapPost("/register", async (RegisterReq r, Db db, HttpContext ctx) =>
{
    var username = (r.Username ?? "").Trim(); var email = (r.Email ?? "").Trim();
    if (!Regex.IsMatch(username, @"^[A-Za-z0-9_.-]{3,14}$")) return Bad("Username must be 3 to 14 letters, numbers, dots, dashes or underscores.");
    if (email.Length > MaxEmail || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Bad("Enter a valid email address (up to 50 characters).");
    if (!PwOk(r.Password)) return Bad(PwMsg);
    string? phone = null;   // optional: it is what lets this customer reset a forgotten password by SMS
    if (!string.IsNullOrWhiteSpace(r.Phone)) { phone = NormPhone(r.Phone); if (phone is null) return Bad("Enter a valid mobile number, like 09171234567, or leave it empty."); }
    using var c = db.Open();
    var regKey = "reg|" + ctx.Connection.RemoteIpAddress;   // sign-ups are limited per address (10 an hour) so nobody can use them to burn server time or fill the user list
    if (await LoginGuard.IsBlockedAsync(c, regKey, 10, 60)) return Throttled();
    var taken = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@username OR Email=@email", new { username, email });
    if (taken > 0) return Conflict("That username or email is already registered.");
    if (phone != null && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Phone=@phone", new { phone }) > 0) return Conflict("That mobile number is already used by another account.");
    await c.ExecuteAsync("INSERT INTO Users(Username,Email,Phone,PasswordHash,Role) VALUES(@username,@email,@phone,@h,'Customer')", new { username, email, phone, h = Pw.Hash(r.Password!) });
    await LoginGuard.FailAsync(c, regKey);
    return Results.Ok(new { ok = true });
});

async Task<IResult> SignInUser(HttpContext ctx, MySqlConnection c, int id, string name, string role)
{
    await c.ExecuteAsync("UPDATE Users SET SessionVersion = SessionVersion + 1, LastActive = NOW() WHERE UserId=@id", new { id });
    var ver = await c.ExecuteScalarAsync<int>("SELECT SessionVersion FROM Users WHERE UserId=@id", new { id });
    var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, name), new(ClaimTypes.Role, role), new("ver", ver.ToString()) };
    var scheme = role == "Customer" ? Schemes.Customer : Schemes.Admin;
    await ctx.SignInAsync(scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)));
    return Results.Ok(new { username = name, role, redirect = scheme == Schemes.Admin ? "Pages/adminhomepage.html" : "Pages/homepage.html" });
}
IResult Throttled() => Results.Json(new { error = "Too many attempts. Try again in a few minutes." }, statusCode: 429);

auth.MapPost("/login", async (LoginReq r, Db db, HttpContext ctx) =>
{
    // reject oversized input before doing any work (the password check is deliberately slow, so huge inputs must never reach it)
    if ((r.Login ?? "").Length > MaxEmail || (r.Password ?? "").Length > 64) return Results.Json(new { error = "Invalid credentials. Please try again." }, statusCode: 401);
    using var c = db.Open();
    // attempts are counted in the database (table LoginAttempts): restarting the server or editing a file does not reset them
    // Three counters: 30 wrong tries per address, 8 per username FROM ONE ADDRESS, and 60 per username from anywhere.
    // The per-username-per-address counter is what stops password guessing without letting a stranger lock the real owner (or the admin) out of their own account with a few bad tries.
    // The 60-per-username counter still slows a guessing attack that is spread over many addresses.
    var who = (r.Login ?? "").Trim().ToLowerInvariant(); var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var keyIp = "ip|" + ip; var keyUserIp = "uip|" + who + "|" + ip; var keyUser = "user|" + who;
    if (await LoginGuard.IsBlockedAsync(c, keyIp, 30, 10) || await LoginGuard.IsBlockedAsync(c, keyUserIp, 8, 10) || await LoginGuard.IsBlockedAsync(c, keyUser, 60, 10)) return Throttled();
    var u = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId,Username,Email,PasswordHash,Role,Status,SessionVersion,TwoFactorEnabled FROM Users WHERE Username=@l OR Email=@l LIMIT 1", new { l = (r.Login ?? "").Trim() });
    // a missing user costs the same time as a wrong password, so response time does not reveal which usernames exist
    var ok = Pw.Verify(r.Password ?? "", u?.PasswordHash ?? Pw.Dummy);
    if (u is null || !ok) { await LoginGuard.FailAsync(c, keyIp); await LoginGuard.FailAsync(c, keyUserIp); await LoginGuard.FailAsync(c, keyUser); return Results.Json(new { error = "Invalid credentials. Please try again." }, statusCode: 401); }
    if (u.Status != "Active") return Results.Json(new { error = "This account is suspended. Please contact the store admin." }, statusCode: 403);
    await LoginGuard.ClearAsync(c, keyUserIp); await LoginGuard.ClearAsync(c, keyUser);
    // upgrade an older, weaker password hash now that we know the correct password (this also retires any reset link issued before the upgrade)
    if (Pw.NeedsRehash(u.PasswordHash)) await c.ExecuteAsync("UPDATE Users SET PasswordHash=@h WHERE UserId=@id", new { h = Pw.Hash(r.Password!), id = u.UserId });
    if (u.TwoFactorEnabled) return Results.Ok(new { require2fa = true, ticket = await Tickets.IssueAsync(c, u.UserId, "2fa", 5) });
    return await SignInUser(ctx, c, u.UserId, u.Username, u.Role);
});

auth.MapPost("/login-2fa", async (Verify2FAReq r, Db db, HttpContext ctx) =>
{
    // The ticket is only handed out after the password was verified, so the 2FA step cannot be used on its own. It allows 5 wrong codes and then dies.
    using var c = db.Open();
    var t = await Tickets.FindAsync(c, r.Ticket, "2fa");
    if (t is null) return Results.Json(new { error = "Your sign-in expired. Please enter your password again." }, statusCode: 401);
    var u = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId,Username,Role,Status,SessionVersion,TwoFactorSecret,TwoFactorEnabled,TwoFactorLastStep FROM Users WHERE UserId=@id", new { id = t.UserId });
    if (u is null || !u.TwoFactorEnabled || string.IsNullOrEmpty(u.TwoFactorSecret)) return Bad("Invalid request.");
    if (u.Status != "Active") return Results.Json(new { error = "This account is suspended. Please contact the store admin." }, statusCode: 403);
    var totp = new Totp(Base32Encoding.ToBytes(SecretBox.Reveal(u.TwoFactorSecret)));
    // a code that was already used once is refused (replay protection)
    if (!totp.VerifyTotp((r.Code ?? "").Trim(), out var step, new VerificationWindow(1, 1)) || step <= u.TwoFactorLastStep) { await Tickets.WrongAsync(c, r.Ticket); return Bad("Invalid 2FA code."); }
    await c.ExecuteAsync("UPDATE Users SET TwoFactorLastStep=@step WHERE UserId=@id", new { step, id = u.UserId });
    await Tickets.BurnAsync(c, r.Ticket);
    return await SignInUser(ctx, c, u.UserId, u.Username, u.Role);
});

auth.MapPost("/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(Schemes.Pick(ctx)); return Results.Ok(new { ok = true }); });

// tells a page which of the two portals are signed in on this browser (so a page can show the right header and never mix them)
auth.MapGet("/whoami", async (HttpContext ctx, Db db) =>
{
    async Task<object?> Who(string scheme)
    {
        var r = await ctx.AuthenticateAsync(scheme); if (!r.Succeeded) return null;
        return new { username = r.Principal!.FindFirstValue(ClaimTypes.Name), role = r.Principal.FindFirstValue(ClaimTypes.Role) };
    }
    using var c = db.Open();
    return Results.Ok(new { shop = await Who(Schemes.Customer), admin = await Who(Schemes.Admin), idleSeconds = await Cfg.IntAsync(c, "AdminIdleSeconds", 30, 3600) });
});
// called when the admin really clicks or types: this is what the idle timer measures
auth.MapPost("/activity", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); await c.ExecuteAsync("UPDATE Users SET LastActive=NOW() WHERE UserId=@id", new { id = Uid(u) }); return Results.Ok(new { ok = true }); }).RequireAuthorization();

auth.MapPost("/forgot", async (ForgotReq r, Db db, EmailService emailer, HttpContext ctx, IWebHostEnvironment env, IConfiguration cfg) =>
{
    var email = (r.Email ?? "").Trim().ToLowerInvariant();
    var msg = "If that email has an account, a password reset link has been sent. It works for 30 minutes.";
    if (email.Length > MaxEmail) return Results.Ok(new { message = msg });
    using var c = db.Open();
    // 5 requests per email and 15 per address in 10 minutes: nobody can use this form to flood inboxes or use up the email provider's daily limit
    var fkey = "forgot|" + email; var fip = "forgotip|" + ctx.Connection.RemoteIpAddress;
    if (await LoginGuard.IsBlockedAsync(c, fkey, 5, 10) || await LoginGuard.IsBlockedAsync(c, fip, 15, 10)) return Throttled();
    await LoginGuard.FailAsync(c, fkey); await LoginGuard.FailAsync(c, fip);
    var user = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId, Username, Email, PasswordHash FROM Users WHERE Email=@email AND Status='Active'", new { email });
    if (user is null) return Results.Ok(new { message = msg });
    var baseUrl = PublicBase(ctx, cfg, env);
    if (baseUrl is null) { Console.WriteLine("[Syscle] No reset email was sent: set App__PublicUrl to the address of your site (for example https://yourshop.example.com)."); return Results.Ok(new { message = msg }); }
    // The password is NOT changed here. The person must open the link and choose a new one.
    var link = $"{baseUrl}/reset.html?token={Uri.EscapeDataString(ResetToken.Make(ctx.RequestServices, user))}";
    if (env.IsDevelopment())
    {
        var sent = false;
        try { sent = await emailer.SendPasswordResetAsync(user.Email, user.Username, link); } catch (Exception ex) { Console.WriteLine("[Syscle] Reset email failed: " + ex.Message); }
        if (!sent)
        {
            Console.WriteLine("[Syscle] Email is not set up, so no reset email was sent to " + user.Email + ".");   // the link itself is a password-equivalent secret: it is only ever shown in Development
            Console.WriteLine("[Syscle] (Development only) reset link: " + link);
            return Results.Ok(new { message = "Email is not set up on this server, so here is your reset link instead.", devLink = link });
        }
        return Results.Ok(new { message = msg });
    }
    // Live site: send in the background so the answer comes back at the same speed whether or not the email has an account (a slow answer would reveal who is registered).
    var to = user.Email; var name = user.Username;
    _ = Task.Run(async () =>
    {
        try { if (!await emailer.SendPasswordResetAsync(to, name, link)) Console.WriteLine("[Syscle] The reset email to " + to + " was not sent (is email set up? see docs/EMAIL_SMS_SETUP.md)."); }
        catch (Exception ex) { Console.WriteLine("[Syscle] Reset email failed: " + ex.Message); }
    });
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

auth.MapGet("/ping", () => Results.Ok(new { ok = true })).RequireAuthorization();  // a ping is not activity: it must not keep an idle admin signed in

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
    await c.ExecuteAsync("UPDATE Users SET TwoFactorSecret=@s WHERE UserId=@id", new { s = SecretBox.Protect(base32Secret), id = Uid(u) });   // stored encrypted, never as plain text
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
    var totp = new Totp(Base32Encoding.ToBytes(SecretBox.Reveal(secret)));
    if (!totp.VerifyTotp((r.Code ?? "").Trim(), out var step, new VerificationWindow(1, 1))) return Bad("Invalid 2FA code.");
    await c.ExecuteAsync("UPDATE Users SET TwoFactorEnabled=1, TwoFactorLastStep=@step WHERE UserId=@id", new { step, id = Uid(u) });
    return Results.Ok(new { ok = true });
});

me.MapPost("/2fa/disable", async (Disable2FAReq r, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open();
    var row = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT PasswordHash, TwoFactorSecret, TwoFactorEnabled FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (row is null || !row.TwoFactorEnabled || string.IsNullOrEmpty(row.TwoFactorSecret)) return Bad("2FA is not turned on.");
    if (!Pw.Verify(r.Password ?? "", row.PasswordHash)) return Bad("Your password is not correct.");
    if (!new Totp(Base32Encoding.ToBytes(SecretBox.Reveal(row.TwoFactorSecret))).VerifyTotp((r.Code ?? "").Trim(), out _, new VerificationWindow(1, 1))) return Bad("Invalid 2FA code.");
    await c.ExecuteAsync("UPDATE Users SET TwoFactorEnabled=0, TwoFactorSecret=NULL, TwoFactorLastStep=0 WHERE UserId=@id", new { id = Uid(u) });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/categories", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT CategoryId AS id, Name AS name, ImagePath AS image FROM Categories ORDER BY CategoryId")); });

app.MapGet("/api/products", async (string? category, Db db) =>
{
    using var c = db.Open();
    var prods = (await c.QueryAsync<ProductRow>(@"SELECT p.ProductId AS id, (SELECT ROUND(AVG(r.Rating),1) FROM Reviews r WHERE r.ProductId=p.ProductId) AS rating, (SELECT COUNT(*) FROM Reviews r WHERE r.ProductId=p.ProductId) AS reviews, (SELECT IFNULL(SUM(oi.Qty),0) FROM OrderItems oi JOIN Orders o ON o.OrderId=oi.OrderId WHERE oi.ProductId=p.ProductId AND o.Status<>'Cancelled' AND o.RefundStatus<>'Refunded') AS sold, p.Name AS name, p.ItemType AS itemType, p.Price AS price, p.Stock AS stock, p.ImagePath AS image, p.Sizes AS sizes, p.Description AS description, p.Specs AS specs, p.FitTags AS fitTags, p.Sku AS sku, p.IsFeatured AS featured, c.Name AS category FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId WHERE (@category IS NULL OR c.Name = @category) ORDER BY p.Name", new { category = Nz(category) })).ToList();
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

var shop = app.MapGroup("/api").RequireAuthorization("Customer");
shop.MapPost("/products/{id:int}/reviews", async (int id, ReviewReq r, ClaimsPrincipal u, Db db) =>
{
    if (r.Rating < 1 || r.Rating > 5) return Bad("Rating must be 1-5.");
    if ((r.Comment ?? "").Length > 1000) return Bad("Review is too long (1000 characters max).");
    using var c = db.Open();
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Products WHERE ProductId=@id", new { id }) == 0) return Results.NotFound(new { error = "Product not found." });
    if (!await Features.BoughtAsync(c, Uid(u), id)) return Results.Json(new { error = "You can review an item only after you bought it and the order was completed." }, statusCode: 403);
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
shop.MapPost("/promo", async (PromoReq r, ClaimsPrincipal u, Db db) => { using var c = db.Open(); var (pct, perr) = await Promos.Check(c, (r.Code ?? "").Trim(), null, Uid(u)); return pct is null ? Results.NotFound(new { error = perr ?? "Invalid code." }) : Results.Ok(new { percent = pct }); });
shop.MapGet("/payment-methods", async (Db db, PayMongoService pm) =>
{
    // every payment method the store switched on is shown. An online one whose keys are not set yet is marked (test mode lets it be tried with a fake payment page).
    using var c = db.Open(); var test = cfg.GetValue("Payments:TestMode", false);
    var rows = await c.QueryAsync<PayMethodRow>("SELECT MethodCode AS Code, Label, AccountNumber, AccountName FROM PaymentMethods WHERE IsOn=1 AND MethodCode<>'qrph'");
    return Results.Ok(rows.Select(m => { var ready = m.Code == "paymongo" ? pm.IsReady : m.Code == "paypal" ? pp.IsReady : true; return new { code = m.Code, label = m.Label, accountNumber = m.AccountNumber, accountName = m.AccountName, ready, test = !ready && test, online = m.Code is "paymongo" or "paypal" }; }));
});

shop.MapPost("/checkout", async (CheckoutReq r, ClaimsPrincipal u, Db db, EmailService emailer, PayMongoService pm, PayPalService pp, HttpRequest req, IConfiguration cfg) =>
{
    using var c = db.Open(); using var tx = c.BeginTransaction(); var uid = Uid(u);
    var lines = (await c.QueryAsync<Line>("SELECT p.ProductId, p.Name, p.Price, ci.Qty, ci.Size, ci.Color FROM CartItems ci JOIN Products p ON p.ProductId=ci.ProductId WHERE ci.UserId=@uid FOR UPDATE OF ci", new { uid }, tx)).ToList();   // FOR UPDATE: a second checkout from the same customer (double click, two tabs) waits here, then finds the cart already empty
    if (lines.Count == 0) return Bad("Cart empty.");
    var method = await c.QueryFirstOrDefaultAsync<string>("SELECT Label FROM PaymentMethods WHERE MethodCode=@m AND IsOn=1", new { m = r.PaymentMethod }, tx);
    if (method is null || r.PaymentMethod == "qrph") return Bad("Invalid payment.");
    var online = r.PaymentMethod is "paymongo" or "paypal"; var providerReady = r.PaymentMethod == "paypal" ? pp.IsReady : pm.IsReady; var testPay = online && !providerReady && cfg.GetValue("Payments:TestMode", false);
    if (online && !providerReady && !testPay) return Bad("This online payment is not set up yet. Please choose another method.");
    // Home delivery: the customer gives an address and a phone number the rider can call. The delivery fee is set by the store.
    var delivery = r.Fulfillment == "Delivery"; string? addr = null, dphone = null; decimal fee = 0; int codMax = 0; double dkm = 0; double? dlat = null, dlng = null;
    if (delivery)
    {
        addr = Regex.Replace((r.Address ?? "").Trim(), @"\s+", " "); dphone = (r.Phone ?? "").Trim();
        if (addr.Length < 10 || addr.Length > 250) return Bad("Enter your full delivery address (house number, street, barangay, city).");
        if (!Regex.IsMatch(dphone, @"^[0-9+\- ]{7,20}$")) return Bad("Enter a phone number the rider can call.");
        // the store delivers by itself within a set range. The pin on the map is measured here; the fee is worked out here, never taken from the browser.
        if (!Geo.ValidPoint(r.Lat, r.Lng)) return Bad("Pin your delivery place on the map so we can check the distance.");
        // settings are read on their own connection: this one is busy with the order transaction
        DeliveryRule rule; using (var c2 = db.Open()) { rule = await DeliveryRules.LoadAsync(c2); codMax = await Cfg.IntAsync(c2, "CodMax", 0, 1000000); }
        if (!rule.Enabled) return Bad("Home delivery is not available right now. Please choose Store pick-up.");
        dkm = Geo.RoadKm(rule.StoreLat, rule.StoreLng, r.Lat!.Value, r.Lng!.Value);
        if ((decimal)dkm > rule.MaxKm) return Bad($"Sorry, we deliver only within {rule.MaxKm:0.#} km of the store. Your place is about {dkm:0.#} km away. Please choose Store pick-up.");
        fee = DeliveryRules.Fee(rule, dkm); dlat = r.Lat; dlng = r.Lng;
    }
    if (r.PickupDate is null || r.PickupDate.Value.Date < Promos.Today || r.PickupDate.Value.Date > Promos.Today.AddDays(30)) return Bad("Invalid date.");
    var pct = 0; string? code = null;
    if (!string.IsNullOrWhiteSpace(r.PromoCode)) { code = r.PromoCode.Trim(); var (found, promoErr) = await Promos.Check(c, code, tx, uid); if (found is null) return Bad(promoErr ?? "Invalid promo."); pct = found.Value; }
    var sub = lines.Sum(l => l.Price * l.Qty); var (bundleDiscount, _) = await Bundles.ForCart(c, uid, tx); var discount = Math.Round((sub - bundleDiscount) * pct / 100m, 2); var total = sub - bundleDiscount - discount + fee;
    // cash on delivery is allowed, but only up to the limit the store sets (0 = no limit), because the money is collected after the goods are handed over
    if (delivery && r.PaymentMethod == "cash" && codMax > 0 && total > codMax) return Bad($"Cash on delivery is only for orders up to ₱{codMax:N0}. Please pay with GCash or PayMongo, or choose Store pick-up.");
    var name = await c.ExecuteScalarAsync<string>("SELECT Username FROM Users WHERE UserId=@uid", new { uid }, tx);
    await c.ExecuteAsync(@"INSERT INTO Orders(UserId,CustomerName,Channel,PaymentMethod,PaymentRef,PromoCode,Discount,BundleDiscount,Total,Status,PickupDate,FulfillmentType,DeliveryAddress,DeliveryPhone,DeliveryFee,DeliveryLat,DeliveryLng,DeliveryKm) VALUES(@uid,@name,'Online',@method,@payRef,@code,@discount,@bundleDiscount,@total,'Pending',@pickup,@ft,@addr,@dphone,@fee,@dlat,@dlng,@dkmv)", new { uid, name, method, payRef, code, discount, bundleDiscount, total, pickup = r.PickupDate.Value.Date, ft = delivery ? "Delivery" : "Pickup", addr, dphone, fee, dlat, dlng, dkmv = delivery ? (double?)dkm : null }, tx);
    var orderId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)", null, tx);
    var soldOut = await Orders.TakeStock(c, tx, orderId, lines);
    if (soldOut != null) return Conflict($"Out of stock: {soldOut}");
    await c.ExecuteAsync("DELETE FROM CartItems WHERE UserId=@uid", new { uid }, tx); await c.ExecuteAsync("DELETE FROM CartBundles WHERE UserId=@uid", new { uid }, tx);
    var orderCode = "ORD-" + orderId.ToString("D6"); tx.Commit();
    // confirmation email (skipped quietly when email is not set up or the customer switched it off)
    try
    {
        var who = await c.QueryFirstOrDefaultAsync<OrderEmailInfo>("SELECT Username AS CustomerName, Email, IFNULL(EmailNotify,1) AS EmailNotify FROM Users WHERE UserId=@uid", new { uid });
        if (who != null && who.EmailNotify && !string.IsNullOrEmpty(who.Email))
            await emailer.SendOrderPlacedEmailAsync(who.Email, who.CustomerName ?? "", orderCode, total, r.PickupDate!.Value.Date, string.Join(", ", lines.Select(l => $"{l.Name} x{l.Qty}")));
    }
    catch (Exception ex) { Console.WriteLine("[Syscle] Order email failed: " + ex.Message); }
    // online payment: open a PayMongo checkout page. PayMongo makes a QR code for exactly this order's total (QR Ph, scanned from any bank or e-wallet app) and also offers GCash, Maya and card. The order stays Unpaid until PayMongo confirms.
    string? checkoutUrl = null, payError = null;
    if (online)
    {
        var origin = $"{req.Scheme}://{req.Host}";
        var (sid, url, err) = await pm.CreateSessionAsync(orderCode, orderId, total, $"{origin}/Pages/orders.html?paid={orderId}", $"{origin}/Pages/orders.html?cancelled={orderId}");
        if (sid != null) { await c.ExecuteAsync("UPDATE Orders SET PaymentRef=@sid WHERE OrderId=@orderId", new { sid, orderId }); checkoutUrl = url; } else payError = err;
    }
    return Results.Ok(new { orderCode, orderId, total, deliveryFee = fee, checkoutUrl, payError });
});

// Pay later: reopens the PayMongo checkout page for an unpaid online order.
shop.MapPost("/orders/{id:int}/pay", async (int id, ClaimsPrincipal u, Db db, PayMongoService pm, HttpRequest req) =>
{
    using var c = db.Open();
    var online = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders o JOIN PaymentMethods pm ON pm.Label = o.PaymentMethod WHERE o.OrderId=@id AND o.UserId=@uid AND pm.MethodCode='paymongo'", new { id, uid = Uid(u) }) > 0;
    var o = !online ? null : await c.QueryFirstOrDefaultAsync<OrderLock>("SELECT Status, PaymentRef, PaymentStatus, FulfillmentType FROM Orders WHERE OrderId=@id AND UserId=@uid", new { id, uid = Uid(u) });
    if (o is null) return Results.NotFound(new { error = "Not found." }); if (o.Status != "Pending" || o.PaymentStatus == "Paid") return Conflict("This order does not need a payment.");
    if (!pm.IsReady) return Bad("Online payment is not available right now.");
    var total = await c.ExecuteScalarAsync<decimal>("SELECT Total FROM Orders WHERE OrderId=@id", new { id }); var oc = "ORD-" + id.ToString("D6"); var origin = $"{req.Scheme}://{req.Host}";
    var (sid, url, err) = await pm.CreateSessionAsync(oc, id, total, $"{origin}/Pages/orders.html?paid={id}", $"{origin}/Pages/orders.html?cancelled={id}");
    if (sid is null) return Bad(err ?? "Could not open the payment page.");
    await c.ExecuteAsync("UPDATE Orders SET PaymentRef=@sid WHERE OrderId=@id", new { sid, id }); return Results.Ok(new { checkoutUrl = url });
});
// Called when the customer comes back from PayMongo: asks PayMongo directly if the payment went through (works even if the webhook is not set up).
shop.MapPost("/orders/{id:int}/verify-payment", async (int id, ClaimsPrincipal u, Db db, PayMongoService pm, PayPalService pp) =>
{
    using var c = db.Open();
    var o = await c.QueryFirstOrDefaultAsync<OrderLock>("SELECT Status, PaymentRef, PaymentStatus, FulfillmentType FROM Orders WHERE OrderId=@id AND UserId=@uid", new { id, uid = Uid(u) });
    if (o is null) return Results.NotFound(new { error = "Not found." });
    if (o.PaymentStatus == "Paid") return Results.Ok(new { paid = true });
    // PayPal: the customer comes back approved, and this call captures the money (PayPal only takes it at this step)
    if (o.PaymentRef != null && o.PaymentRef.StartsWith("pp_") && await pp.IsPaidAsync(o.PaymentRef[3..])) { await c.ExecuteAsync("UPDATE Orders SET " + Sql.MarkPaid + " WHERE OrderId=@id AND PaymentStatus<>'Paid'", new { id }); return Results.Ok(new { paid = true }); }
    if (o.PaymentRef != null && o.PaymentRef.StartsWith("cs_") && await pm.IsPaidAsync(o.PaymentRef)) { await c.ExecuteAsync("UPDATE Orders SET " + Sql.MarkPaid + " WHERE OrderId=@id AND PaymentStatus<>'Paid'", new { id }); return Results.Ok(new { paid = true }); }
    return Results.Ok(new { paid = false });
});
// PayMongo calls this address when a payment is made (set it in the PayMongo dashboard: https://YOUR-SITE/api/webhooks/paymongo).
app.MapPost("/api/webhooks/paymongo", async (HttpRequest req, Db db, PayMongoService pm) =>
{
    using var reader = new StreamReader(req.Body); var payload = await reader.ReadToEndAsync();
    if (!pm.VerifySignature(payload, req.Headers["Paymongo-Signature"].ToString())) return Results.Unauthorized();
    string? sid = null;
    try
    {
        using var doc = JsonDocument.Parse(payload); var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        if (attrs.GetProperty("type").GetString() == "checkout_session.payment.paid")
        {
            var sid = attrs.GetProperty("data").GetProperty("id").GetString(); using var c = db.Open();
            await c.ExecuteAsync("UPDATE Orders SET PaymentStatus='Paid', PaidAt=NOW() WHERE PaymentRef=@sid AND PaymentStatus<>'Paid'", new { sid });
        }
        else if (attrs.GetProperty("type").GetString() == "payment.paid")
        {
            // in-shop QR Ph payment: the payment names the payment intent that was saved on the order
            var pid = attrs.GetProperty("data").GetProperty("attributes").GetProperty("payment_intent_id").GetString(); using var c = db.Open();
            if (!string.IsNullOrEmpty(pid)) await QrPay.MarkPaidByIntentAsync(c, pid);
        }
    }
    return Results.Ok(new { ok = true });
});

shop.MapGet("/orders", async (ClaimsPrincipal u, Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync(@"SELECT o.OrderId AS id, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS code, o.CreatedAt AS date, o.PickupDate AS pickup, o.PaymentMethod AS payment, o.Total AS total, o.Status AS status, o.FulfillmentType AS fulfillment, o.DeliveryAddress AS address, o.DeliveryPhone AS deliveryPhone, o.DeliveryFee AS deliveryFee, o.Courier AS courier, o.TrackingNo AS trackingNo, o.DeliveryKm AS deliveryKm, o.LalamoveStatus AS lalamoveStatus, o.LalamoveLink AS lalamoveLink, o.LalamoveDriver AS lalamoveDriver, o.LalamoveCost AS lalamoveCost, o.PaymentStatus AS paymentStatus, o.RefundStatus AS refundStatus, o.RefundReason AS refundReason, o.RefundNote AS refundNote, (o.ItemsConfirmedAt IS NOT NULL) AS itemsConfirmed, o.ReadyAt AS readyAt, o.CompletedAt AS completedAt, o.PaidAt AS paidAt, o.Discount AS discount, o.PromoCode AS promoCode, (SELECT GROUP_CONCAT(CONCAT(oi.ProductName, IF(oi.Size <> '', CONCAT(' (', oi.Size, ')'), ''), IF(oi.Color <> '', CONCAT(' [', oi.Color, ']'), ''), ' x', oi.Qty) SEPARATOR ', ') FROM OrderItems oi WHERE oi.OrderId = o.OrderId) AS items FROM Orders o WHERE o.UserId=@uid ORDER BY o.OrderId DESC", new { uid = Uid(u) })); });
shop.MapPost("/orders/{id:int}/cancel", async (int id, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var st = await c.QueryFirstOrDefaultAsync<string>("SELECT Status FROM Orders WHERE OrderId=@id AND UserId=@uid FOR UPDATE", new { id, uid = Uid(u) }, tx);
    if (st is null) return Results.NotFound(new { error = "Not found." }); if (st != "Pending") return Conflict("Can only cancel pending.");
    if (await c.ExecuteScalarAsync<string>("SELECT PaymentStatus FROM Orders WHERE OrderId=@id", new { id }, tx) == "Paid") return Conflict("This order is already paid online. Please message the store to cancel it and refund your payment.");
    await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name);
    await c.ExecuteAsync("UPDATE Orders SET Status='Cancelled' WHERE OrderId=@id", new { id }, tx); tx.Commit(); return Results.Ok(new { ok = true });
});

var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");


// ---------- store info (shown in the help window; edited by admins in Settings) ----------
string[] StoreKeys = { "StoreName", "StoreTagline", "StoreAddress", "StorePhone", "StoreHours", "PickupNote", "DeliveryFee", "StoreLat", "StoreLng", "DeliveryMaxKm", "DeliveryBaseFee", "DeliveryPerKm", "LalamoveService", "ReceiptFooter", "StoreTIN", "AutoPrintReceipt", "CodMax" };
async Task<Dictionary<string, string>> StoreInfo(MySqlConnection c)
{
    var rows = await c.QueryAsync<KV>("SELECT SettingKey AS K, IFNULL(SettingValue,'') AS V FROM Settings WHERE SettingKey IN @keys", new { keys = StoreKeys });
    var d = StoreKeys.ToDictionary(k => k, _ => ""); foreach (var row in rows) d[row.K] = row.V; if (d["StoreName"] == "") d["StoreName"] = "Web-Syscle"; foreach (var k in StoreKeys) if (d[k] == "" && Cfg.Defaults.TryGetValue(k, out var dv)) d[k] = dv; return d;
}
app.MapGet("/api/store-info", async (Db db) =>
{
    using var c = db.Open(); var d = await StoreInfo(c);
    foreach (var k in new[] { "PickupHoldDays", "RefundWindowDays", "UnpaidOnlineHours", "LowStockThreshold" }) d[k] = await Cfg.GetAsync(c, k);   // the rules a customer should know about
    return Results.Ok(d);
});

// ---------- SMS recovery: forgot password with a mobile number ----------
auth.MapPost("/forgot-sms", async (SmsForgotReq r, Db db, SmsService sms, HttpContext ctx, IWebHostEnvironment env) =>
{
    var phone = NormPhone(r.Phone); if (phone is null) return Bad("Enter your mobile number, like 09171234567.");
    using var c = db.Open();
    var skey = "sms|" + phone; if (await LoginGuard.IsBlockedAsync(c, skey, 3, 10) || await LoginGuard.IsBlockedAsync(c, "smsip|" + ctx.Connection.RemoteIpAddress, 10, 10)) return Throttled();
    await LoginGuard.FailAsync(c, skey); await LoginGuard.FailAsync(c, "smsip|" + ctx.Connection.RemoteIpAddress);
    var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    var user = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId, PasswordHash FROM Users WHERE Phone=@phone AND Status='Active' LIMIT 1", new { phone });
    // the same answer is given whether or not the number exists, so nobody can use this form to find out who is registered
    var msg = "If that number is registered, a 6-digit code was sent by SMS. It works for 10 minutes.";
    if (user is null) return Results.Ok(new { message = msg, ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() });
    var ticket = await Tickets.IssueAsync(c, user.UserId, "sms", 10, code);
    var text = $"Web-Syscle code: {code}. Valid for 10 minutes. Do not share it with anyone.";
    if (!env.IsDevelopment())
    {
        // Live site: send in the background so the answer is as fast for a registered number as for an unknown one.
        var to = phone;
        _ = Task.Run(async () =>
        {
            try { if (!await sms.SendAsync(to, text)) Console.WriteLine("[Syscle] The recovery code to " + to + " was not sent (is SmsSettings__SemaphoreApiKey set? see docs/EMAIL_SMS_SETUP.md)."); }
            catch (Exception ex) { Console.WriteLine("[Syscle] SMS failed: " + ex.Message); }
        });
        return Results.Ok(new { message = msg, ticket });
    }
    var sent = false;
    try { sent = await sms.SendAsync(phone, text); } catch (Exception ex) { Console.WriteLine("[Syscle] SMS failed: " + ex.Message); }
    if (!sent)
    {
        Console.WriteLine($"[Syscle] SMS is not set up, so no recovery code was sent to {phone}.");   // the code is a password-equivalent secret: it is only ever shown in Development
        if (env.IsDevelopment()) Console.WriteLine($"[Syscle] (Development only) recovery code for {phone}: {code}");
        if (env.IsDevelopment()) return Results.Ok(new { message = "SMS is not set up on this server, so here is your test code instead: " + code, ticket, devCode = code });
    }
    return Results.Ok(new { message = msg, ticket });
});
auth.MapPost("/reset-sms", async (SmsResetReq r, Db db, HttpContext ctx) =>
{
    if (!PwOk(r.Password)) return Bad(PwMsg);
    using var c = db.Open();
    var t = await Tickets.FindAsync(c, r.Ticket, "sms");
    if (t is null) return Bad("This code expired or was used too many times. Please request a new one.");
    if (t.CodeHash is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Tickets.Hash((r.Code ?? "").Trim())), Encoding.UTF8.GetBytes(t.CodeHash))) { await Tickets.WrongAsync(c, r.Ticket); return Bad("That code is not correct."); }
    await c.ExecuteAsync("UPDATE Users SET PasswordHash=@h, SessionVersion=SessionVersion+1 WHERE UserId=@id", new { h = Pw.Hash(r.Password!), id = t.UserId });
    await Tickets.BurnAsync(c, r.Ticket);
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

admin.MapGet("/store-info", async (Db db, SmsService sms, EmailService mail, IConfiguration cfg) => { using var c = db.Open(); return Results.Ok(new { info = await StoreInfo(c), smsReady = sms.IsReady, emailReady = mail.IsReady, emailVia = mail.Via, publicUrlReady = !string.IsNullOrWhiteSpace(cfg["App:PublicUrl"]) }); });
// Lets the admin prove that email / SMS really work (a real message to an address or number they type) without waiting for a customer to forget a password.
admin.MapPost("/messaging-test", async (MessagingTestReq r, ClaimsPrincipal u, Db db, EmailService mail, SmsService sms) =>
{
    var channel = (r.Channel ?? "").Trim().ToLowerInvariant(); var to = (r.To ?? "").Trim();
    using var c = db.Open(); var key = "msgtest|" + (u.Identity?.Name ?? "admin");
    if (await LoginGuard.IsBlockedAsync(c, key, 10, 60)) return Throttled(); await LoginGuard.FailAsync(c, key);
    string Short(Exception ex) => ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
    if (channel == "email")
    {
        if (to.Length > MaxEmail || !Regex.IsMatch(to, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Bad("Enter a valid email address.");
        if (!mail.IsReady) return Bad("Email is not set up yet. Add the SMTP or SendGrid settings (see docs/EMAIL_SMS_SETUP.md), restart the site and try again.");
        try { return await mail.SendEmailAsync(to, "Web-Syscle test email", "<p>This is a test email from your Web-Syscle store. Password reset emails are sent the same way.</p>") ? Results.Ok(new { ok = true, message = "Test email sent to " + to + ". Check the inbox and the spam folder." }) : Bad("The email provider did not accept the message. The server log says why."); }
        catch (Exception ex) { Console.WriteLine("[Syscle] Test email failed: " + ex.Message); return Bad("The email could not be sent: " + Short(ex)); }
    }
    if (channel == "sms")
    {
        var phone = NormPhone(to); if (phone is null) return Bad("Enter a mobile number, like 09171234567.");
        if (!sms.IsReady) return Bad("SMS is not set up yet. Add the Semaphore API key (see docs/EMAIL_SMS_SETUP.md), restart the site and try again.");
        try { return await sms.SendAsync(phone, "Web-Syscle test message. SMS password recovery is working.") ? Results.Ok(new { ok = true, message = "Test SMS sent to " + phone + "." }) : Bad("The SMS provider did not accept the message. The server log says why (often: no credits left, or the sender name is not approved)."); }
        catch (Exception ex) { Console.WriteLine("[Syscle] Test SMS failed: " + ex.Message); return Bad("The SMS could not be sent: " + Short(ex)); }
    }
    return Bad("Choose email or SMS.");
});
admin.MapPut("/store-info", async (Dictionary<string, string?> r, Db db) =>
{
    using var c = db.Open();
    foreach (var k in StoreKeys) if (r.TryGetValue(k, out var v))
    {
        v = (v ?? "").Trim(); if (v.Length > 200) return Bad("Each field can be up to 200 characters.");
        if ((k == "StoreLat" || k == "StoreLng") && v != "" && !double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return Bad("The store location must be numbers, like 14.5995 and 120.9842.");
        if ((k == "DeliveryMaxKm" || k == "DeliveryBaseFee" || k == "DeliveryPerKm" || k == "CodMax") && v != "" && !(decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var nv) && nv >= 0 && nv <= 5000)) return Bad("Delivery range and prices must be numbers (0 or more).");
        await c.ExecuteAsync("INSERT INTO Settings(SettingKey,SettingValue) VALUES(@k,@v) ON DUPLICATE KEY UPDATE SettingValue=@v", new { k, v });
    }
    Cfg.Clear(); return Results.Ok(new { ok = true });
});

admin.MapPost("/categories", async (HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    var form = await req.ReadFormAsync(); var name = form["name"].ToString().Trim(); string? path = null; var file = form.Files["image"];
    if (file is { Length: > 0 }) { var ext = Path.GetExtension(file.FileName).ToLowerInvariant(); if (BadImageExt(ext) || !await Pics.LooksLikeImageAsync(file)) return Bad("Invalid file."); var dir = Path.Combine(env.WebRootPath, "Icons_and_Buttons"); Directory.CreateDirectory(dir); var fn = $"cat_{Guid.NewGuid():N}{ext}"; await using (var fs = File.Create(Path.Combine(dir, fn))) await file.CopyToAsync(fs); path = "../Icons_and_Buttons/" + fn; }
    if (name.Length < 2 || name.Length > 60) return Bad("Category name must be 2 to 60 characters.");
    using var c = db.Open(); if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Categories WHERE LOWER(Name)=LOWER(@name)", new { name }) > 0) return Conflict("That category already exists.");
    await c.ExecuteAsync("INSERT INTO Categories(Name,ImagePath) VALUES(@name,@path)", new { name, path }); return Results.Ok(new { ok = true });
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
    var rev = Sql.Rev; var rd = Sql.RevDate;
    var orders = await c.QueryFirstAsync($@"SELECT IFNULL(SUM(CASE WHEN {rev} AND DATE({rd})=@today THEN o.Total END),0) AS todaySales, IFNULL(SUM(CASE WHEN DATE(o.CreatedAt)=@today THEN 1 ELSE 0 END),0) AS todayOrders, IFNULL(SUM(CASE WHEN o.Status='Pending' THEN 1 ELSE 0 END),0) AS pendingOrders, IFNULL(SUM(CASE WHEN o.Status IN ('Pending','Preparing','Ready','Out for Delivery') THEN 1 ELSE 0 END),0) AS openOrders, IFNULL(SUM(CASE WHEN {rev} AND DATE({rd})=DATE_SUB(@today, INTERVAL 1 DAY) THEN o.Total END),0) AS yesterdaySales, IFNULL(SUM(CASE WHEN {rev} AND DATE_FORMAT({rd},'%Y-%m')=DATE_FORMAT(@today,'%Y-%m') THEN o.Total END),0) AS monthSales, IFNULL(SUM(CASE WHEN {rev} THEN o.Total END),0) AS totalSales, IFNULL(SUM(CASE WHEN o.RefundStatus='Requested' THEN 1 ELSE 0 END),0) AS refundRequests, IFNULL(SUM(CASE WHEN o.Status NOT IN ('Cancelled','Completed') AND o.ItemsConfirmedAt IS NULL THEN 1 ELSE 0 END),0) AS toConfirm FROM Orders o", new { today = Promos.Today });
    var lowAt = await Cfg.IntAsync(c, "LowStockThreshold", 0, 1000);
    var prods = await c.QueryFirstAsync(@"SELECT COUNT(*) AS products, IFNULL(SUM(CASE WHEN Stock>0 AND Stock<=@lowAt THEN 1 ELSE 0 END),0) AS lowStock, IFNULL(SUM(CASE WHEN Stock=0 THEN 1 ELSE 0 END),0) AS outOfStock FROM Products", new { lowAt });
    var customers = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Role='Customer'");
    var low = await c.QueryAsync("SELECT ProductId AS id, Name AS name, Stock AS stock, CategoryId AS categoryId FROM Products WHERE Stock<=@lowAt ORDER BY Stock, Name LIMIT 6", new { lowAt });
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
    var kind = r.Kind == "Package" ? "Package" : "Bundle"; var bsd = Promos.D(r.StartsAt); var bed = Promos.D(r.EndsAt);
    if (bed != null && bsd != null && bed < bsd) return Bad("The end date must be on or after the start date.");
    var bseason = Nz(r.Season); if (bseason != null && !Seasons.Exists(await Seasons.StatesAsync(c, tx), bseason)) return Bad("Pick a season from the list.");
    var bdesc = Nz(r.Description); if (bdesc != null && bdesc.Length > 500) return Bad("The description is too long (500 characters max).");
    if (!r.Force)
    {
        var sig = string.Join(",", groups.OrderBy(g => g.Key).Select(g => g.Key + "x" + g.Value));
        var existing = (await c.QueryAsync<BundleItemSig>("SELECT BundleId, ProductId, Qty FROM BundleItems", null, tx)).ToList();
        var same = existing.GroupBy(x => x.BundleId).Any(g => string.Join(",", g.OrderBy(x => x.ProductId).Select(x => x.ProductId + "x" + x.Qty)) == sig);
        if (same) return Results.Conflict(new { error = isBogo ? "A Buy 1 Get 1 for this item is already created." : "A bundle with these exact items already exists.", duplicate = true });
    }
 
    var name = Nz(r.Name) ?? (groups.Count == 1 ? (isBogo && r.DiscountPercent == 50 ? "Buy 1 Get 1: " : "Buy " + totalQty + " ") + found[0].Name : string.Join(" + ", found.Select(f => f.Name)));
    if (name.Length > 160) name = name[..160];
    await c.ExecuteAsync("INSERT INTO Bundles(Name,DiscountPercent,IsActive,AutoMade,Kind,Season,StartsAt,EndsAt,Description) VALUES(@name,@pct,1,0,@kind,@bseason,@bsd,@bed,@bdesc)", new { name, pct = r.DiscountPercent, kind, bseason, bsd, bed, bdesc }, tx);
    var id = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)", null, tx);
    foreach (var g in groups) await c.ExecuteAsync("INSERT INTO BundleItems(BundleId,ProductId,Qty) VALUES(@id,@pid,@q)", new { id, pid = g.Key, q = g.Value }, tx);
    tx.Commit(); return Results.Ok(new { ok = true, id });
});
admin.MapPut("/bundles/{id:int}", async (int id, BundleEditReq r, Db db) =>
{
    using var c = db.Open(); var n = await c.ExecuteAsync("UPDATE Bundles SET DiscountPercent = IFNULL(@pct, DiscountPercent), IsActive = IFNULL(@act, IsActive), Description = IFNULL(@desc, Description) WHERE BundleId=@id", new { id, pct = r.DiscountPercent, act = r.IsActive, desc = Nz(r.Description) });
    return n == 0 ? Results.NotFound(new { error = "Not found." }) : Results.Ok(new { ok = true });
});
admin.MapPost("/bundles/{id:int}/image", async (int id, HttpRequest req, Db db, IWebHostEnvironment env) =>
{
    using var c = db.Open(); var (path, error) = await Pics.SaveAsync(req, env.WebRootPath, "Products", "bun", 3_000_000);
    if (error != null) return Bad(error); await c.ExecuteAsync("UPDATE Bundles SET ImagePath=@p WHERE BundleId=@id", new { p = path, id }); return Results.Ok(new { ok = true, path });
});
admin.MapDelete("/bundles/{id:int}", async (int id, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Bundles WHERE BundleId=@id", new { id }); return Results.Ok(new { ok = true }); });

admin.MapGet("/products", async (Db db) =>
{
    using var c = db.Open();
    var prods = (await c.QueryAsync<AdminProductRow>(@"SELECT p.ProductId AS id, p.CategoryId AS categoryId, c.Name AS category, p.ItemType AS itemType, p.Name AS name, p.Price AS price, p.Stock AS stock, p.ImagePath AS image, p.Sizes AS sizes, p.Description AS description, p.Specs AS specs, p.FitTags AS fitTags, p.Sku AS sku, p.CostPrice AS costPrice, p.IsFeatured AS featured FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId ORDER BY c.Name, p.Name")).ToList();
    var allVariants = (await c.QueryAsync<VariantRow>(@"SELECT VariantId AS id, ProductId AS productId, ColorName AS colorName, ImagePath AS image FROM ProductVariants")).ToList();
    foreach (var p in prods) p.variants = allVariants.Where(v => v.productId == p.id).ToList();
    return Results.Ok(prods);
});
string? CheckProduct(ProductReq r) => string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100 ? "Invalid name." : string.IsNullOrWhiteSpace(r.ItemType) || r.ItemType.Trim().Length > 60 ? "Invalid item type." : r.Price < 0 ? "Invalid price." : r.Stock < 0 ? "Invalid stock." : (r.Description ?? "").Length > 2000 ? "The description is too long (2000 characters max)." : (r.Specs ?? "").Length > 2000 ? "The specifications are too long (2000 characters max)." : (r.FitTags ?? "").Length > 250 ? "Fit tags are too long." : (r.Sku ?? "").Trim().Length > 30 || !Regex.IsMatch((r.Sku ?? "").Trim(), @"^(SYS-\d{5})?$", RegexOptions.IgnoreCase) ? "SKU must be SYS- followed by exactly 5 numbers." : r.CostPrice is < 0 or > 10000000 ? "Invalid cost price." : null;
async Task<bool> DuplicateProduct(MySqlConnection c, ProductReq r, int selfId) =>
    await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Products WHERE LOWER(Name)=LOWER(@n) AND CategoryId=@cat AND ProductId<>@selfId", new { n = r.Name!.Trim(), cat = r.CategoryId, selfId }) > 0
    || (Nz(r.Sku) != null && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Products WHERE Sku=@sku AND ProductId<>@selfId", new { sku = Nz(r.Sku)!.ToUpperInvariant(), selfId }) > 0)
    || (r.Variants ?? new List<VariantReq>()).GroupBy(v => (v.ColorName ?? "").Trim().ToLowerInvariant()).Any(g => g.Count() > 1);
admin.MapPost("/products", async (ProductReq r, Db db, ClaimsPrincipal u) =>
{
    var err = CheckProduct(r); if (err != null) return Bad(err);
    using var c = db.Open();
    if (await DuplicateProduct(c, r, 0)) return Conflict("A product with this name already exists in this category.");
    await c.ExecuteAsync("INSERT INTO Products(CategoryId,ItemType,Name,Price,Stock,Sizes,Description,Specs,FitTags,Sku,CostPrice) VALUES(@CategoryId,@t,@n,@Price,@Stock,@sz,@d,@sp,@ft,@sku,@cost)", new { r.CategoryId, t = r.ItemType!.Trim(), n = r.Name!.Trim(), r.Price, r.Stock, sz = Sizing.Normalize(r.Sizes), d = Nz(r.Description), sp = Nz(r.Specs), ft = Nz(r.FitTags), sku = Nz(r.Sku)?.ToUpperInvariant(), cost = r.CostPrice });
    var newId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)");
    await c.ExecuteAsync("UPDATE Products SET Sku=CONCAT('SYS-', LPAD(ProductId,5,'0')) WHERE ProductId=@newId AND Sku IS NULL", new { newId });
    if (r.Stock != 0) await StockLog.Add(c, null, newId, r.Stock, "New item", null, u.Identity?.Name);
    if (r.Variants != null) foreach (var v in r.Variants) await c.ExecuteAsync("INSERT INTO ProductVariants(ProductId,ColorName,ImagePath) VALUES(@newId,@color,@image)", new { newId, color = v.ColorName, image = v.ImagePath });
    return Results.Ok(new { ok = true, id = newId });
});
admin.MapPut("/products/{id:int}", async (int id, ProductReq r, Db db, ClaimsPrincipal u) =>
{
    var err = CheckProduct(r); if (err != null) return Bad(err);
    using var c = db.Open();
    if (await DuplicateProduct(c, r, id)) return Conflict("Another product with this name already exists in this category.");
    var oldStock = await c.ExecuteScalarAsync<int?>("SELECT Stock FROM Products WHERE ProductId=@id", new { id });
    await c.ExecuteAsync("UPDATE Products SET CategoryId=@CategoryId, ItemType=@t, Name=@n, Price=@Price, Stock=@Stock, Sizes=@sz, Description=@d, Specs=@sp, FitTags=@ft, Sku=IFNULL(@sku,Sku), CostPrice=@cost WHERE ProductId=@id", new { id, r.CategoryId, t = r.ItemType!.Trim(), n = r.Name!.Trim(), r.Price, r.Stock, sz = Sizing.Normalize(r.Sizes), d = Nz(r.Description), sp = Nz(r.Specs), ft = Nz(r.FitTags), sku = Nz(r.Sku)?.ToUpperInvariant(), cost = r.CostPrice });
    if (oldStock.HasValue && oldStock.Value != r.Stock) await StockLog.Add(c, null, id, r.Stock - oldStock.Value, "Edited by admin", null, u.Identity?.Name);
    await c.ExecuteAsync("DELETE FROM ProductVariants WHERE ProductId=@id", new { id });
    if (r.Variants != null) foreach (var v in r.Variants) await c.ExecuteAsync("INSERT INTO ProductVariants(ProductId,ColorName,ImagePath) VALUES(@id,@color,@image)", new { id, color = v.ColorName, image = v.ImagePath });
    return Results.Ok(new { ok = true });
});
admin.MapDelete("/products/{id:int}", async (int id, Db db, ClaimsPrincipal u) => { using var c = db.Open(); await c.ExecuteAsync("INSERT INTO StockMovements(ProductId,ProductName,Delta,StockAfter,Reason,ByUser) SELECT NULL, Name, -Stock, 0, 'Item deleted', @by FROM Products WHERE ProductId=@id AND Stock<>0", new { id, by = u.Identity?.Name }); await c.ExecuteAsync("DELETE FROM Products WHERE ProductId=@id", new { id }); return Results.Ok(new { ok = true }); });
admin.MapPost("/products/{id:int}/stock", async (int id, StockReq r, Db db, ClaimsPrincipal u) => { using var c = db.Open(); var n = await c.ExecuteAsync("UPDATE Products SET Stock=Stock+@d WHERE ProductId=@id AND Stock+@d>=0", new { d = r.Delta, id }); if (n > 0) await StockLog.Add(c, null, id, r.Delta, r.Delta > 0 ? "Restock (+)" : "Removed (-)", null, u.Identity?.Name); return Results.Ok(new { ok = true }); });
admin.MapGet("/stock-movements", async (string? search, string? reason, string? startDate, string? endDate, Db db) =>
{
    DateTime? fromDate = DateTime.TryParse(startDate, out var d1) ? d1.Date : null; DateTime? toDate = DateTime.TryParse(endDate, out var d2) ? d2.Date.AddDays(1) : null;
    using var c = db.Open();
    return Results.Ok(await c.QueryAsync(@"SELECT MovementId AS id, ProductId AS productId, ProductName AS product, Delta AS delta, StockAfter AS stockAfter, Reason AS reason, Ref AS reference, ByUser AS byUser, CreatedAt AS date FROM StockMovements
        WHERE (@search IS NULL OR ProductName LIKE CONCAT('%', @search, '%') OR Ref LIKE CONCAT('%', @search, '%')) AND (@reason IS NULL OR Reason = @reason) AND (@fromDate IS NULL OR CreatedAt >= @fromDate) AND (@toDate IS NULL OR CreatedAt < @toDate)
        ORDER BY MovementId DESC LIMIT 500", new { search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(), reason = string.IsNullOrWhiteSpace(reason) ? null : reason, fromDate, toDate }));
});
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
    status = (status == "Pending" || status == "Preparing" || status == "Ready" || status == "Out for Delivery" || status == "Completed" || status == "Cancelled") ? status : null;
    using var c = db.Open();
    var p = new { search = Nz(search), like = "%" + Nz(search) + "%", status, startDate = fromDate, endDate = toDate };
    const string filter = @"(@search IS NULL OR CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) LIKE @like OR o.CustomerName LIKE @like) AND (@startDate IS NULL OR o.CreatedAt >= @startDate) AND (@endDate IS NULL OR o.CreatedAt < @endDate)";
    var rows = await c.QueryAsync(@"SELECT o.OrderId AS id, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS code, o.CreatedAt AS date, o.CustomerName AS customer, o.UserId AS userId, o.PaymentMethod AS payment, o.PaymentRef AS paymentRef, o.PickupDate AS pickup, o.Total AS total, o.Status AS status, o.FulfillmentType AS fulfillment, o.DeliveryAddress AS address, o.DeliveryPhone AS deliveryPhone, o.DeliveryFee AS deliveryFee, o.Courier AS courier, o.TrackingNo AS trackingNo, o.DeliveryKm AS deliveryKm, o.LalamoveStatus AS lalamoveStatus, o.LalamoveLink AS lalamoveLink, o.LalamoveDriver AS lalamoveDriver, o.LalamoveCost AS lalamoveCost, o.PaymentStatus AS paymentStatus, o.RefundStatus AS refundStatus, o.RefundReason AS refundReason, o.RefundNote AS refundNote, (o.ItemsConfirmedAt IS NOT NULL) AS itemsConfirmed, o.ReadyAt AS readyAt, o.CompletedAt AS completedAt, o.PaidAt AS paidAt, o.Discount AS discount, o.PromoCode AS promoCode, (SELECT GROUP_CONCAT(CONCAT(oi.ProductName, IF(oi.Size <> '', CONCAT(' (', oi.Size, ')'), ''), IF(oi.Color <> '', CONCAT(' [', oi.Color, ']'), ''), ' x', oi.Qty) SEPARATOR ', ') FROM OrderItems oi WHERE oi.OrderId = o.OrderId) AS items FROM Orders o WHERE " + filter + " AND (@status IS NULL OR o.Status = @status) ORDER BY o.OrderId DESC", p);
    // the four counters follow the same date range and search as the list, so the numbers always match what is on screen
    var summary = await c.QueryFirstAsync(@"SELECT COUNT(*) AS total, IFNULL(SUM(CASE WHEN o.Status='Completed' THEN 1 ELSE 0 END),0) AS completed, IFNULL(SUM(CASE WHEN o.Status='Pending' THEN 1 ELSE 0 END),0) AS pending, IFNULL(SUM(CASE WHEN o.Status='Preparing' THEN 1 ELSE 0 END),0) AS preparing, IFNULL(SUM(CASE WHEN o.Status='Ready' THEN 1 ELSE 0 END),0) AS ready, IFNULL(SUM(CASE WHEN o.Status='Cancelled' THEN 1 ELSE 0 END),0) AS cancelled FROM Orders o WHERE " + filter, new { p.search, p.like, p.startDate, p.endDate });
    return Results.Ok(new { summary, rows });
});
admin.MapPost("/transactions/walkin", async (WalkinReq r, Db db, ClaimsPrincipal u) =>
{
    if (r.Items == null || r.Items.Count == 0) return Bad("Add items.");
    if (r.Items.Any(i => i.Qty < 1 || i.Qty > 999)) return Bad("Invalid quantity.");
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var method = await c.QueryFirstOrDefaultAsync<string>("SELECT Label FROM PaymentMethods WHERE MethodCode=@m AND IsOn=1 AND MethodCode NOT IN ('paymongo','paypal')", new { m = r.PaymentMethod }, tx);   // online methods need the customer's own device, so they cannot be rung up as already paid
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
    // QR Ph: the sale is saved unpaid (stock is held) and finishes by itself when the customer's scan is paid
    var qr = r.PaymentMethod == "qrph";
    if (qr && total < 20m) return Bad("QR payment needs at least ₱20. Use cash for a smaller sale.");
    await c.ExecuteAsync(qr ? @"INSERT INTO Orders(UserId,CustomerName,Channel,PaymentMethod,Discount,Total,Status,PaymentStatus,ItemsConfirmedAt,ItemsConfirmedBy) VALUES(NULL,@cust,'Walk-in',@method,0,@total,'Preparing','Unpaid',NOW(),@by)"
        : @"INSERT INTO Orders(UserId,CustomerName,Channel,PaymentMethod,Discount,Total,Status,PaymentStatus,PaidAt,PaymentConfirmedBy,ItemsConfirmedAt,ItemsConfirmedBy,CompletedAt) VALUES(NULL,@cust,'Walk-in',@method,0,@total,'Completed','Paid',NOW(),@by,NOW(),@by,NOW())", new { cust, method, total, by = u.Identity?.Name }, tx);
    var orderId = await c.ExecuteScalarAsync<int>("SELECT CAST(LAST_INSERT_ID() AS SIGNED)", null, tx);
    var soldOut = await Orders.TakeStock(c, tx, orderId, lines, "Walk-in sale", "WLK-", u.Identity?.Name); if (soldOut != null) return Conflict($"Out of stock: {soldOut}");
    var orderCode = "WLK-" + orderId.ToString("D6"); tx.Commit(); return Results.Ok(new { orderCode, orderId, total, qr });
});
admin.MapPut("/transactions/{id:int}/status", async (int id, StatusReq r, Db db, EmailService emailer, ClaimsPrincipal u) =>
{
    // Order progress: Pending -> Preparing -> Ready (for pick-up) -> Completed. Cancelled can happen until the order is completed.
    int Rank(string? st) => st switch { "Pending" => 0, "Preparing" => 1, "Ready" => 2, "Out for Delivery" => 3, "Completed" => 4, _ => -1 };
    if (r.Status is not ("Preparing" or "Ready" or "Out for Delivery" or "Completed" or "Cancelled")) return Bad("Unknown status.");
    using var c = db.Open(); using var tx = c.BeginTransaction();
    var ol = await c.QueryFirstOrDefaultAsync<OrderLock>("SELECT Status, PaymentRef, PaymentStatus, FulfillmentType, ItemsConfirmedAt, RefundStatus FROM Orders WHERE OrderId=@id FOR UPDATE", new { id }, tx); var cur = ol?.Status;
    if (cur is null) return Results.NotFound(); if (cur == "Cancelled" || cur == "Completed") return Conflict("This order is already finished.");
    if (r.Status != "Cancelled" && Rank(r.Status) <= Rank(cur)) return Conflict("The order is already at or past that step.");
    if (r.Status == "Out for Delivery" && ol!.FulfillmentType != "Delivery") return Conflict("This is a pick-up order. Mark it Ready instead.");
    if (r.Status != "Cancelled" && ol!.ItemsConfirmedAt == null) return Conflict("Confirm the items first, then move the order forward.");
    if (r.Status == "Completed" && ol!.PaymentStatus != "Paid") return Conflict("The payment is not recorded yet. Confirm the payment first (online payments are recorded automatically).");
    if (ol!.RefundStatus == "Requested" && r.Status != "Cancelled") return Conflict("The customer asked for a refund. Answer the refund request first.");
    if (r.Status == "Cancelled" && ol.PaymentStatus == "Paid" && ol.RefundStatus != "Refunded") return Conflict("This order is already paid. Cancel it through a refund so the money is returned first.");
    if (r.Status == "Cancelled" && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE OrderId=@id AND LalamoveStatus IN ('BOOKING','ASSIGNING_DRIVER','ON_GOING','PICKED_UP','MANUAL')", new { id }, tx) > 0) return Conflict("A Lalamove rider is booked for this order. Cancel the booking first (Delivery button), then cancel the order.");
    if (r.Status == "Cancelled") await StockLog.ReturnOrder(c, tx, id, u.Identity?.Name);
    var courier = Nz(r.Courier); var trackNo = Nz(r.TrackingNo); if (courier != null && courier.Length > 60) courier = courier[..60]; if (trackNo != null && trackNo.Length > 60) trackNo = trackNo[..60];
    await c.ExecuteAsync("UPDATE Orders SET Status=@s, Courier=IFNULL(@courier,Courier), TrackingNo=IFNULL(@trackNo,TrackingNo), ReadyAt=IF(@s IN ('Ready','Out for Delivery'),NOW(),ReadyAt), CompletedAt=IF(@s='Completed',NOW(),CompletedAt) WHERE OrderId=@id", new { s = r.Status, id, courier, trackNo }, tx);
    await Audit.AddAsync(c, u.Identity?.Name, "Order " + r.Status, "Order " + id, tx);
    var orderInfo = await c.QueryFirstOrDefaultAsync<OrderEmailInfo>("SELECT o.CustomerName, u.Email, IFNULL(u.EmailNotify,1) AS EmailNotify, CONCAT(IF(o.Channel='Walk-in','WLK-','ORD-'), LPAD(o.OrderId,6,'0')) AS code FROM Orders o LEFT JOIN Users u ON u.UserId = o.UserId WHERE o.OrderId=@id", new { id }, tx);
    tx.Commit();
    if (orderInfo != null && orderInfo.Email != null && orderInfo.EmailNotify) { try { await emailer.SendOrderStatusEmailAsync(orderInfo.Email, orderInfo.CustomerName!, orderInfo.Code!, r.Status); } catch { } }
    return Results.Ok(new { ok = true });
});

admin.MapPut("/products/{id:int}/featured", async (int id, FeatReq r, Db db) =>
{
    using var c = db.Open();
    if (r.Featured && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Products WHERE IsFeatured=1 AND ProductId<>@id", new { id }) >= await Cfg.IntAsync(c, "MaxFeatured", 1, 50)) return Bad("You reached the featured-item limit (a store rule). Remove one first.");
    var n = await c.ExecuteAsync("UPDATE Products SET IsFeatured=@f WHERE ProductId=@id", new { id, f = r.Featured ? 1 : 0 });
    return n == 0 ? Results.NotFound(new { error = "Not found." }) : Results.Ok(new { ok = true });
});
admin.MapDelete("/reviews/{id:int}", async (int id, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM Reviews WHERE ReviewId=@id", new { id }); return Results.Ok(new { ok = true }); });
admin.MapGet("/top-buyers", async (int? limit, Db db) =>
{
    using var c = db.Open(); var n = Math.Clamp(limit ?? 10, 1, 50);
    return Results.Ok(await c.QueryAsync("SELECT u.UserId AS userId, u.Username AS username, COUNT(*) AS orders, IFNULL(SUM(o.Total),0) AS spent, MAX(o.CreatedAt) AS lastOrder FROM Orders o JOIN Users u ON u.UserId=o.UserId WHERE " + Sql.Rev + " GROUP BY u.UserId, u.Username ORDER BY orders DESC, spent DESC LIMIT @n", new { n }));
});
// Fast / slow-moving products (the label is based on how many units sold in the last N days)
admin.MapGet("/movers", async (int? days, Db db) =>
{
    var n = Math.Clamp(days ?? 30, 7, 365); using var c = db.Open();
    var rows = await Insights.LoadAsync(c, n); var res = Insights.Classify(rows, n);
    return Results.Ok(new { days = n, fast = res.Count(x => x.status == "Fast"), slow = res.Count(x => x.status == "Slow"), items = res });
});
// Simple sales forecast: weekday averages of the last 8 weeks, nudged by the recent trend. It is an estimate, not a promise.
admin.MapGet("/forecast", async (Db db) =>
{
    using var c = db.Open(); return Results.Ok(await Insights.ForecastAsync(c));
});
// Sales reports: one range, compared with the range of the same length right before it.
admin.MapGet("/reports", async (string? period, string? from, string? to, Db db) =>
{
    var per = period is "weekly" or "monthly" or "yearly" ? period : "daily";
    var end = DateTime.TryParse(to, out var t) ? t.Date.AddDays(1) : Promos.Today.AddDays(1);
    var start = DateTime.TryParse(from, out var f) ? f.Date : end.AddDays(per == "daily" ? -14 : per == "weekly" ? -84 : per == "monthly" ? -365 : -1826);
    if (start >= end) return Bad("The start date must be before the end date.");
    if ((end - start).TotalDays > 3700) return Bad("Pick a range of 10 years or less.");
    var span = end - start; var prevStart = start - span;
    var fmt = per == "daily" ? "%Y-%m-%d" : per == "weekly" ? "%x-W%v" : per == "monthly" ? "%Y-%m" : "%Y";
    using var c = db.Open();
    var rev = Sql.Rev; var rd = Sql.RevDate;
    var series = await c.QueryAsync($"SELECT DATE_FORMAT({rd}, @fmt) AS label, IFNULL(SUM(o.Total),0) AS sales, COUNT(*) AS orders FROM Orders o WHERE {rev} AND {rd} >= @start AND {rd} < @end GROUP BY label ORDER BY label", new { fmt, start, end });
    var sum = $"SELECT IFNULL(SUM(o.Total),0) AS sales, COUNT(*) AS orders, IFNULL((SELECT SUM(oi.Qty) FROM OrderItems oi JOIN Orders o2 ON o2.OrderId=oi.OrderId WHERE o2.PaymentStatus='Paid' AND o2.ItemsConfirmedAt IS NOT NULL AND o2.Status<>'Cancelled' AND o2.RefundStatus<>'Refunded' AND IFNULL(o2.PaidAt,o2.CreatedAt) >= @a AND IFNULL(o2.PaidAt,o2.CreatedAt) < @b),0) AS items FROM Orders o WHERE {rev} AND {rd} >= @a AND {rd} < @b";
    var current = await c.QueryFirstAsync(sum, new { a = start, b = end });
    var previous = await c.QueryFirstAsync(sum, new { a = prevStart, b = start });
    var byItem = await c.QueryAsync($"SELECT oi.ProductName AS name, SUM(oi.Qty) AS qty, SUM(oi.UnitPrice*oi.Qty) AS sales FROM OrderItems oi JOIN Orders o ON o.OrderId=oi.OrderId WHERE {rev} AND {rd} >= @start AND {rd} < @end GROUP BY oi.ProductName ORDER BY sales DESC LIMIT 20", new { start, end });
    var byStatus = await c.QueryAsync("SELECT Status AS status, COUNT(*) AS count, IFNULL(SUM(Total),0) AS total FROM Orders WHERE CreatedAt >= @start AND CreatedAt < @end GROUP BY Status", new { start, end });
    var byPayment = await c.QueryAsync($"SELECT o.PaymentMethod AS method, COUNT(*) AS count, IFNULL(SUM(o.Total),0) AS total FROM Orders o WHERE {rev} AND {rd} >= @start AND {rd} < @end GROUP BY o.PaymentMethod ORDER BY total DESC", new { start, end });
    var byChannel = await c.QueryAsync($"SELECT o.Channel AS channel, COUNT(*) AS count, IFNULL(SUM(o.Total),0) AS total FROM Orders o WHERE {rev} AND {rd} >= @start AND {rd} < @end GROUP BY o.Channel", new { start, end });
    var buyers = await c.QueryAsync("SELECT u.Username AS username, COUNT(*) AS orders, IFNULL(SUM(o.Total),0) AS spent FROM Orders o JOIN Users u ON u.UserId=o.UserId WHERE " + rev + " AND " + rd + " >= @start AND " + rd + " < @end GROUP BY u.UserId, u.Username ORDER BY orders DESC, spent DESC LIMIT 10", new { start, end });
    var promos = await c.QueryAsync($"SELECT o.PromoCode AS code, COUNT(*) AS uses, IFNULL(SUM(o.Discount),0) AS given FROM Orders o WHERE o.PromoCode IS NOT NULL AND {rev} AND {rd} >= @start AND {rd} < @end GROUP BY o.PromoCode ORDER BY uses DESC", new { start, end });
    return Results.Ok(new { period = per, from = start.ToString("yyyy-MM-dd"), to = end.AddDays(-1).ToString("yyyy-MM-dd"), prevFrom = prevStart.ToString("yyyy-MM-dd"), series, current, previous, byItem, byStatus, byPayment, byChannel, buyers, promos });
});
admin.MapGet("/reviews", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT r.ReviewId AS id, r.Rating AS rating, r.Comment AS comment, r.CreatedAt AS date, u.Username AS username, p.Name AS product FROM Reviews r JOIN Users u ON u.UserId=r.UserId JOIN Products p ON p.ProductId=r.ProductId ORDER BY r.ReviewId DESC")); });

// ---------- withdraw money from the PayMongo wallet to a GCash account (every step needs the admin's 2FA code) ----------
// Checks the 6-digit code from the admin's authenticator app, exactly like signing in does: the code must be right, must not have been used before, and 5 wrong tries lock it for 10 minutes.
// Returns null when the code is fine, otherwise the message to show.
async Task<string?> Check2FaAsync(MySqlConnection c, ClaimsPrincipal u, string? code)
{
    var usr = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT UserId,Username,TwoFactorSecret,TwoFactorEnabled,TwoFactorLastStep FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (usr is null || !usr.TwoFactorEnabled || string.IsNullOrEmpty(usr.TwoFactorSecret)) return "Turn on two-factor authentication first (My account > Security). Withdrawals need it.";
    var key = "wd2fa|" + usr.UserId;
    if (await LoginGuard.IsBlockedAsync(c, key, 5, 10)) return "Too many wrong codes. Try again in 10 minutes.";
    var totp = new Totp(Base32Encoding.ToBytes(SecretBox.Reveal(usr.TwoFactorSecret)));
    if (!totp.VerifyTotp((code ?? "").Trim(), out var step, new VerificationWindow(1, 1))) { await LoginGuard.FailAsync(c, key); return "Invalid 2FA code."; }
    if (step <= usr.TwoFactorLastStep) return "That code was already used. Wait for the next 6-digit code and try again.";
    await c.ExecuteAsync("UPDATE Users SET TwoFactorLastStep=@step WHERE UserId=@id", new { step, id = usr.UserId });
    await LoginGuard.ClearAsync(c, key);
    return null;
}
decimal WdMin() => builder.Configuration.GetValue("Withdrawals:Min", 100m); decimal WdMax() => builder.Configuration.GetValue("Withdrawals:Max", 50000m); decimal WdFee() => builder.Configuration.GetValue("Withdrawals:Fee", 10m);
string Php(decimal v) => "PHP " + v.ToString("N2");
admin.MapGet("/wallet", async (ClaimsPrincipal u, Db db, PayMongoService pm) =>
{
    using var c = db.Open();
    var (w, err) = await pm.GetWalletAsync();
    var tf = await c.ExecuteScalarAsync<int>("SELECT TwoFactorEnabled FROM Users WHERE UserId=@id", new { id = Uid(u) }) == 1;
    var accounts = await c.QueryAsync("SELECT AccountId AS id, AccountName AS name, AccountNumber AS number FROM WithdrawalAccounts ORDER BY AccountId");
    return Results.Ok(new { ready = pm.IsReady, twoFactor = tf, walletError = err, available = w == null ? (decimal?)null : w.AvailableCentavos / 100m, pending = w == null ? (decimal?)null : w.PendingCentavos / 100m, walletName = w?.AccountName, walletNumber = w?.AccountNumber, accounts, min = WdMin(), max = WdMax(), fee = WdFee() });
});
admin.MapPost("/wallet/accounts", async (WalletAcctReq r, ClaimsPrincipal u, Db db, EmailService mail) =>
{
    var name = Regex.Replace((r.Name ?? "").Trim(), @"\s+", " "); var number = NormPhone(r.Number);
    if (!Regex.IsMatch(name, @"^[\p{L}][\p{L} .'-]{1,79}$")) return Bad("Type the name exactly as it appears on the GCash account.");
    if (number is null) return Bad("Enter the GCash mobile number, like 09171234567.");
    using var c = db.Open();
    var bad = await Check2FaAsync(c, u, r.Code); if (bad != null) return Bad(bad);
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM WithdrawalAccounts") >= 5) return Bad("You can save up to 5 GCash accounts. Remove one first.");
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM WithdrawalAccounts WHERE AccountNumber=@number", new { number }) > 0) return Conflict("That GCash number is already saved.");
    await c.ExecuteAsync("INSERT INTO WithdrawalAccounts(AccountName,AccountNumber,CreatedBy) VALUES(@name,@number,@by)", new { name, number, by = u.Identity?.Name });
    await Audit.AddAsync(c, u.Identity?.Name, "Withdrawal account added", name + " " + number);
    var to = await c.ExecuteScalarAsync<string>("SELECT Email FROM Users WHERE UserId=@id", new { id = Uid(u) });
    if (!string.IsNullOrWhiteSpace(to)) _ = Task.Run(async () => { try { await mail.SendEmailAsync(to, "A GCash account was added for withdrawals", $"<p>The GCash account <b>{System.Net.WebUtility.HtmlEncode(name)}</b> ({number}) was added for withdrawals on your Web-Syscle store. If this was not you, change your password and turn off the admin accounts you do not recognise.</p>"); } catch { } });
    return Results.Ok(new { ok = true });
});
admin.MapDelete("/wallet/accounts/{id:int}", async (int id, ClaimsPrincipal u, Db db) =>
{
    using var c = db.Open(); var n = await c.ExecuteAsync("DELETE FROM WithdrawalAccounts WHERE AccountId=@id", new { id });
    if (n > 0) await Audit.AddAsync(c, u.Identity?.Name, "Withdrawal account removed", "#" + id);
    return n == 0 ? Results.NotFound(new { error = "Not found." }) : Results.Ok(new { ok = true });
});
admin.MapPost("/wallet/withdraw", async (WithdrawReq r, ClaimsPrincipal u, Db db, PayMongoService pm, EmailService mail) =>
{
    using var c = db.Open();
    var bad = await Check2FaAsync(c, u, r.Code); if (bad != null) return Bad(bad);
    var acct = await c.QueryFirstOrDefaultAsync<WAcct>("SELECT AccountId, AccountName, AccountNumber FROM WithdrawalAccounts WHERE AccountId=@id", new { id = r.AccountId });
    if (acct is null) return Bad("Choose the GCash account to send the money to.");
    var amount = Math.Round(r.Amount, 2);
    if (amount < WdMin() || amount > WdMax()) return Bad($"A withdrawal must be between {Php(WdMin())} and {Php(WdMax())}.");
    // only one withdrawal at a time, even if two requests arrive together (a database lock covers the check, the record and the call to PayMongo)
    if (await c.ExecuteScalarAsync<int>("SELECT GET_LOCK('syscle_withdraw', 5)") != 1) return Conflict("Another withdrawal is being processed. Try again in a moment.");
    try
    {
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Withdrawals WHERE Status='Pending'") > 0) return Conflict("The last withdrawal has not finished yet. Wait until it shows Succeeded or Failed.");
        var (w, werr) = await pm.GetWalletAsync(); if (w == null) return Bad(werr ?? "The wallet could not be read.");
        var available = w.AvailableCentavos / 100m;
        if (amount + WdFee() > available) return Bad($"Not enough money in the wallet. Available: {Php(available)}. PayMongo also charges about {Php(WdFee())} for each transfer.");
        var id = await c.ExecuteScalarAsync<int>("INSERT INTO Withdrawals(AccountName,AccountNumber,Amount,Status,RequestedBy) VALUES(@n,@num,@a,'Pending',@by); SELECT LAST_INSERT_ID();", new { n = acct.AccountName, num = acct.AccountNumber, a = amount, by = u.Identity?.Name });
        var reference = "WD-" + id.ToString("D6"); await c.ExecuteAsync("UPDATE Withdrawals SET Reference=@reference WHERE WithdrawalId=@id", new { reference, id });
        var (tid, status, err, unknown) = await pm.SendToGcashAsync(w, acct.AccountName, acct.AccountNumber, amount, reference);
        await c.ExecuteAsync("UPDATE Withdrawals SET Status=@status, TransferId=@tid, Error=@err, UpdatedAt=NOW() WHERE WithdrawalId=@id", new { status, tid, err = err != null && err.Length > 300 ? err[..300] : err, id });
        await Audit.AddAsync(c, u.Identity?.Name, "Withdrawal " + status.ToLowerInvariant(), $"{reference} {Php(amount)} to {acct.AccountNumber}");
        if (status == "Failed")
        {
            var hint = err != null && (err.Contains("recipient", StringComparison.OrdinalIgnoreCase) || err.Contains("cooling", StringComparison.OrdinalIgnoreCase) || err.Contains("register", StringComparison.OrdinalIgnoreCase)) ? " If PayMongo asks you to register or verify the recipient, do that in your PayMongo dashboard (Wallet > Recipients), wait for its waiting period and try again." : "";
            return Bad("PayMongo did not send the money: " + err + hint);
        }
        var to = await c.ExecuteScalarAsync<string>("SELECT Email FROM Users WHERE UserId=@id", new { id = Uid(u) });
        if (!string.IsNullOrWhiteSpace(to)) _ = Task.Run(async () => { try { await mail.SendEmailAsync(to, "Withdrawal " + reference, $"<p>A withdrawal of <b>{Php(amount)}</b> to the GCash account {System.Net.WebUtility.HtmlEncode(acct.AccountName)} ({acct.AccountNumber}) was requested on your Web-Syscle store ({reference}). If this was not you, change your password right away and contact PayMongo.</p>"); } catch { } });
        return Results.Ok(new { ok = true, id, reference, status, unknown, message = unknown ? err : status == "Succeeded" ? $"{Php(amount)} was sent to {acct.AccountName}." : $"{Php(amount)} is on its way to {acct.AccountName}. This page shows when it arrives." });
    }
    finally { await c.ExecuteAsync("SELECT RELEASE_LOCK('syscle_withdraw')"); }
});
admin.MapGet("/wallet/withdrawals", async (Db db, PayMongoService pm) =>
{
    using var c = db.Open();
    // a transfer that was still on its way last time is checked with PayMongo now
    foreach (var p in await c.QueryAsync<WdRow>("SELECT WithdrawalId, Status, TransferId FROM Withdrawals WHERE Status='Pending' AND TransferId IS NOT NULL"))
    {
        var st = await pm.GetTransferStatusAsync(p.TransferId!);
        if (st != null && st != "Pending") await c.ExecuteAsync("UPDATE Withdrawals SET Status=@st, UpdatedAt=NOW() WHERE WithdrawalId=@id", new { st, id = p.WithdrawalId });
    }
    return Results.Ok(await c.QueryAsync("SELECT WithdrawalId AS id, Reference AS reference, AccountName AS name, AccountNumber AS number, Amount AS amount, Status AS status, Error AS error, RequestedBy AS by, RequestedAt AS at FROM Withdrawals ORDER BY WithdrawalId DESC LIMIT 50"));
});
admin.MapGet("/promos", async (Db db) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT Code AS code, DiscountPercent AS percent, IsActive AS isActive, DATE_FORMAT(StartsAt,'%Y-%m-%d') AS startsAt, DATE_FORMAT(EndsAt,'%Y-%m-%d') AS endsAt, MaxUses AS maxUses, MaxPerUser AS maxPerUser, Season AS season, IsPublic AS isPublic, (SELECT COUNT(*) FROM Orders o WHERE o.PromoCode = PromoCodes.Code AND o.Status<>'Cancelled') AS uses FROM PromoCodes ORDER BY Code")); });
admin.MapPost("/promos", async (PromoNewReq r, Db db) =>
{
    var code = (r.Code ?? "").Trim().ToUpperInvariant();
    if (!Regex.IsMatch(code, @"^[A-Z0-9_-]{3,30}$")) return Bad("Code must be 3-30 letters, numbers, dashes or underscores.");
    if (r.Percent < 1 || r.Percent > 90) return Bad("Discount must be 1-90%.");
    var sd = Promos.D(r.StartsAt); var ed = Promos.D(r.EndsAt);
    if (ed != null && sd != null && ed < sd) return Bad("The end date must be on or after the start date.");
    var unlimited = r.Limit == "unlimited"; if (r.Limit == "limited" && r.MaxUses == null) return Bad("Enter how many times the code can be used.");
    if (!unlimited && r.MaxUses is < 1 or > 100000) return Bad("Usage limit must be 1 or more.");
    var perUserUnlimited = r.PerUserLimit == "unlimited"; var perUser = r.PerUser ?? 1;
    if (!perUserUnlimited && perUser is < 1 or > 1000) return Bad("Uses per customer must be 1 or more.");
    var season = Nz(r.Season);
    using var c = db.Open();
    if (season != null && !Seasons.Exists(await Seasons.StatesAsync(c), season)) return Bad("Pick a season from the list.");
    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PromoCodes WHERE Code=@code", new { code }) > 0) return Conflict("That code already exists.");
    await c.ExecuteAsync("INSERT INTO PromoCodes(Code,DiscountPercent,IsActive,StartsAt,EndsAt,MaxUses,MaxPerUser,Season,IsPublic) VALUES(@code,@p,1,@sd,@ed,@mu,@mpu,@season,@pub)", new { code, p = r.Percent, sd, ed, mu = unlimited ? (int?)null : r.MaxUses, mpu = perUserUnlimited ? (int?)null : perUser, season, pub = r.IsPublic });
    await Audit.AddAsync(c, null, "Promo code created", code);
    return Results.Ok(new { ok = true });
});
admin.MapPut("/promos/{code}", async (string code, PromoEditReq r, Db db) =>
{
    if (r.Percent is < 1 or > 90) return Bad("Discount must be 1-90%.");
    using var c = db.Open();
    var ed = Promos.D(r.EndsAt); if (r.MaxUses is < 1 or > 100000) return Bad("Usage limit must be 1 or more.");
    if (r.PerUser is < 1 or > 1000) return Bad("Uses per customer must be 1 or more.");
    var n = await c.ExecuteAsync("UPDATE PromoCodes SET IsActive = IFNULL(@act, IsActive), DiscountPercent = IFNULL(@pct, DiscountPercent), EndsAt = IFNULL(@ed, EndsAt), MaxUses = CASE WHEN @lim='unlimited' THEN NULL ELSE IFNULL(@mu, MaxUses) END, MaxPerUser = CASE WHEN @plim='unlimited' THEN NULL ELSE IFNULL(@pu, MaxPerUser) END, IsPublic = IFNULL(@pub, IsPublic) WHERE Code=@code", new { code, act = r.IsActive, pct = r.Percent, ed, mu = r.MaxUses, lim = r.Limit, pu = r.PerUser, plim = r.PerUserLimit, pub = r.IsPublic });
    return n == 0 ? Results.NotFound(new { error = "Not found." }) : Results.Ok(new { ok = true });
});
admin.MapDelete("/promos/{code}", async (string code, Db db) => { using var c = db.Open(); await c.ExecuteAsync("DELETE FROM PromoCodes WHERE Code=@code", new { code }); return Results.Ok(new { ok = true }); });

admin.MapGet("/payment-methods", async (Db db, PayMongoService pm, PayPalService pp) => { using var c = db.Open(); return Results.Ok(await c.QueryAsync("SELECT MethodCode AS code, Label AS label, IsOn AS isOn, CASE MethodCode WHEN 'paymongo' THEN @pm WHEN 'paypal' THEN @pp ELSE 1 END AS ready FROM PaymentMethods ORDER BY MethodCode", new { pm = pm.IsReady ? 1 : 0, pp = pp.IsReady ? 1 : 0 })); });
admin.MapPut("/payment-methods", async (List<MethodReq> list, Db db) => { using var c = db.Open(); foreach (var m in list) await c.ExecuteAsync("UPDATE PaymentMethods SET IsOn=@IsOn WHERE MethodCode=@Code", new { m.Code, m.IsOn }); return Results.Ok(new { ok = true }); });

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

Features.Map(app);
DeliveryEndpoints.Map(app); QrPay.Map(app);
Receipts.Map(app);
app.Run();

record RegisterReq(string? Username, string? Email, string? Password, string? Phone = null);
record MessagingTestReq(string? Channel, string? To);
record WalletAcctReq(string? Name, string? Number, string? Code);
record WithdrawReq(int AccountId, decimal Amount, string? Code);
class WAcct { public int AccountId { get; set; } public string AccountName { get; set; } = ""; public string AccountNumber { get; set; } = ""; }
class WdRow { public int WithdrawalId { get; set; } public string Reference { get; set; } = ""; public string AccountName { get; set; } = ""; public string AccountNumber { get; set; } = ""; public decimal Amount { get; set; } public string Status { get; set; } = ""; public string? TransferId { get; set; } public string? Error { get; set; } public string? RequestedBy { get; set; } public DateTime RequestedAt { get; set; } }
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
record PromoNewReq(string? Code, int Percent, string? StartsAt = null, string? EndsAt = null, int? MaxUses = null, string? Season = null, string? Limit = null, bool IsPublic = false, int? PerUser = null, string? PerUserLimit = null);
record PromoEditReq(bool? IsActive, int? Percent, string? EndsAt = null, int? MaxUses = null, string? Limit = null, bool? IsPublic = null, int? PerUser = null, string? PerUserLimit = null);
record FeatReq(bool Featured);
record ProfileReq(string? Email, string? Phone);
record PasswordReq(string? Current, string? Next);
record ReviewReq(int Rating, string? Comment);
record VariantReq(string? ColorName, string? ImagePath);
record CartReq(int ProductId, int Qty, string? Size, string? Color);
record QtyReq(int Qty, string? Size, string? Color);
record PromoReq(string? Code);
record CheckoutReq(DateTime? PickupDate, string? PromoCode, string? PaymentMethod, string? PaymentRef = null, string? Fulfillment = null, string? Address = null, string? Phone = null, double? Lat = null, double? Lng = null);
record ItemReq(int ProductId, int Qty, string? Size, string? Color);
record WalkinReq(string? CustomerName, string? PaymentMethod, List<ItemReq>? Items);
record StatusReq(string? Status, string? Courier = null, string? TrackingNo = null);
class OrderLock { public DateTime? ItemsConfirmedAt { get; set; } public string? RefundStatus { get; set; } public string? Status { get; set; } public string? PaymentRef { get; set; } public string? PaymentStatus { get; set; } public string? FulfillmentType { get; set; } }
record ProductReq(int CategoryId, string? ItemType, string? Name, decimal Price, int Stock, string? Sizes, List<VariantReq>? Variants, string? Description = null, string? Specs = null, string? FitTags = null, string? Sku = null, decimal? CostPrice = null);
record StockReq(int Delta);
record MethodReq(string Code, bool IsOn, string? AccountNumber, string? AccountName);
record NewAdminReq(string? Username, string? Email, string? Password);
record SuspendReq(bool Suspend);
record PasswordSetReq(string? Password);
record RoleReq(string Role);
record BundleAddReq(int BundleId, int Qty, Dictionary<string, string>? Sizes);
record BundleNewReq(List<int>? Products, int DiscountPercent, string? Name, bool Force = false, string? Kind = null, string? Season = null, string? StartsAt = null, string? EndsAt = null, string? Description = null);
record BundleEditReq(int? DiscountPercent, bool? IsActive, string? Description = null);

class UserRow { public int UserId { get; set; } public string Username { get; set; } = ""; public string PasswordHash { get; set; } = ""; public string Role { get; set; } = ""; public string Status { get; set; } = ""; public int SessionVersion { get; set; } public string Email { get; set; } = ""; public bool TwoFactorEnabled { get; set; } public string? TwoFactorSecret { get; set; } public long TwoFactorLastStep { get; set; } }
class KV { public string K { get; set; } = ""; public string V { get; set; } = ""; }
class SessionRow { public string Role { get; set; } = ""; public string Status { get; set; } = ""; public int SessionVersion { get; set; } public int IdleFor { get; set; } }
class Line { public int ProductId { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } public int Qty { get; set; } public string Size { get; set; } = ""; public string Color { get; set; } = ""; }
class Db { private readonly string _cs; public Db(string cs) { _cs = cs; } public MySqlConnection Open()
    {
        var c = new MySqlConnection(_cs); c.Open();
        // Every date the database writes with NOW() and every day the reports group by is in shop time (Philippines, UTC+8), the same clock the app uses in Promos.Today.
        // Without this a database running in UTC (the default in Docker and on most hosts) files sales made between midnight and 8 AM under the previous day.
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SET time_zone='" + Promos.SqlOffset + "'"; cmd.ExecuteNonQuery(); }
        return c;
    } }
class BundleRow { public string? Description { get; set; } public string? ImagePath { get; set; } public int BundleId { get; set; } public string Name { get; set; } = ""; public int DiscountPercent { get; set; } public bool IsActive { get; set; } public bool AutoMade { get; set; } public DateTime? StartsAt { get; set; } public DateTime? EndsAt { get; set; } public string? Season { get; set; } public string Kind { get; set; } = "Bundle"; }
class BundleItemRow { public int BundleId { get; set; } public int ProductId { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } public int Stock { get; set; } public string? Image { get; set; } public int Qty { get; set; } public string? Sizes { get; set; } public string? Description { get; set; } public string? Specs { get; set; } public string? FitTags { get; set; } }
class BundleStock { public int ProductId { get; set; } public int Qty { get; set; } public int Stock { get; set; } public string Name { get; set; } = ""; public string? Sizes { get; set; } }
class BundleItemSig { public int BundleId { get; set; } public int ProductId { get; set; } public int Qty { get; set; } }
class BundleProd { public int ProductId { get; set; } public string Name { get; set; } = ""; }
class SizedProduct { public int Stock { get; set; } public string? Sizes { get; set; } }
class BundlePriceRow { public int ProductId { get; set; } public int Qty { get; set; } public decimal Price { get; set; } }
class CartQty { public int ProductId { get; set; } public long Qty { get; set; } }
class CartBundleRow { public int BundleId { get; set; } public int Qty { get; set; } public string Name { get; set; } = ""; public int DiscountPercent { get; set; } }
class Cand { public int ProductId { get; set; } public string Name { get; set; } = ""; public string ItemType { get; set; } = ""; public decimal Price { get; set; } public int Stock { get; set; } public int IdleDays { get; set; } public decimal Sold { get; set; } }
class OrderEmailInfo { public bool EmailNotify { get; set; } = true; public string? CustomerName { get; set; } public string? Email { get; set; } public string? Code { get; set; } }
class ProductRow { public bool featured { get; set; } public double? rating { get; set; } public int reviews { get; set; } public int sold { get; set; } public int id { get; set; } public int categoryId { get; set; } public string category { get; set; } = ""; public string itemType { get; set; } = ""; public string name { get; set; } = ""; public decimal price { get; set; } public int stock { get; set; } public string? image { get; set; } public string? sizes { get; set; } public string? description { get; set; } public string? specs { get; set; } public string? fitTags { get; set; } public string? sku { get; set; } public List<VariantRow> variants { get; set; } = new(); }
class AdminProductRow : ProductRow { public decimal? costPrice { get; set; } }
class VariantRow { public int id { get; set; } public int productId { get; set; } public string colorName { get; set; } = ""; public string? image { get; set; } }

class PromoRow { public string? Season { get; set; } public int Percent { get; set; } public DateTime? StartsAt { get; set; } public DateTime? EndsAt { get; set; } public int? MaxUses { get; set; } public int? MaxPerUser { get; set; } }
class MoverRow { public int ProductId { get; set; } public string Name { get; set; } = ""; public string ItemType { get; set; } = ""; public int Stock { get; set; } public decimal Price { get; set; } public decimal Sold { get; set; } public DateTime? LastSold { get; set; } public DateTime? AddedAt { get; set; } public bool Featured { get; set; } }
class DayRow { public DateTime Day { get; set; } public decimal Sales { get; set; } }
static class Promos
{
    public const string SqlOffset = "+08:00";   // shop time (Philippines, no daylight saving); keep in step with the AddHours(8) below
    public static DateTime Now => DateTime.UtcNow.AddHours(8);
    public static DateTime Today => Now.Date; // shop time (Philippines)
    public static DateTime? D(string? s) => DateTime.TryParse(s, out var d) ? d.Date : null;
    public static string Window(DateTime? s, DateTime? e) => s != null && Today < s.Value.Date ? "Scheduled" : e != null && Today > e.Value.Date ? "Expired" : "Live";
    public static async Task<(int? pct, string? err)> Check(MySqlConnection c, string code, MySqlTransaction? tx, int? userId = null)
    {
        var p = await c.QueryFirstOrDefaultAsync<PromoRow>("SELECT DiscountPercent AS Percent, StartsAt, EndsAt, MaxUses, MaxPerUser, Season FROM PromoCodes WHERE Code=@code AND IsActive=1", new { code }, tx);
        if (p is null) return (null, "Invalid code.");
        if (p.StartsAt != null && Today < p.StartsAt.Value.Date) return (null, "This code starts on " + p.StartsAt.Value.ToString("MMM d, yyyy") + ".");
        if (p.EndsAt != null && Today > p.EndsAt.Value.Date) return (null, "This code has expired.");
        if (p.Season != null) { var (live, why) = Seasons.Check(await Seasons.StatesAsync(c, tx), p.Season); if (!live) return (null, why ?? "This code is out of season."); }
        if (p.MaxUses != null && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE PromoCode=@code AND Status<>'Cancelled'", new { code }, tx) >= p.MaxUses) return (null, "This code has reached its usage limit.");
        // one customer can only use a code as many times as the admin allowed (cancelled orders do not count)
        if (p.MaxPerUser != null && userId != null)
        {
            var mine = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders WHERE PromoCode=@code AND UserId=@userId AND Status<>'Cancelled'", new { code, userId }, tx);
            if (mine >= p.MaxPerUser) return (null, p.MaxPerUser == 1 ? "You have already used this code. It can only be used once per customer." : $"You have already used this code {p.MaxPerUser} times, which is the most one customer can use.");
        }
        return (p.Percent, null);
    }
}
static class Insights
{
    public static async Task<List<MoverRow>> LoadAsync(MySqlConnection c, int days)
    {
        var since = Promos.Today.AddDays(-days);
        return (await c.QueryAsync<MoverRow>(@"SELECT p.ProductId, p.Name, p.ItemType, p.Stock, p.Price, p.AddedAt, p.IsFeatured AS Featured,
            IFNULL((SELECT SUM(oi.Qty) FROM OrderItems oi JOIN Orders o ON o.OrderId=oi.OrderId WHERE oi.ProductId=p.ProductId AND o.Status<>'Cancelled' AND o.RefundStatus<>'Refunded' AND o.CreatedAt>=@since),0) AS Sold,
            (SELECT MAX(o.CreatedAt) FROM OrderItems oi JOIN Orders o ON o.OrderId=oi.OrderId WHERE oi.ProductId=p.ProductId AND o.Status<>'Cancelled') AS LastSold
            FROM Products p", new { since })).ToList();
    }
    public static List<dynamic> Classify(List<MoverRow> rows, int days)
    {
        var sold = rows.Where(r => r.Sold > 0).Select(r => (double)r.Sold).OrderBy(x => x).ToList();
        var cut = sold.Count == 0 ? double.MaxValue : Math.Max(2, sold[(int)Math.Floor((sold.Count - 1) * 0.75)]);
        var res = new List<dynamic>();
        foreach (var r in rows)
        {
            var perDay = (double)r.Sold / days; var last = r.LastSold ?? r.AddedAt ?? Promos.Now; var idle = Math.Max(0, (int)(Promos.Now - last).TotalDays);
            var isNew = r.AddedAt != null && (Promos.Now - r.AddedAt.Value).TotalDays < days;
            var status = (double)r.Sold >= cut && r.Sold > 0 ? "Fast" : r.Stock > 0 && r.Sold <= 1 && !(isNew && r.Sold == 0) ? "Slow" : isNew && r.Sold == 0 ? "New" : "Normal";
            res.Add(new { id = r.ProductId, name = r.Name, itemType = r.ItemType, stock = r.Stock, price = r.Price, sold = r.Sold, perDay = Math.Round(perDay, 2), daysLeft = perDay > 0 ? (int?)Math.Round(r.Stock / perDay) : null, idleDays = idle, lastSold = r.LastSold?.ToString("yyyy-MM-dd"), featured = r.Featured, status });
        }
        return res.OrderByDescending(x => (decimal)x.sold).ThenBy(x => (string)x.name).ToList();
    }
    public static async Task<object> ForecastAsync(MySqlConnection c)
    {
        var today = Promos.Today; var from = today.AddDays(-55);
        var map = (await c.QueryAsync<DayRow>($"SELECT DATE({Sql.RevDate}) AS Day, IFNULL(SUM(o.Total),0) AS Sales FROM Orders o WHERE {Sql.Rev} AND {Sql.RevDate}>=@from GROUP BY DATE({Sql.RevDate})", new { from })).ToDictionary(x => x.Day.Date, x => x.Sales);
        var hist = Enumerable.Range(0, 56).Select(i => (day: from.AddDays(i), sales: map.TryGetValue(from.AddDays(i), out var v) ? v : 0m)).ToList();
        var overall = hist.Sum(h => h.sales) / 56m; var last28 = hist.Skip(28).Sum(h => h.sales); var prev28 = hist.Take(28).Sum(h => h.sales);
        var ratio = prev28 > 0 ? (double)(last28 / prev28) : 1.0; var factor = 1 + (Math.Clamp(ratio, 0.7, 1.3) - 1) / 2;
        var dow = new decimal[7]; for (var d = 0; d < 7; d++) { var xs = hist.Where(h => (int)h.day.DayOfWeek == d).ToList(); dow[d] = xs.Count == 0 ? overall : xs.Sum(h => h.sales) / xs.Count; }
        decimal Day(DateTime d) => Math.Round((dow[(int)d.DayOfWeek] * 0.5m + overall * 0.5m) * (decimal)factor, 2);
        var next = Enumerable.Range(1, 14).Select(i => new { label = today.AddDays(i).ToString("yyyy-MM-dd"), sales = Day(today.AddDays(i)) }).ToList();
        var next7 = Enumerable.Range(1, 7).Sum(i => Day(today.AddDays(i))); var next30 = Enumerable.Range(1, 30).Sum(i => Day(today.AddDays(i)));
        var daysWithSales = hist.Count(h => h.sales > 0);
        var rows = await LoadAsync(c, 30);
        var restock = rows.Where(r => r.Sold > 0).Select(r => { var perDay = (double)r.Sold / 30.0; var need = Math.Ceiling(perDay * 30 * factor * 1.2); return new { id = r.ProductId, name = r.Name, stock = r.Stock, perDay = Math.Round(perDay, 2), daysLeft = (int)Math.Round(r.Stock / perDay), suggest = Math.Max(0, (int)need - r.Stock) }; })
            .OrderBy(x => x.daysLeft).Take(15).ToList();
        return new { history = hist.Skip(28).Select(h => new { label = h.day.ToString("yyyy-MM-dd"), sales = h.sales }), forecast = next, next7, next30, avgDaily = Math.Round(overall, 2), trendPct = Math.Round((ratio - 1) * 100), daysWithSales, confidence = daysWithSales < 14 ? "low" : daysWithSales < 35 ? "fair" : "good", restock };
    }
}
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
        var heads = (await c.QueryAsync<BundleRow>("SELECT BundleId, Name, DiscountPercent, IsActive, AutoMade, StartsAt, EndsAt, Season, Kind, Description, ImagePath FROM Bundles ORDER BY BundleId DESC")).ToList();
        var items = (await c.QueryAsync<BundleItemRow>("SELECT bi.BundleId, p.ProductId, p.Name, p.Price, p.Stock, p.ImagePath AS Image, p.Sizes AS Sizes, p.Description, p.Specs, p.FitTags, bi.Qty FROM BundleItems bi JOIN Products p ON p.ProductId = bi.ProductId")).ToList();
        var list = new List<object>();
        foreach (var h in heads)
        {
            var its = items.Where(i => i.BundleId == h.BundleId).ToList(); if (its.Sum(i => i.Qty) < 2) continue; var inStock = its.All(i => i.Stock >= i.Qty);
            if (activeOnly && (!h.IsActive || !inStock || Promos.Window(h.StartsAt, h.EndsAt) != "Live")) continue; var listPrice = its.Sum(i => i.Price * i.Qty); var price = Math.Round(listPrice * (100 - h.DiscountPercent) / 100m, 2);
            list.Add(new { id = h.BundleId, name = h.Name, discountPercent = h.DiscountPercent, isActive = h.IsActive, auto = h.AutoMade, kind = h.Kind, season = h.Season, description = h.Description, image = h.ImagePath, startsAt = h.StartsAt?.ToString("yyyy-MM-dd"), endsAt = h.EndsAt?.ToString("yyyy-MM-dd"), window = Promos.Window(h.StartsAt, h.EndsAt), inStock, listPrice, price, saving = listPrice - price, items = its.Select(i => new { id = i.ProductId, name = i.Name, price = i.Price, image = i.Image, sizes = i.Sizes, description = i.Description, specs = i.Specs, fitTags = i.FitTags, qty = i.Qty }).ToList() });
        }
        return list;
    }
    public static async Task<(decimal discount, List<object> applied)> ForCart(MySqlConnection c, int uid, MySqlTransaction? tx)
    {
        var cart = (await c.QueryAsync<CartQty>("SELECT ProductId, CAST(SUM(Qty) AS SIGNED) AS Qty FROM CartItems WHERE UserId=@uid GROUP BY ProductId", new { uid }, tx)).ToDictionary(x => x.ProductId, x => (int)x.Qty);
        var cbs = (await c.QueryAsync<CartBundleRow>("SELECT cb.BundleId, cb.Qty, b.Name, b.DiscountPercent FROM CartBundles cb JOIN Bundles b ON b.BundleId = cb.BundleId WHERE cb.UserId=@uid AND b.IsActive=1 AND (b.StartsAt IS NULL OR b.StartsAt <= @today) AND (b.EndsAt IS NULL OR b.EndsAt >= @today) ORDER BY cb.BundleId", new { uid, today = Promos.Today }, tx)).ToList();
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
    // Checks the first bytes of the file (JPEG, PNG or WebP) instead of trusting the file name, so a renamed document or script cannot be uploaded as a picture.
    public static async Task<bool> LooksLikeImageAsync(IFormFile f)
    {
        var h = new byte[12]; await using var s = f.OpenReadStream();
        if (await s.ReadAtLeastAsync(h, 12, false) < 12) return false;
        bool jpg = h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;
        bool png = h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47;
        bool webp = h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50;
        return jpg || png || webp;
    }
    public static async Task<(string? path, string? error)> SaveAsync(HttpRequest req, string webRoot, string folder, string prefix, long maxBytes)
    {
        var form = await req.ReadFormAsync(); var file = form.Files["image"]; if (file is null || file.Length == 0) return (null, "Choose pic.");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant(); if (!new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(ext) || file.Length > maxBytes || !await LooksLikeImageAsync(file)) return (null, "Invalid file.");
        var dir = Path.Combine(webRoot, folder); Directory.CreateDirectory(dir); var fn = $"{prefix}_{Guid.NewGuid():N}{ext}"; await using (var fs = File.Create(Path.Combine(dir, fn))) await file.CopyToAsync(fs);
        return ("/" + folder + "/" + fn, null);
    }
}

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
            await c.ExecuteAsync("INSERT INTO OrderItems(OrderId,ProductId,ProductName,Size,Color,UnitPrice,UnitCost,Qty) VALUES(@orderId,@id,@name,@size,@color,@price,(SELECT CostPrice FROM Products WHERE ProductId=@id),@q)", new { orderId, id = l.ProductId, name = l.Name, size = l.Size, color = l.Color, price = l.Price, q = l.Qty }, tx);
        }
        return null;
    }
}

static class Gate
{
    // Guests (not logged in) may open the landing page, the shop and the category pages. Everything else needs a login.
    public static string? Required(string path)
    {
        var p = path.ToLowerInvariant(); string file = p.Substring(p.LastIndexOf('/') + 1);
        if (p.StartsWith("/pages/"))
        {
            if (file is "error.html" or "homepage.html" or "category-view.html") return null;
            if (file.StartsWith("admin")) return "Admin";
            if (file is "account.html" or "settings.html") return "Any";
            return "Customer";
        }
        if (p.StartsWith("/javascripts/"))
        {
            if (file is "api.js" or "shell.js" or "support.js" or "customer.js" or "category.js") return null;
            if (file.StartsWith("admin")) return "Admin";
            if (file is "account.js" or "settings.js") return "Any";
            return "Customer";
        }
        return null;
    }
}

static class Pw
{
    public static readonly string Dummy = Hash("not-a-real-password");
    public const int Iterations = 600_000;   // current OWASP guidance for PBKDF2-HMAC-SHA256. Old hashes keep working (their own count is stored inside them) and are upgraded the next time that person signs in.
    public static string Hash(string pw) { var salt = RandomNumberGenerator.GetBytes(16); var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, Iterations, HashAlgorithmName.SHA256, 32); return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(h)}"; }
    public static bool NeedsRehash(string stored) { var p = stored.Split('.'); return p.Length == 4 && int.TryParse(p[1], out var n) && n < Iterations; }
    public static bool Verify(string pw, string stored) { var p = stored.Split('.'); if (p.Length != 4) return false; var salt = Convert.FromBase64String(p[2]); var expected = Convert.FromBase64String(p[3]); var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, int.Parse(p[1]), HashAlgorithmName.SHA256, expected.Length); return CryptographicOperations.FixedTimeEquals(h, expected); }
}

static class Auth
{
    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier); var db = ctx.HttpContext.RequestServices.GetRequiredService<Db>(); SessionRow? s = null; var idleLimit = 0;
        if (int.TryParse(id, out var uid))
        {
            using var c = db.Open();
            s = await c.QueryFirstOrDefaultAsync<SessionRow>("SELECT Role, Status, SessionVersion, TIMESTAMPDIFF(SECOND, IFNULL(LastActive, NOW()), NOW()) AS IdleFor FROM Users WHERE UserId=@uid", new { uid });
            await c.ExecuteAsync("UPDATE Users SET LastSeen = NOW() WHERE UserId=@uid AND (LastSeen IS NULL OR LastSeen < DATE_SUB(NOW(), INTERVAL 1 MINUTE))", new { uid });
            if (ctx.Scheme.Name == Schemes.Admin) idleLimit = await Cfg.IntAsync(c, "AdminIdleSeconds", 30, 3600);
        }
        string? why = null;
        if (s is null) why = "gone";
        else if (s.Status != "Active") why = "suspended";
        else if (s.SessionVersion.ToString() != ctx.Principal!.FindFirstValue("ver")) why = "replaced";
        else if (s.Role != ctx.Principal.FindFirstValue(ClaimTypes.Role)) why = "changed";
        else if (ctx.Scheme.Name == Schemes.Admin && s.Role == "Customer") why = "changed";          // the admin cookie can never carry a customer
        else if (ctx.Scheme.Name == Schemes.Customer && s.Role != "Customer") why = "changed";      // and the customer cookie can never carry an admin
        else if (idleLimit > 0 && s.IdleFor > idleLimit) why = "idle";                                  // admin portal auto sign-out when nobody is working
        if (why != null)
        {
            ctx.HttpContext.Items["sy-reason"] = why;
            ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(ctx.Scheme.Name);
        }
    }
}

public class PayMongoService
{
    private readonly IConfiguration _config;
    public PayMongoService(IConfiguration config) { _config = config; }
    string Secret => _config["PayMongo:SecretKey"] ?? "";
    string WebhookSecret => _config["PayMongo:WebhookSecret"] ?? "";
    public bool IsReady => Secret.StartsWith("sk_");
    HttpClient Client()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret + ":")));
        return h;
    }
    // The ways to pay offered on the PayMongo page. "qrph" shows a QR code made for this order's exact total. PayMongo only accepts methods that are switched on in your PayMongo
    // dashboard, so if QR Ph is not active on your account yet, set PayMongo__Methods to "gcash,paymaya,card" until it is.
    static readonly string[] KnownMethods = { "qrph", "gcash", "paymaya", "card", "grab_pay", "dob", "dob_ubp", "brankas_bdo", "brankas_landbank", "brankas_metrobank", "billease", "atome" };
    string[] Methods
    {
        get
        {
            var asked = (_config["PayMongo:Methods"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Where(x => KnownMethods.Contains(x)).Distinct().ToArray();
            return asked.Length > 0 ? asked : new[] { "qrph", "gcash", "paymaya", "card" };
        }
    }
    // Creates a PayMongo hosted checkout page for one order. The customer pays there by scanning a QR code made for the order total, or with GCash, Maya or a card.
    public async Task<(string? id, string? url, string? err)> CreateSessionAsync(string orderCode, int orderId, decimal total, string successUrl, string cancelUrl)
    {
        if (!IsReady) return (null, null, "Online payment is not set up.");
        try
        {
            object Body(string[] methods) => new { data = new { attributes = new {
                send_email_receipt = false, show_description = true, show_line_items = true, description = "Web-Syscle order " + orderCode, reference_number = orderCode,
                line_items = new[] { new { currency = "PHP", amount = (int)Math.Round(total * 100m), name = "Order " + orderCode, quantity = 1 } },
                payment_method_types = (_config["PayMongo:QrPh"] ?? "").ToLowerInvariant() == "true" ? new[] { "gcash", "paymaya", "card", "qrph" } : new[] { "gcash", "paymaya", "card" }, success_url = successUrl, cancel_url = cancelUrl, metadata = new { orderId = orderId.ToString() } } } };
            using var http = Client(); var res = await http.PostAsJsonAsync("https://api.paymongo.com/v1/checkout_sessions", body); var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine("[Syscle] PayMongo answered " + (int)res.StatusCode + " for the methods [" + string.Join(",", Methods) + "]: " + (text.Length > 300 ? text[..300] : text));
                // PayMongo refuses a method that is not switched on for the account (for example QR Ph or online banking before they are activated). Customers must still be able to pay, so try again with the basics.
                var basic = new[] { "card", "gcash", "paymaya" };
                if (res.StatusCode == System.Net.HttpStatusCode.BadRequest && !Methods.All(m => basic.Contains(m)))
                {
                    res = await http.PostAsJsonAsync("https://api.paymongo.com/v1/checkout_sessions", Body(basic)); text = await res.Content.ReadAsStringAsync();
                    if (res.IsSuccessStatusCode) Console.WriteLine("[Syscle] Used card, GCash and Maya only. Activate the other methods in your PayMongo dashboard (Settings > Payment methods) to offer them.");
                }
                if (!res.IsSuccessStatusCode) { Console.WriteLine("[Syscle] PayMongo answered " + (int)res.StatusCode + ": " + (text.Length > 300 ? text[..300] : text)); return (null, null, "The payment page could not be opened. Try again or choose another method."); }
            }
            using var doc = JsonDocument.Parse(text); var data = doc.RootElement.GetProperty("data");
            return (data.GetProperty("id").GetString(), data.GetProperty("attributes").GetProperty("checkout_url").GetString(), null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] PayMongo error: " + ex.Message); return (null, null, "The payment page could not be opened. Try again or choose another method."); }
    }
    // ---------- PayMongo Wallet: read the balance and send money to a GCash account ----------
    public record WalletInfo(string AccountNumber, string AccountName, string Bic, long AvailableCentavos, long PendingCentavos);
    static JsonElement? Find(JsonElement e, string name)   // first property with this name, searched through the whole reply (PayMongo nests things under data / attributes)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            foreach (var p in e.EnumerateObject()) { var f = Find(p.Value, name); if (f != null) return f; }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var i in e.EnumerateArray()) { var f = Find(i, name); if (f != null) return f; }
        return null;
    }
    static long? Num(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v : e is { ValueKind: JsonValueKind.String } s && long.TryParse(s.GetString(), out var w) ? w : null;
    static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    static string ErrText(string text) { try { using var d = JsonDocument.Parse(text); var det = Str(Find(d.RootElement, "detail")); if (!string.IsNullOrWhiteSpace(det)) return det!; } catch { } return text.Length > 200 ? text[..200] : text; }
    static string MapStatus(string? s) { var x = (s ?? "").ToLowerInvariant(); return x.Contains("succe") || x.Contains("complete") || x == "paid" ? "Succeeded" : x.Contains("fail") || x.Contains("reject") || x.Contains("return") || x.Contains("cancel") || x.Contains("expire") ? "Failed" : "Pending"; }

    // The store's PayMongo wallet: how much money is in it, and which account sends the money out.
    public async Task<(WalletInfo? wallet, string? err)> GetWalletAsync()
    {
        if (!IsReady) return (null, "PayMongo is not connected yet. Add PayMongo__SecretKey and restart the site.");
        try
        {
            using var http = Client(); var id = (_config["PayMongo:WalletId"] ?? "").Trim();
            var res = await http.GetAsync("https://api.paymongo.com/v2/wallets" + (id.Length > 0 ? "/" + Uri.EscapeDataString(id) : "")); var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) { Console.WriteLine("[Syscle] PayMongo wallet answered " + (int)res.StatusCode + ": " + (text.Length > 300 ? text[..300] : text)); return (null, "PayMongo would not show the wallet: " + ErrText(text) + " (the PayMongo account needs an activated Wallet)."); }
            using var doc = JsonDocument.Parse(text); var w = doc.RootElement;
            if (Find(w, "data") is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0) w = list[0];
            var avail = Num(Find(w, "available_balance")) ?? Num(Find(w, "available")) ?? Num(Find(w, "balance"));
            var pend = Num(Find(w, "pending_balance")) ?? Num(Find(w, "pending")) ?? 0;
            var src = Find(w, "source_account");
            string? number = Str(src is { } s1 ? Find(s1, "number") : null) ?? Str(Find(w, "account_number")), name = Str(src is { } s2 ? Find(s2, "name") : null) ?? Str(Find(w, "account_name"));
            var cn = _config["PayMongo:WalletAccountNumber"]; var cname = _config["PayMongo:WalletAccountName"];
            if (!string.IsNullOrWhiteSpace(cn)) number = cn.Trim(); if (!string.IsNullOrWhiteSpace(cname)) name = cname.Trim();   // settings win when the admin filled them in
            var bic = Str(src is { } s3 ? Find(s3, "bic") : null) ?? "PAEYPHM2XXX";
            if (avail == null || string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name))
            { Console.WriteLine("[Syscle] Could not read the PayMongo wallet reply: " + (text.Length > 300 ? text[..300] : text)); return (null, "The wallet reply from PayMongo could not be read. Set PayMongo__WalletAccountNumber and PayMongo__WalletAccountName (shown in your PayMongo dashboard under Wallet), restart, and try again."); }
            return (new WalletInfo(number!, name!, bic, avail.Value, pend), null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] PayMongo wallet error: " + ex.Message); return (null, "PayMongo could not be reached right now. Try again in a moment."); }
    }
    string? _gcashBic;
    // GCash's bank code in PayMongo's list of InstaPay receivers (found once, then remembered).
    async Task<string> GcashBicAsync()
    {
        if (_gcashBic != null) return _gcashBic;
        string? found = null;
        void Walk(JsonElement e)
        {
            if (found != null) return;
            if (e.ValueKind == JsonValueKind.Object)
            {
                string? P(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var nm = P("name") ?? P("display_name") ?? P("description"); var code = P("provider_code") ?? P("bic") ?? P("code");
                if (nm != null && code != null && (nm.Contains("gcash", StringComparison.OrdinalIgnoreCase) || nm.Contains("g-xchange", StringComparison.OrdinalIgnoreCase) || nm.Contains("gxchange", StringComparison.OrdinalIgnoreCase))) { found = code; return; }
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var i in e.EnumerateArray()) Walk(i);
        }
        try { using var http = Client(); var text = await http.GetStringAsync("https://api.paymongo.com/v1/wallets/receiving_institutions?provider=instapay"); using var doc = JsonDocument.Parse(text); Walk(doc.RootElement); }
        catch (Exception ex) { Console.WriteLine("[Syscle] Could not load PayMongo's receiving banks: " + ex.Message); }
        return _gcashBic = found ?? "GXCHPHM2XXX";
    }
    // Sends money from the wallet to a GCash account. "unknown" = PayMongo may or may not have received the request: the caller must NOT send it again.
    public async Task<(string? id, string status, string? err, bool unknown)> SendToGcashAsync(WalletInfo w, string accountName, string accountNumber, decimal amount, string reference)
    {
        try
        {
            var bic = await GcashBicAsync();
            var body = new { transfers = new[] { new {
                source_account = new { number = w.AccountNumber, name = w.AccountName, bic = w.Bic },
                destination_account = new { number = accountNumber, name = accountName, bic },
                amount = (long)Math.Round(amount * 100m), currency = "PHP", provider = "instapay", description = "Web-Syscle withdrawal " + reference, reference_number = reference } } };
            using var http = Client(); HttpResponseMessage res;
            try { res = await http.PostAsJsonAsync("https://api.paymongo.com/v2/batch_transfers", body); }
            catch (Exception ex) { Console.WriteLine("[Syscle] Withdrawal " + reference + ": no answer from PayMongo: " + ex.Message); return (null, "Pending", "PayMongo did not answer. Check the PayMongo dashboard (Wallet > Transactions) before trying again, because the money may already be on its way.", true); }
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine("[Syscle] Withdrawal " + reference + ": PayMongo answered " + (int)res.StatusCode + ": " + (text.Length > 300 ? text[..300] : text));
                if ((int)res.StatusCode >= 500) return (null, "Pending", "PayMongo had a problem. Check the PayMongo dashboard (Wallet > Transactions) before trying again, because the money may already be on its way.", true);
                return (null, "Failed", ErrText(text), false);
            }
            using var doc = JsonDocument.Parse(text); var tr = Find(doc.RootElement, "transfers") is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 ? arr[0] : doc.RootElement;
            return (Str(Find(tr, "id")), MapStatus(Str(Find(tr, "status"))), null, false);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] Withdrawal " + reference + " error: " + ex.Message); return (null, "Pending", "Something went wrong while sending. Check the PayMongo dashboard (Wallet > Transactions) before trying again.", true); }
    }
    // Asks PayMongo how a transfer ended up (null = could not find out).
    public async Task<string?> GetTransferStatusAsync(string transferId)
    {
        try { using var http = Client(); var text = await http.GetStringAsync("https://api.paymongo.com/v2/transfers/" + Uri.EscapeDataString(transferId)); using var doc = JsonDocument.Parse(text); var st = Str(Find(doc.RootElement, "status")); return st == null ? null : MapStatus(st); }
        catch (Exception ex) { Console.WriteLine("[Syscle] Could not read transfer " + transferId + ": " + ex.Message); return null; }
    }
    // Asks PayMongo whether a checkout session has been paid.
    public async Task<bool> IsPaidAsync(string sessionId)
    {
        if (!IsReady) return false;
        try
        {
            using var http = Client(); var text = await http.GetStringAsync("https://api.paymongo.com/v1/checkout_sessions/" + Uri.EscapeDataString(sessionId));
            using var doc = JsonDocument.Parse(text); var a = doc.RootElement.GetProperty("data").GetProperty("attributes");
            if (a.TryGetProperty("payments", out var pays) && pays.ValueKind == JsonValueKind.Array)
                foreach (var p in pays.EnumerateArray()) if (p.GetProperty("attributes").GetProperty("status").GetString() == "paid") return true;
            if (a.TryGetProperty("payment_intent", out var pi) && pi.ValueKind == JsonValueKind.Object && pi.GetProperty("attributes").GetProperty("status").GetString() == "succeeded") return true;
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] PayMongo check failed: " + ex.Message); }
        return false;
    }
    // PayMongo signs every webhook: header looks like "t=123,te=abc,li=def"; the signature is HMAC-SHA256 of "t.payload" with your webhook secret.
    public bool VerifySignature(string payload, string header)
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret) || string.IsNullOrWhiteSpace(header)) return false;
        var parts = header.Split(',').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim(), x => x[1].Trim());
        if (!parts.TryGetValue("t", out var t)) return false;
        var mine = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(t + "." + payload))).ToLowerInvariant();
        foreach (var k in new[] { "te", "li" }) if (parts.TryGetValue(k, out var sig) && sig.Length == mine.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sig), Encoding.UTF8.GetBytes(mine))) return true;
        return false;
    }
}

// PayPal Checkout (Orders v2). Sandbox keys work for testing; set PayPal:Mode to "live" with live keys for real money.
public class PayPalService
{
    private readonly IConfiguration _config;
    public PayPalService(IConfiguration config) { _config = config; }
    string ClientId => _config["PayPal:ClientId"] ?? "";
    string Secret => _config["PayPal:Secret"] ?? "";
    string Currency => string.IsNullOrWhiteSpace(_config["PayPal:Currency"]) ? "PHP" : _config["PayPal:Currency"]!;
    string Base => string.Equals(_config["PayPal:Mode"], "live", StringComparison.OrdinalIgnoreCase) ? "https://api-m.paypal.com" : "https://api-m.sandbox.paypal.com";
    public bool IsReady => ClientId.Length > 10 && Secret.Length > 10 && !ClientId.Contains("PASTE") && !Secret.Contains("PASTE");
    async Task<HttpClient?> ClientAsync()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/v1/oauth2/token") { Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + Secret)));
        var res = await http.SendAsync(req); var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) { Console.WriteLine("[Syscle] PayPal login answered " + (int)res.StatusCode + ": " + (text.Length > 200 ? text[..200] : text)); return null; }
        using var doc = JsonDocument.Parse(text); http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("access_token").GetString());
        return http;
    }
    // Creates a PayPal order and returns the page where the customer logs in to PayPal and approves the payment. The id is stored as "pp_" + PayPal order id.
    public async Task<(string? id, string? url, string? err)> CreateOrderAsync(string orderCode, decimal total, string returnUrl, string cancelUrl)
    {
        const string fail = "The PayPal page could not be opened. Try again or choose another method.";
        if (!IsReady) return (null, null, "PayPal is not set up.");
        try
        {
            using var http = await ClientAsync(); if (http is null) return (null, null, fail);
            var body = new { intent = "CAPTURE", purchase_units = new[] { new { reference_id = orderCode, description = "Web-Syscle order " + orderCode, amount = new { currency_code = Currency, value = total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) } } },
                payment_source = new { paypal = new { experience_context = new { brand_name = "Web-Syscle", user_action = "PAY_NOW", shipping_preference = "NO_SHIPPING", return_url = returnUrl, cancel_url = cancelUrl } } } };
            http.DefaultRequestHeaders.Add("PayPal-Request-Id", orderCode + "-" + Guid.NewGuid().ToString("N")[..8]);
            var res = await http.PostAsJsonAsync(Base + "/v2/checkout/orders", body); var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) { Console.WriteLine("[Syscle] PayPal answered " + (int)res.StatusCode + ": " + (text.Length > 300 ? text[..300] : text)); return (null, null, fail); }
            using var doc = JsonDocument.Parse(text); var id = doc.RootElement.GetProperty("id").GetString(); string? url = null;
            foreach (var l in doc.RootElement.GetProperty("links").EnumerateArray()) { var rel = l.GetProperty("rel").GetString(); if (rel is "payer-action" or "approve") { url = l.GetProperty("href").GetString(); break; } }
            return url is null ? (null, null, fail) : ("pp_" + id, url, null);
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] PayPal error: " + ex.Message); return (null, null, fail); }
    }
    // True when the order is paid. If the customer approved it but the money was not taken yet, it is captured here.
    public async Task<bool> IsPaidAsync(string paypalOrderId)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(paypalOrderId)) return false;
        try
        {
            using var http = await ClientAsync(); if (http is null) return false;
            var text = await http.GetStringAsync(Base + "/v2/checkout/orders/" + Uri.EscapeDataString(paypalOrderId));
            using var doc = JsonDocument.Parse(text); var status = doc.RootElement.GetProperty("status").GetString();
            if (status == "COMPLETED") return true;
            if (status != "APPROVED") return false;
            var cap = new HttpRequestMessage(HttpMethod.Post, Base + "/v2/checkout/orders/" + Uri.EscapeDataString(paypalOrderId) + "/capture") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            var res = await http.SendAsync(cap); var ct = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode) { using var d2 = JsonDocument.Parse(ct); return d2.RootElement.GetProperty("status").GetString() == "COMPLETED"; }
            if (ct.Contains("ORDER_ALREADY_CAPTURED")) return true;
            Console.WriteLine("[Syscle] PayPal capture answered " + (int)res.StatusCode + ": " + (ct.Length > 300 ? ct[..300] : ct));
        }
        catch (Exception ex) { Console.WriteLine("[Syscle] PayPal check failed: " + ex.Message); }
        return false;
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
        var form = new Dictionary<string, string> { ["apikey"] = Key, ["number"] = phone, ["message"] = text };
        var sender = _config["SmsSettings:SenderName"]; if (!string.IsNullOrWhiteSpace(sender)) form["sendername"] = sender;   // only send it when one is set
        var res = await http.PostAsync("https://api.semaphore.co/api/v4/messages", new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        Console.WriteLine("[Syscle] Semaphore answered " + (int)res.StatusCode + ": " + (body.Length > 300 ? body[..300] : body));   // visible in the Render logs
        return res.IsSuccessStatusCode;
    }
}

public class EmailService
{
    private readonly IConfiguration _config;
    public EmailService(IConfiguration config) { _config = config; }
    public bool IsReady => !string.IsNullOrWhiteSpace(_config["EmailSettings:SmtpHost"]) || RealSendGridKey();
    public string? Via => !string.IsNullOrWhiteSpace(_config["EmailSettings:SmtpHost"]) ? "SMTP" : RealSendGridKey() ? "SendGrid" : null;
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
            using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
            await smtp.ConnectAsync(host, _config.GetValue("EmailSettings:SmtpPort", 587), MailKit.Security.SecureSocketOptions.Auto);   // Auto: port 587 uses STARTTLS, port 465 uses SSL
            if (!string.IsNullOrEmpty(smtpUser)) await smtp.AuthenticateAsync(smtpUser, _config["EmailSettings:SmtpPassword"]);
            await smtp.SendAsync(msg); await smtp.DisconnectAsync(true);
            return true;
        }
        if (RealSendGridKey())
        {
            var client = new SendGridClient(_config["EmailSettings:SendGridApiKey"]);
            var m = MailHelper.CreateSingleEmail(new EmailAddress(senderEmail, senderName), new EmailAddress(toEmail), subject, "", htmlContent);
            var res = await client.SendEmailAsync(m);
            if (!res.IsSuccessStatusCode) { var body = await res.Body.ReadAsStringAsync(); Console.WriteLine("[Syscle] SendGrid answered " + (int)res.StatusCode + ": " + (body.Length > 300 ? body[..300] : body) + "  (the sender address must be verified in SendGrid)"); }
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
        var line = status switch
        {
            "Preparing" => "We are now <b>preparing</b> your order.",
            "Ready" => "Your order is <b>ready</b> (packed). Pick-up orders: please bring your order number. Delivery orders: we will hand it to the rider soon.",
            "Out for Delivery" => "Your order is <b>out for delivery</b>. Please keep your phone nearby so the rider can reach you.",
            "Completed" => "Your order is <b>completed</b>. Thank you for shopping with us!",
            "Cancelled" => "Your order was <b>cancelled</b>. The items went back to stock.",
            _ => $"Your order is now <b>{status}</b>."
        };
        var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(username)},</p><p>Order <b>{orderCode}</b>: {line}</p>";
        return SendEmailAsync(toEmail, $"Order {orderCode}: {status}", html);
    }
    public Task<bool> SendOrderPlacedEmailAsync(string toEmail, string username, string orderCode, decimal total, DateTime pickup, string items)
    {
        var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(username)},</p><p>Thank you! We received your order <b>{orderCode}</b>.</p><p>{System.Net.WebUtility.HtmlEncode(items)}</p><p>Total: <b>PHP {total:N2}</b><br>Pick-up date: <b>{pickup:MMMM d, yyyy}</b></p><p>You can follow every step of your order in <b>My Orders</b> on the website.</p>";
        return SendEmailAsync(toEmail, $"Order {orderCode} received", html);
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
    // The database sets itself up when the app starts: it creates the database if it is missing, runs Database/schema.sql, adds any column an
    // older version did not have, then runs Database/seed.sql (starter data). Nobody has to run a SQL file by hand.
    static async Task AddColumn(MySqlConnection c, string table, string column, string ddl)
    {
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column", new { table, column }) == 0)
            await c.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {ddl}");
    }
    static async Task Migrate(MySqlConnection c)
    {
        // Users
        await AddColumn(c, "Users", "EmailNotify", "TINYINT(1) NOT NULL DEFAULT 1");
        await AddColumn(c, "Users", "TwoFactorLastStep", "BIGINT NULL");
        await AddColumn(c, "Users", "LastActive", "DATETIME NULL");
        await c.ExecuteAsync("ALTER TABLE Users MODIFY TwoFactorSecret VARCHAR(200) NULL");
        // Products
        await AddColumn(c, "Products", "IsFeatured", "TINYINT(1) NOT NULL DEFAULT 0");
        await AddColumn(c, "Products", "CostPrice", "DECIMAL(10,2) NULL");
        await AddColumn(c, "Products", "Description", "TEXT NULL");
        await AddColumn(c, "Products", "Specs", "TEXT NULL");
        await AddColumn(c, "Products", "FitTags", "VARCHAR(250) NULL");
        await AddColumn(c, "Products", "Sku", "VARCHAR(30) NULL");
        // Orders
        await AddColumn(c, "Orders", "PaymentRef", "VARCHAR(60) NULL");
        await c.ExecuteAsync("ALTER TABLE Orders MODIFY PaymentRef VARCHAR(60) NULL");
        var st = await c.ExecuteScalarAsync<string>("SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME='Orders' AND COLUMN_NAME='Status'");
        if (st != null && !st.Contains("Out for Delivery")) await c.ExecuteAsync("ALTER TABLE Orders MODIFY Status ENUM('Pending','Preparing','Ready','Out for Delivery','Completed','Cancelled') NOT NULL");
        await AddColumn(c, "Orders", "FulfillmentType", "VARCHAR(10) NOT NULL DEFAULT 'Pickup'");
        await AddColumn(c, "Orders", "DeliveryAddress", "VARCHAR(250) NULL");
        await AddColumn(c, "Orders", "DeliveryPhone", "VARCHAR(20) NULL");
        await AddColumn(c, "Orders", "DeliveryFee", "DECIMAL(10,2) NOT NULL DEFAULT 0");
        await AddColumn(c, "Orders", "Courier", "VARCHAR(60) NULL");
        await AddColumn(c, "Orders", "TrackingNo", "VARCHAR(60) NULL");
        await AddColumn(c, "Orders", "DeliveryLat", "DECIMAL(9,6) NULL"); await AddColumn(c, "Orders", "DeliveryLng", "DECIMAL(9,6) NULL"); await AddColumn(c, "Orders", "DeliveryKm", "DECIMAL(6,1) NULL");
        await AddColumn(c, "Orders", "LalamoveOrderId", "VARCHAR(40) NULL"); await AddColumn(c, "Orders", "LalamoveStatus", "VARCHAR(20) NULL"); await AddColumn(c, "Orders", "LalamoveLink", "VARCHAR(400) NULL");
        await AddColumn(c, "Orders", "LalamoveDriver", "VARCHAR(120) NULL"); await AddColumn(c, "Orders", "LalamoveCost", "DECIMAL(10,2) NULL"); await AddColumn(c, "Orders", "LalamoveCheckedAt", "DATETIME NULL");
        bool hadPaid = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME='Orders' AND COLUMN_NAME='PaymentStatus'") > 0;
        await AddColumn(c, "Orders", "PaymentStatus", "VARCHAR(10) NOT NULL DEFAULT 'Unpaid'");
        await AddColumn(c, "Orders", "PaidAt", "DATETIME NULL");
        await AddColumn(c, "Orders", "PaymentConfirmedBy", "VARCHAR(50) NULL");
        await AddColumn(c, "Orders", "CancelReason", "VARCHAR(120) NULL");
        await AddColumn(c, "Orders", "ItemsConfirmedAt", "DATETIME NULL");
        await AddColumn(c, "Orders", "ItemsConfirmedBy", "VARCHAR(50) NULL");
        await AddColumn(c, "Orders", "ReadyAt", "DATETIME NULL");
        await AddColumn(c, "Orders", "CompletedAt", "DATETIME NULL");
        await AddColumn(c, "Orders", "RefundStatus", "VARCHAR(12) NOT NULL DEFAULT 'None'");
        await AddColumn(c, "Orders", "RefundReason", "VARCHAR(300) NULL");
        await AddColumn(c, "Orders", "RefundAmount", "DECIMAL(10,2) NULL");
        await AddColumn(c, "Orders", "RefundRequestedAt", "DATETIME NULL");
        await AddColumn(c, "Orders", "RefundedAt", "DATETIME NULL");
        await AddColumn(c, "Orders", "RefundNote", "VARCHAR(300) NULL");
        if (!hadPaid) await c.ExecuteAsync("UPDATE Orders SET PaymentStatus='Paid', PaidAt=IFNULL(PaidAt,CreatedAt), ItemsConfirmedAt=IFNULL(ItemsConfirmedAt,CreatedAt), CompletedAt=IFNULL(CompletedAt,CreatedAt) WHERE Status='Completed'");
        await AddColumn(c, "OrderItems", "UnitCost", "DECIMAL(10,2) NULL");
        // Online payments now all go through PayMongo (QR Ph, GCash, Maya, card): the manual GCash method and the fixed payment QR are retired. Past orders keep their "GCash" label in the history.
        await c.ExecuteAsync("DELETE FROM PaymentMethods WHERE MethodCode='gcash'");
        await c.ExecuteAsync("DELETE FROM Settings WHERE SettingKey IN ('ShowPaymentQr','PaymentQrImage')");
        await c.ExecuteAsync("DELETE FROM PaymentMethods WHERE MethodCode='paypal'");   // every online payment now goes through PayMongo; past PayPal orders keep their label in the history
        await c.ExecuteAsync("UPDATE Orders SET PaymentMethod='QR, GCash, Maya or card (online)' WHERE PaymentMethod='GCash / Maya / Card (online)'");   // the order's label must keep matching the method's label
        await c.ExecuteAsync("UPDATE PaymentMethods SET Label='QR, GCash, Maya or card (online)' WHERE MethodCode='paymongo' AND Label='GCash / Maya / Card (online)'");
        // Promos and bundles
        await AddColumn(c, "PromoCodes", "StartsAt", "DATE NULL"); await AddColumn(c, "PromoCodes", "EndsAt", "DATE NULL");
        await AddColumn(c, "PromoCodes", "MaxUses", "INT NULL"); await AddColumn(c, "PromoCodes", "Season", "VARCHAR(30) NULL");
        await AddColumn(c, "PromoCodes", "IsPublic", "TINYINT(1) NOT NULL DEFAULT 0");
        await AddColumn(c, "PromoCodes", "MaxPerUser", "INT NULL");   // how many times ONE customer may use the code (empty = no limit per customer)
        await AddColumn(c, "Bundles", "StartsAt", "DATE NULL"); await AddColumn(c, "Bundles", "EndsAt", "DATE NULL");
        await AddColumn(c, "Bundles", "Season", "VARCHAR(30) NULL"); await AddColumn(c, "Bundles", "Kind", "VARCHAR(10) NOT NULL DEFAULT 'Bundle'");
        await AddColumn(c, "Bundles", "Description", "VARCHAR(500) NULL"); await AddColumn(c, "Bundles", "ImagePath", "VARCHAR(260) NULL");
        // in-shop QR Ph payment (shown on the sale screen only; never offered to online customers)
        await c.ExecuteAsync("INSERT IGNORE INTO PaymentMethods (MethodCode, Label, IsOn) VALUES ('qrph','QR Ph (scan to pay)',1)");
        // payment buttons are Cash, GCash and PayMongo. The old combined label becomes "PayMongo" (orders follow), and PayPal is switched off once
        await c.ExecuteAsync("UPDATE Orders SET PaymentMethod='PayMongo' WHERE PaymentMethod='GCash / Maya / Card (online)'");
        if (await c.ExecuteAsync("UPDATE PaymentMethods SET Label='PayMongo' WHERE MethodCode='paymongo' AND Label='GCash / Maya / Card (online)'") > 0)
            await c.ExecuteAsync("UPDATE PaymentMethods SET IsOn=0 WHERE MethodCode='paypal'");
        // a unique index on SKU (ignored when it already exists)
        try { await c.ExecuteAsync("CREATE UNIQUE INDEX UX_Products_Sku ON Products(Sku)"); } catch (MySqlException) { }
    }

    // Splits a SQL file into statements. A semicolon or "--" inside quotes does not count, so comments and text values are safe.
    static IEnumerable<string> Split(string sql)
    {
        var sb = new StringBuilder(); char q = '\0';
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (q != '\0') { sb.Append(ch); if (ch == '\\' && q != '`' && i + 1 < sql.Length) { sb.Append(sql[++i]); continue; } if (ch == q) q = '\0'; continue; }
            if (ch is '\'' or '"' or '`') { q = ch; sb.Append(ch); continue; }
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; sb.Append('\n'); continue; }
            if (ch == ';') { var t = sb.ToString().Trim(); sb.Clear(); if (t.Length > 0) yield return t; continue; }
            sb.Append(ch);
        }
        var last = sb.ToString().Trim(); if (last.Length > 0) yield return last;
    }
    static async Task RunFile(MySqlConnection c, string name)
    {
        var path = new[] { Path.Combine(AppContext.BaseDirectory, "Database", name), Path.Combine(Directory.GetCurrentDirectory(), "Database", name), Path.Combine(Directory.GetCurrentDirectory(), "..", "Database", name) }.FirstOrDefault(File.Exists);
        if (path is null) { Console.WriteLine("[Syscle] " + name + " was not found, skipped."); return; }
        foreach (var stmt in Split(await File.ReadAllTextAsync(path))) await c.ExecuteAsync(stmt);
    }

    public static async Task RunAsync(string cs, IConfiguration cfg)
    {
        // the database container can need a few seconds after a restart, so try for about 40 seconds before giving up
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                var b = new MySqlConnectionStringBuilder(cs); var dbName = b.Database; b.Database = "";
                if (!string.IsNullOrEmpty(dbName))
                {
                    using var root = new MySqlConnection(b.ConnectionString); await root.OpenAsync();
                    if (!System.Text.RegularExpressions.Regex.IsMatch(dbName, @"^[A-Za-z0-9_]+$")) throw new InvalidOperationException("Database name may only use letters, numbers and underscores.");
                    await root.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{dbName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
                }
                using var c = new MySqlConnection(cs); await c.OpenAsync(); await c.ExecuteAsync("SET time_zone='" + Promos.SqlOffset + "'");   // same shop-time clock as Db.Open
                await RunFile(c, "schema.sql"); await Migrate(c); await RunFile(c, "seed.sql");
                await Security.InitAsync(c, cfg);
                await LoginGuard.CleanAsync(c);
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users") > 0) return;
                var dev = (cfg["ASPNETCORE_ENVIRONMENT"] ?? "Production") == "Development";
                var adminPw = cfg["Seed:AdminPassword"]; var custPw = cfg["Seed:CustomerPassword"];
                if (string.IsNullOrWhiteSpace(adminPw)) { if (dev) adminPw = "admin123"; else { Console.WriteLine("[Syscle] Set Seed__AdminPassword (8-64 characters) before the first start. No accounts were created."); return; } }
                await c.ExecuteAsync("INSERT INTO Users(Username,Email,PasswordHash,Role) VALUES ('admin','admin@syscle.com',@a,'SuperAdmin')", new { a = Pw.Hash(adminPw!) });
                if (string.IsNullOrWhiteSpace(custPw) && dev) custPw = "password123";
                if (!string.IsNullOrWhiteSpace(custPw) && custPw != "none") await c.ExecuteAsync("INSERT INTO Users(Username,Email,PasswordHash,Role) VALUES ('customer1','customer@syscle.com',@b,'Customer')", new { b = Pw.Hash(custPw) });
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Syscle] Startup attempt {attempt}/20 failed (is the database running?): " + e.Message);
                if (attempt < 20) await Task.Delay(2000);
            }
        }
        // do not start a shop that has no database: stop with a clear message so the host shows the failure and restarts or alerts
        throw new InvalidOperationException("Web-Syscle could not set up the database after 20 attempts. Check ConnectionStrings__Syscle and that MySQL is running and reachable.");
    }
}
