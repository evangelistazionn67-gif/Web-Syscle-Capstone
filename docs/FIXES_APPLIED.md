# Fixes applied after the code scan (not yet compiled or run, so run `dotnet build` first)

## High priority
1. **Duplicate checkout orders**: the cart rows are locked during checkout (`FOR UPDATE OF ci`, needs MySQL 8) and the Checkout button ignores a second click while the first is running.
2. **Payments**: one shared rule, `Sql.MarkPaid`, is used by the PayMongo webhook and both payment-return paths. A payment that arrives for an already-cancelled order flags it "Refund requested" instead of leaving a paid, cancelled order. The webhook answers HTTP 500 when the database fails so PayMongo retries.
3. **Startup**: the app stops with a clear message if the database cannot be set up after 20 tries, instead of running with no database.
4. **Secrets in logs**: password-reset links and SMS recovery codes are only logged in Development.
5. **Spoofable client IP**: new setting `Security__TrustProxy` (default `true` in code, `false` in docker-compose).

## Medium priority
6. **Passwords**: allowed length is now 8 to 64 (server, forms and messages). New hashes use PBKDF2 with 600,000 iterations (was 100,000); older hashes keep working and are upgraded automatically the next time that person signs in. Sign-ups are limited to 10 per address per hour because hashing is now heavier.
7. **Lockout abuse**: login attempts are counted per username *per address* (8 in 10 minutes), per address (30) and per username overall (60). A stranger can no longer lock the real owner or the admin out with a few wrong tries.
8. **Cookies**: new setting `Security__SecureCookies` (`SECURE_COOKIES` in docker-compose) forces the sign-in cookie to HTTPS only. The server also sends `Strict-Transport-Security` on HTTPS requests.

## Cleanup and remaining findings
9. **Stray and dead files removed**: `Pages/admin_payment_setup - Copy.html` (it was being served), the stale root copies `admin_payment_setup.html` and `admin_payment.js` next to the .csproj, the unused `wwwroot/index.js`, the stale `Syscle/Dockerfile.txt` (the real one is `Dockerfile` in the project root), 14 CSS files that no page links (all styling is in `ui.css` and `support.css`), and 7 unused icon JPGs. `wwwroot/Icons_and_Buttons/` is created on demand for category pictures uploaded in the admin.
10. **Accessibility**: the two `<img>` tags without `alt` now have it. The seven CSS class names flagged earlier turned out to be JavaScript hooks, not missing styles, so nothing needed fixing there.
11. **Image uploads** are now checked by their first bytes (JPEG, PNG or WebP), not just the file name, in all four upload places. The payment QR image address gets a `?v=` number so browsers show the new QR as soon as it is replaced instead of the old cached one.
12. **One clock (shop time, UTC+8)**: every database connection now sets `time_zone='+08:00'`, and the app's own date maths uses `Promos.Now` / `Promos.Today`. Before, a database in UTC (the Docker default) filed sales made between midnight and 8 AM under the previous day in the reports, and order times showed 8 hours behind. Rows created before this change were saved in UTC, so old test data will look 8 hours early; new rows are right.

## Checked and found fine
PayPal and PayMongo verification (the server only asks about the payment reference it created itself), 2FA (secret stored encrypted, a code cannot be used twice, ticket attempts are limited, turning it off needs password and code), the sales-forecast arithmetic, and output escaping in the front end.

## Known gaps, not changed
- Fonts (Google Fonts), the icon font (jsDelivr) and the QR scanner library (cdnjs) load from the internet. Self-hosting them needs a network connection that was not available here.
- There is no Content-Security-Policy header yet. The pages use inline scripts, so adding one safely needs testing in a browser.
- 2FA has no recovery codes: a user who loses their phone needs an admin to reset it.
- There are no automated tests yet.

## New features
- **Pop-up for every new online order (admin pages).** When a customer checks out, a card slides in at the top right of every open admin page with the order number, customer, total, item count, payment method and pick-up or delivery. It disappears after about 9 seconds (it stays while the mouse is over it), can be closed with the x, opens Transactions when clicked, and at most 3 cards show at once (more than 3 new orders at the same time become one "N new orders" card). The page checks every 10 seconds while it is open and catches up as soon as the admin returns to the tab. The first time an admin page is opened only a starting point is saved, so old orders are never announced. Walk-in sales rung up by staff do not trigger it. The bell and sidebar counters also refresh at once.
  Files: `Features.cs` (new `GET /api/admin/orders/new-since`), `wwwroot/JavaScripts/shell.js`, `wwwroot/Styles/ui.css`.

