# Web-Syscle (bike parts shop)
ASP.NET Core 8 + MySQL. Two sides in one site: Customer (shop, cart, checkout, orders) and Admin (inventory with pictures, store-front categories, walk-in sales, transactions, payment setup, accounts).

## Run it
1. Install Visual Studio 2022 (workload: ASP.NET and web development) and MySQL Server 8 + MySQL Workbench.
2. In Workbench, open `Database/Syscle_MySQL.sql` and run it (lightning bolt). It creates `WebSyscleDB`.
3. In `Syscle.Web`, create `appsettings.Development.json` (it is git-ignored, so your password stays private):
   `{ "ConnectionStrings": { "Syscle": "Server=localhost;Port=3306;Database=WebSyscleDB;User ID=root;Password=YOUR_PASSWORD;" } }`
4. Open `Syscle.sln`, Build > Rebuild Solution, press F5.
5. First start creates demo logins: `admin` / `admin123` and `customer1` / `password123`. Change them before real use.

## Already have a database from an older version?
Run `Database/Update_Schema.sql` once in Workbench (File > Open SQL Script > lightning bolt). If you are locked out of an account, run the needed line of `Database/Fix_Login.sql`.

## Put it online (GitHub + Codespaces)
See `GITHUB_CODESPACES_GUIDE.md` in the repository root.

## Forgot password
The reset link is emailed. If email is not set up (`EmailSettings` in appsettings), the link is shown on screen in Development and printed in the console.

## Where things are
- `Syscle.Web/Program.cs`: all server code (login, roles, API, MySQL queries)
- `Syscle.Web/wwwroot/Pages` + `JavaScripts` + `Styles`: the website
- `Database/Syscle_MySQL.sql`: tables and starter data

## Team rules
Never commit passwords. Pull before you start, commit small, write a clear message.

## Help window, messages, settings (new)
- The round support button (bottom right) opens a help window: quick answers, store contact details, quick links and a chat with the store.
- Admins read and answer chats in **Customer Messages** (dashboard tile). A red number on the support button shows unread messages.
- **Settings** (gear icon): customers choose a preferred payment method and e-mail notifications; admins edit store name, address, phone, hours and the pick-up note, and see whether e-mail and SMS are set up.
- New tables/columns (`Messages`, `Orders.PaymentRef`, `Users.EmailNotify`) are created automatically when the app starts. `Update_Schema.sql` also contains them.

## Input limits
Username 3-14 characters, password 8-14 characters, e-mail up to 50. The server enforces this too (not just the forms), JSON requests are capped at 20 KB, and every request at 6 MB, so a huge pasted value cannot overload the site.

## GCash payments
Customers pay the store's GCash number (and QR if shown), then type the reference number from their GCash receipt at checkout. The reference appears on the order in Transactions History; check it in your GCash app before pressing Complete. The same reference cannot be used for two orders.
GCash has no free API that lets a website charge a personal account automatically. Automatic "scan, pay, order confirmed" needs a payment gateway with a business account (for example PayMongo, Xendit or Maya Business) plus its API keys and a webhook.

## SMS password recovery
On the login page: **Forgot Password -> Reset with my mobile number**. The customer enters the number saved in My account (09XXXXXXXXX), receives a 6-digit code (valid 10 minutes, 5 tries) and picks a new password.
Sending real SMS needs an SMS provider. The code supports Semaphore (Philippines): put your key in `appsettings.Development.json` (or the environment variable `SmsSettings__SemaphoreApiKey`):
```
"SmsSettings": { "SemaphoreApiKey": "your-key", "SenderName": "" }
```
Without a key, in Development the code is shown on screen so you can test; in Production it is only written to the server log.
