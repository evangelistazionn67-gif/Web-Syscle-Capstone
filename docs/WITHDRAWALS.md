# Withdraw funds (PayMongo wallet to GCash)

Every online payment (QR Ph, GCash, Maya, cards and the other networks) lands in the store's **PayMongo wallet**. **Admin > Withdraw funds** shows the wallet balance and sends money from it to a GCash account of your choice.

## What you need
1. A PayMongo account with the **Wallet** activated, and the live (or test) secret key in `PayMongo__SecretKey`. Sending money out through the API must be allowed on your PayMongo account; if PayMongo says it is not, ask PayMongo support to enable "Send Money" for your account.
2. **Two-factor authentication turned on for the admin** (My account > Security). The page stays locked until it is.
3. A saved GCash account (name exactly as registered on GCash, and the GCash mobile number).

## How a withdrawal is protected
- Adding a GCash account and every withdrawal need a fresh 6-digit code from the admin's authenticator app. It is checked like signing in is: a code can only be used once, and 5 wrong codes lock withdrawals for 10 minutes.
- Only one withdrawal can run at a time, so a double click cannot send twice.
- Every withdrawal is recorded (who, when, how much, to which account, and PayMongo's answer) in the history on the same page and in the audit log. The admin is also emailed when an account is added or money is withdrawn (when email is set up).
- Limits (change them in settings): `Withdrawals__Min` (default 100), `Withdrawals__Max` (default 50,000, the InstaPay limit per transfer), `Withdrawals__Fee` (default 10, PayMongo's fee per transfer, used only to check that the wallet can cover it).

## Things PayMongo controls
- **New recipients:** PayMongo can require a new receiving account to be registered and verified (with a waiting period) before money can be sent to it. If a withdrawal fails for that reason, the page shows PayMongo's message: register the account in your PayMongo dashboard (Wallet > Recipients), wait out the period, then try again.
- **Fee and speed:** PayMongo charges a fee per transfer and sends it through InstaPay, usually within minutes. A transfer that is still on its way shows **Pending**; the page asks PayMongo again every time it is opened.
- **If PayMongo does not answer**, the withdrawal stays **Pending** and no new one can be started. Check PayMongo's dashboard (Wallet > Transactions) before trying again, because the money may already be on its way.

## If the wallet cannot be read
The page reads the wallet from PayMongo's API. If it shows an error such as "The wallet reply from PayMongo could not be read", copy these from your PayMongo dashboard (Wallet) into the settings and restart: `PayMongo__WalletAccountNumber`, `PayMongo__WalletAccountName` (and `PayMongo__WalletId` if you have more than one wallet). The server log shows what PayMongo answered.

## Test it first
Use your PayMongo **test** key and a small amount. Check in PayMongo's dashboard that the transfer appears before using live keys.