## Payments: PayMongo only (QR for the exact amount)
- **Generated QR instead of a fixed QR.** Online payment now opens PayMongo's secure page with **QR Ph**, a QR code made for that order's exact total, plus GCash, Maya and card. Payment is recorded automatically (webhook, or when the customer returns to My orders). The ways to pay are set by `PayMongo__Methods`, `App__PublicUrl`, `EmailSettings__SmtpHost/SmtpPort/SmtpUser/SmtpPassword/SenderEmail`, `SmsSettings__SemaphoreApiKey`, `PayMongo__WalletId`, `PayMongo__WalletAccountNumber`, `PayMongo__WalletAccountName`, `Withdrawals__Min/Max/Fee` (default `qrph,gcash,paymaya,card`).
- **Removed:** the manual GCash method (number, reference field, duplicate-reference check), the fixed payment QR (upload, toggle, customer display, `/api/settings/payment-qr`) and the store account number/name fields. On the first start the GCash method and the two QR settings are deleted from the database; old orders keep their "GCash" label in the history, and the PayMongo method is renamed "QR, GCash, Maya or card (online)" (existing orders are renamed with it so refunds and reports keep matching).
- **Admin "Payment setup" tab** is now **Payment methods**: just ON/OFF switches for Cash, PayMongo and PayPal.
- **Walk-in sales** can now only be rung up as Cash (online methods need the customer's own phone). Taking e-wallet payments at the counter would need a PayMongo QR on the walk-in screen, which is not built yet.
- **"QR labels" tab kept on purpose:** it prints the SKU QR codes stuck on products, which staff scan at the counter to add items to a walk-in sale. It is not the payment QR.
- **PayPal** is untouched.
- Wording on the home page, login page, help (FAQ) and settings no longer mentions manual GCash.

## Password recovery by email and SMS (activated)
The recovery flows were already in the code but could not work on a live site. Now:
- **Reset-link poisoning fixed.** The link in a reset email used to be built from the visitor's `Host` header, so anyone could make the victim receive a genuine email linking to the attacker's site. Links now use the new setting `App__PublicUrl` (required on a live site; Development uses the request address).
- **Same speed for everyone.** On a live site the email or SMS is sent in the background, so the answer does not reveal whether an email or number is registered.
- **More limits.** Reset emails are now also limited per visitor (15 every 10 minutes) so the form cannot be used to flood inboxes or burn the email provider's quota.
- **Email provider fixes.** SMTP now works on port 465 as well as 587 and gives up after 15 seconds; SendGrid failures are written to the log with the reason.
- **Optional mobile number at sign-up**, because SMS recovery only works for customers who saved a number (before, only My account could add one). Duplicate numbers are refused.
- **Admin > Settings > Password recovery** now shows email (SMTP or SendGrid), SMS and site-address status, with **Send a test email** and **Send a test SMS** buttons (limited to 10 an hour).
- **Docs and settings:** `docs/EMAIL_SMS_SETUP.md` (step by step), new lines in `.env.example`, `docker-compose.yml`, `appsettings.json` and the README.
- **You still have to create the provider accounts and add their keys.** That cannot be done from the code.

## Bug fix: admin Settings (and My account) signed the admin out
Pressing **Settings** as an admin showed "Signed Out". Settings and My account are shared by both portals, so the page first has to find out whether an admin or a customer is signed in. The first request went out before that check finished and was labelled as the shop portal; the server looked for a customer sign-in, which an admin does not have, and the page treated the "not signed in" answer as a sign-out. `api()` in `wwwroot/JavaScripts/api.js` now waits for that check on shared pages (with a 3-second limit so a slow check can never freeze the page). Admin and shop pages are unchanged. The same fix covers My account for admins.

## Team findings (customer checkout, payments, withdrawals, promos)
1. **Checkout button always visible.** The Order summary box was "sticky" and taller than a normal screen, with the Place order button below it, so people had to zoom out to reach it. The total, the payment list and the Place order button are now one block pinned at the bottom of the summary box; only the upper part (promo, delivery, date, breakdown) scrolls. On phones the layout is a normal page again.
2. **Payments simplified.** The customer chooses **Cash** (pay at the store or to the rider) or **Online payment**. Online always opens PayMongo: a QR code for the exact total, GCash, Maya, cards and other networks (set the list in `PayMongo__Methods`; if PayMongo refuses a network that is not activated on your account, the page falls back to card, GCash and Maya so customers can still pay). **Removed:** the practice payment page, test mode (`Payments__TestMode`), the sandbox endpoint and PayPal (hidden and deleted from the payment methods; old PayPal orders keep their label in the history).
3. **Withdraw funds (new admin page).** Shows the PayMongo wallet balance and sends money to a saved GCash account. Adding an account and every withdrawal need the admin's 2FA code (single-use, 5 wrong tries lock it for 10 minutes), only one withdrawal can run at a time, everything is logged, and the admin is emailed. Setup and limits: `docs/WITHDRAWALS.md`. Files: `Program.cs`, `schema.sql` (two new tables), `admin_withdraw.html/.js`.
4. **Promos that advertise.** On shop pages a glowing card announces the customer's promos, waits about 7 seconds, then shrinks into a gift icon in the header beside the cart (with a count badge). Hovering (or tapping) the icon lists the codes and bundle deals available to that customer. The card appears once per visit (again after 10 minutes), never on the cart page, and has "Hide for today". The old pop-up window and the promo list on the customer dashboard are gone.
5. **Uses per customer.** When creating a promo code the admin sets how many times one customer can use it (default once each, or unlimited); there is a **Per customer** button to change it later. It is enforced when the code is applied and at checkout, cancelled orders do not count, and a customer who has used a code up no longer sees it. Existing codes stay unlimited per customer.

## Settings added (also in README and .env.example)
`Security__TrustProxy`, `Security__SecureCookies`, `PayMongo__Methods`, `App__PublicUrl`, `EmailSettings__SmtpHost/SmtpPort/SmtpUser/SmtpPassword/SenderEmail`, `SmsSettings__SemaphoreApiKey`
