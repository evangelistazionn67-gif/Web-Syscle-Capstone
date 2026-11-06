# Delivery (Lalamove, handled by the admin), cash on delivery and receipts

## One-time setup
1. Admin > Settings > "Home delivery & receipts": tap your shop on the map (or "Use my current location"), set the range in km, base fee, fee per km, the cash-on-delivery limit, then Save.
2. Lalamove keys (optional but needed for automatic booking). In Codespaces:
   `export Lalamove__ApiKey=pk_test_... ; export Lalamove__Secret=sk_test_... ; export Lalamove__Mode=sandbox`
   then run the app. Sandbox = practice, no real rider. For real riders use the production keys, `Lalamove__Mode=production`, and top up your Lalamove wallet.
3. Without keys you can still book in the Lalamove app and type the booking details into the order ("Delivery" button).

## How the money flows
| Customer pays | Order created | Admin does | Counted as sale |
|---|---|---|---|
| PayMongo | Paid automatically after checkout | Confirm items, Book Lalamove | when Paid + items confirmed |
| GCash | Unpaid until admin checks the reference | Confirm items, "Payment received", Book Lalamove | after payment recorded |
| Cash on delivery | Unpaid | Confirm items, Book Lalamove, collect cash, press "Cash collected", Complete | after "Cash collected" |

* The customer pays a delivery fee (base + per km, rounded up to PHP 5). The Lalamove rider cost is paid from your Lalamove wallet; the order screen shows both so you can see the difference.
* Cash on delivery: Lalamove does not hand the shop the customer's cash. You (or whoever delivers) must collect it; keep the COD limit low.
* A booked rider blocks cancelling the order and approving a refund until the booking is cancelled.
* Gateway refunds (PayMongo/GCash) are returned by you in the provider dashboard.

## Receipts
Orders > Receipt button prints in one click (80 mm roll or A4). Walk-in sales print by themselves after "Record sale" (switch off in Settings). Customers can print their own receipts in My orders.

## Pay by QR in the shop (PayMongo QR Ph)
* Walk-in sale: choose **QR Ph (scan to pay)** > "Show payment QR". The customer scans it with GCash, Maya or any bank app. The sale closes and the receipt prints by itself once PayMongo confirms. No reference number is typed.
* Orders list: **Pay by QR** appears on unpaid orders whose items are confirmed (pick-up customers paying at the counter).
* The money goes to your PayMongo balance; PayMongo pays it out to your bank account on its payout schedule.
* Needs: `PayMongo__SecretKey` set, and QR Ph switched on for your PayMongo account (ask PayMongo support). Minimum ₱20; each code lasts 30 minutes and works once.
* Webhook (optional, faster): in PayMongo > Developers > Webhooks add `https://YOUR-SITE/api/webhooks/paymongo` for `payment.paid`. Without it the screen checks every 3 seconds anyway.
* Online checkout can also show QR Ph: set `PayMongo__QrPh=true` (only after PayMongo enabled it, otherwise checkout would fail).
* With test keys, PayMongo says QR codes are live: do not scan and pay a test code with real money unless you mean it (it is a real payment).
