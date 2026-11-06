# PayPal setup (works next to PayMongo)

The cart shows "PayPal (online)" only when PayPal keys are set. Nothing else to configure; no webhook is needed.

## Get test keys (free)
1. Go to developer.paypal.com and log in with any PayPal account.
2. Apps & Credentials > make sure **Sandbox** is selected > **Create App** (name: Web-Syscle).
3. Copy the **Client ID** and **Secret**.
4. Testing Tools > Sandbox Accounts: use the *Personal* account (email + password shown there) to "buy" during your demo.

## Put the keys in
- Local: `Syscle.Web/appsettings.json` > `"PayPal": { "ClientId": "...", "Secret": "...", "Mode": "sandbox", "Currency": "PHP" }`
- Hosted site: add environment variables `PayPal__ClientId`, `PayPal__Secret`, `PayPal__Mode` (= `sandbox`), then redeploy.
Never commit real keys to GitHub.

## How it works
1. Customer picks "PayPal (online)" in the cart and places the order (order is Unpaid).
2. They are sent to PayPal, log in, and approve.
3. PayPal sends them back to My orders. The site confirms with PayPal and takes the payment, then the order shows **Paid**.
4. If they close the tab early, "Pay now" in My orders finishes it without paying twice.
The admin cannot mark an unpaid PayPal order Completed. Cancelling a paid order and refunding is done by the admin in the PayPal dashboard.

## Going live (after the defense)
Use a verified PayPal **Business** account, create a **Live** app, switch `Mode` to `live` and use the live keys. PayPal charges a fee per sale; check PayPal's current fee page for the Philippines.
