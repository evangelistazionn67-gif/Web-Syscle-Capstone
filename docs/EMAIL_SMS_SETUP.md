# Password recovery by email and SMS: setup guide

The code is finished. What is still needed are accounts with an email provider and an SMS provider, because the site has to send real messages through someone. Until you add their keys, the site works as before: in Development it shows the reset link or code on screen, and on a live site nothing is sent.

Customers can recover a forgotten password two ways (login page > Forgot Password):
- **Email**: a reset link that works for 30 minutes and only once.
- **SMS**: a 6-digit code that works for 10 minutes. It only works for customers who saved a mobile number (new customers can enter one when they sign up; everyone can add one in My account).

## 1. Tell the site its own address (required on a live site)
Set `App__PublicUrl` to the address customers use, with no slash at the end, for example `https://yourshop.example.com`.
The reset email uses it for its link. It is **not** taken from the visitor's request on purpose: otherwise anyone could ask for a reset on someone else's account with a forged address and the victim would receive an email whose link opens the attacker's page. If it is missing on a live site, no reset emails are sent and the log says so. In Development it is not needed.

## 2. Email: choose ONE of these

**A. Gmail (or any SMTP mailbox), the quickest**
1. Turn on 2-Step Verification for the Google account, then create an **App Password** (Google Account > Security > App passwords).
2. Set: `EmailSettings__SmtpHost=smtp.gmail.com`, `EmailSettings__SmtpPort=587`, `EmailSettings__SmtpUser=your@gmail.com`, `EmailSettings__SmtpPassword=<the 16-letter app password>`, `EmailSettings__SenderEmail=your@gmail.com`.
3. Gmail only sends as the signed-in address, so `SenderEmail` should be that same address. Port 465 also works (the site picks SSL automatically).
Good for a demo or a small shop. Gmail limits how many emails a day it will send.

**B. SendGrid, for a real business**
1. Create a SendGrid account and verify your sender address (Settings > Sender Authentication). SendGrid refuses mail from an address it has not verified.
2. Create an API key (Settings > API Keys). It starts with `SG.`.
3. Set: `EmailSettings__SendGridApiKey=SG....` and `EmailSettings__SenderEmail=<the verified address>`.
If both SMTP and SendGrid are filled in, SMTP is used.

## 3. SMS (Semaphore, a Philippine SMS service)
1. Create an account at semaphore.co, load some credits, and copy your **API key**. (Check Semaphore's current prices; each message costs credits.)
2. Set `SmsSettings__SemaphoreApiKey=<your key>`.
3. `SmsSettings__SenderName` is optional. Leave it empty unless Semaphore has approved a sender name for you; an unapproved name makes the messages fail.

## 4. Where to put these settings
- **Docker / docker compose**: copy `.env.example` to `.env` and fill in the lines (`PUBLIC_URL`, `SMTP_*`, `SENDGRID_API_KEY`, `SEMAPHORE_API_KEY`...).
- **Render or another host**: add them as environment variables with the double-underscore names above.
- **GitHub Codespaces**: GitHub > Settings > Codespaces > Secrets, add each one with the double-underscore name (for example `EmailSettings__SmtpHost`) and give this repository access. Restart or rebuild the codespace so it picks them up.
- **Your own computer**: put them in `Syscle/Syscle.Web/appsettings.Development.json` (that file is ignored by git, so it never reaches GitHub).
Never put real keys or passwords in `appsettings.json` or any file that is committed.

## 5. Check that it works (no customer needed)
Sign in as admin > **Settings**. The **Password recovery** card shows whether email, SMS and the site address are set up. Type your own email address or mobile number and press **SEND** to receive a real test message. If it fails, the card shows the reason, and the server log has more detail (for example: a SendGrid sender that is not verified, an SMTP password that is wrong, or no Semaphore credits left).

## Security notes
- Asking for a reset always gets the same answer whether or not the email or number is registered, and the answer comes back at the same speed.
- Limits: 5 reset emails per address and 15 per visitor every 10 minutes; 3 SMS per number and 10 per visitor every 10 minutes.
- A reset signs the person out everywhere else, and a reset link stops working once the password has changed.
- A reset by email or SMS also works for an account that has 2FA on (that is how someone who lost their phone gets back in). Anyone who controls the mailbox or the phone number can therefore take over the account, so keep the admin accounts' email addresses secure.
