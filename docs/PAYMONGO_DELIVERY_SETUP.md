# PayMongo online payment + home delivery: setup and demo guide

## What was added
- **Home delivery** in the cart: the customer picks Store pick-up or Home delivery, types an address and a phone number. A delivery fee (default PHP 100) is added. The admin changes the fee in Settings > Home delivery fee (0 = free).
- **Delivery tracking:** orders for delivery go Pending > Preparing > Packed > Out for delivery > Delivered. When the admin presses "Out for delivery" they type the rider/courier name and an optional tracking number. The customer sees them on My orders, gets the bell notification and an email.
- **Online payment (PayMongo):** a payment choice "PayMongo". It only shows to customers when the secret key is set. The customer pays on PayMongo's page. The order shows **Paid** or **Unpaid**, and the admin cannot mark an unpaid online order Completed.
- **Cash / GCash reference** still work as before. Completing an order marks it Paid.
- **Sales graph:** days with no sales now show as empty bars instead of being skipped.

## 1. Get PayMongo test keys (free)
1. Create an account at paymongo.com and open the Dashboard. Stay in **Test mode**.
2. Developers > API Keys: copy the **Secret key** (starts with `sk_test_`).

## 2. Put the key in Render
Render > your service > Environment > add:
- `PayMongo__SecretKey` = `sk_test_...`
- (after step 3) `PayMongo__WebhookSecret` = `whsk_...`

Then redeploy. (Locally, put the same two values in `appsettings.json` under `"PayMongo"`.)

## 3. Webhook (recommended)
PayMongo Dashboard > Developers > Webhooks > Create:
- URL: `https://web-sycle-capstone.onrender.com/api/webhooks/paymongo`
- Event: `checkout_session.payment.paid`
- Copy the webhook secret into `PayMongo__WebhookSecret`.

Without the webhook it still works: when the customer returns to My orders after paying, the server asks PayMongo directly and marks the order Paid.

## 4. Demo steps
1. Log in as a customer, add an item, open the cart.
2. Choose **Home delivery**, type an address and phone, choose **PayMongo**, press Place order.
3. On PayMongo's test page pick a method and complete the test payment (use the test card or the test GCash/Maya option shown on the page; see PayMongo's docs for current test details).
4. You land on My orders: the order shows **Paid**.
5. Log in as admin > Orders: press Start preparing, Mark packed, **Out for delivery** (type a rider name), then Mark delivered. Watch the customer's tracker and bell update.

## Important notes
- The server code is not compiled in my workspace. Build once and fix any typo the compiler reports.
- A customer cannot cancel an order that is already paid online. The admin cancels it and refunds in the PayMongo dashboard (refunds are not automatic here).
- Going live (real money) needs PayMongo business verification and live keys. The test setup is enough for the defense.
- The database updates itself on start (new Orders columns, the new status, the new payment method row).
