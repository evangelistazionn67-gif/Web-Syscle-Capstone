# Web-Syscle

Bicycle-parts online shop and inventory system (ASP.NET Core 8 minimal API, MySQL, Dapper, plain HTML/CSS/JS).
Two portals in one site: **Customer** (shop, cart, checkout, orders, refunds, reviews) and **Admin** (inventory, promos, bundles, orders, walk-in sales, reports).

## Quick start (Docker)
1. Copy `.env.example` to `.env` and fill it in.
2. `docker compose up --build`
3. Open http://localhost:8080 and sign in as `admin` with the `ADMIN_PASSWORD` you chose.

The app creates the database, tables and starter data by itself on first start (`Syscle/Database/schema.sql`, `seed.sql`).

## Quick start (Visual Studio or `dotnet run`)
1. Install .NET 8 SDK and MySQL 8.
2. Copy `Syscle/Syscle.Web/appsettings.Development.example.json` to `appsettings.Development.json` and set your MySQL password (this file is git-ignored).
3. `cd Syscle/Syscle.Web && dotnet run`. In Development, demo logins are `admin` / `admin123` and `customer1` / `password123`.

## GitHub Codespaces
Open the repository in a Codespace. It starts MySQL and the site (port 5080) with practice payments switched on. See `docs/GITHUB_CODESPACES_GUIDE.md`.

## Configuration (environment variables or appsettings)
| Setting | Purpose |
|---|---|
| `ConnectionStrings__Syscle` | MySQL connection |
| `Security__DataKey` | Key that encrypts stored 2FA secrets (32+ characters). If empty, one is generated and stored in the database |
| `Seed__AdminPassword` | Password of the first admin (production only) |
| `Payments__TestMode` | `true` = online payments use a practice page |
| `EmailSettings__*`, `SmsSettings__*`, `PayMongo__*`, `PayPal__*` | Optional providers. Never commit real keys |

Store rules (admin auto sign-out seconds, low-stock level, order hold times, refund window, seasons, dropdown lists) are edited in the Admin panel under **Lists and rules**, not in code.

## Security notes
- Customer and admin sign-ins use separate cookies, so both can be open in one browser without mixing.
- Login throttling, one-time sign-in tickets and 2FA replay protection are stored in MySQL. 2FA secrets are AES-GCM encrypted. Cookie-signing keys are stored in MySQL.
- The admin portal signs out after the idle time set in **Lists and rules** (default 300 s; use 30 for a demo).
- All dashboard and report data comes from `/api/admin/*`, which requires an admin session.

## Order flow
Customer orders -> admin **confirms the items** -> payment is recorded (automatic for PayMongo/PayPal, manual for cash/GCash) -> order moves Ready / Out for delivery -> Completed. Sales figures count only paid, item-confirmed, non-refunded orders. Unpaid online orders and uncollected pick-ups are cancelled automatically and returned to stock after the hold times in the store rules. Customers can request a refund; the admin approves or declines it.

## Folders
- `Syscle/Syscle.Web/Program.cs`, `Features.cs`, `Infra.cs`: server code
- `Syscle/Syscle.Web/wwwroot`: pages, scripts, styles
- `Syscle/Database`: `schema.sql` (tables), `seed.sql` (starter data)
- `docs/`: setup guides and objectives coverage
