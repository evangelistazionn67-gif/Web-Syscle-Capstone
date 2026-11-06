# Web-Syscle: put it on GitHub and run it in Codespaces

Follow the parts in order. Anything in a grey box is typed into **Command Prompt** (press the Windows key, type `cmd`, press Enter).

## Part 1 - What to install on your PC
Only **Git** is required. Codespaces runs .NET and MySQL for you in the cloud, so you do NOT need to install them for this route.

1. Download **Git for Windows** from https://git-scm.com/download/win and install it. Click **Next** on every screen and keep the defaults.
2. Close Command Prompt and open it again, then check:
   ```
   git --version
   ```
   It should print something like `git version 2.4x`.
3. (Optional) **VS Code** from https://code.visualstudio.com if you want to edit files on your PC.

## Part 2 - Make the new GitHub account
1. Go to https://github.com/signup, enter the new email, a password and a username.
2. Open the email GitHub sends you and enter the verification code.
3. Choose the **Free** plan. (Free gives you 120 core-hours and 15 GB of storage per month for Codespaces, which is plenty for a school/demo project.)

## Part 3 - Create the empty repository
1. On github.com click the **+** at the top right, then **New repository**.
2. Repository name: `Web-Syscle`.
3. Choose **Private**.
4. Leave **Add a README**, **.gitignore** and **license** all unticked.
5. Click **Create repository**. Keep this page open, you need its address:
   `https://github.com/YOUR-USERNAME/Web-Syscle.git`

## Part 4 - Upload the project from your PC
1. Unzip `Web-Syscle-fixed.zip`. Open the unzipped `Web-Syscle` folder. You must see the folders `.devcontainer` and `Syscle` directly inside it. (If you only see another `Web-Syscle` folder, open that one.)
2. Click the address bar of that folder in File Explorer, type `cmd` and press Enter. A Command Prompt opens already inside the folder.
3. Tell Git who you are (once per PC, use the NEW account's email):
   ```
   git config --global user.name "Your Name"
   git config --global user.email "your-new-account-email@example.com"
   ```
4. Create the repository and check what will be uploaded:
   ```
   git init
   git branch -M main
   git add .
   git status
   ```
   Look through the green list. `appsettings.Development.json` (your database password) must NOT be in it. It is blocked by `.gitignore`. If you see it, stop and ask for help.
5. Save and upload (replace YOUR-USERNAME):
   ```
   git commit -m "First upload of Web-Syscle"
   git remote add origin https://github.com/YOUR-USERNAME/Web-Syscle.git
   git push -u origin main
   ```
6. A browser window opens asking you to sign in to GitHub. Click **Authorize**. Go back to Command Prompt; it finishes the upload.
7. Refresh the repository page on GitHub. You should see `.devcontainer`, `Syscle`, `README`/guide files at the top level.

## Part 5 - Open it in Codespaces (the browser version)
1. On the repository page click the green **Code** button, open the **Codespaces** tab, then **Create codespace on main**.
2. Wait 3 to 6 minutes the first time. A VS Code opens in the browser and the bottom terminal shows the setup (MySQL, tables, packages). It ends with `READY`.
3. The website starts by itself. A popup says "Your application running on port 5080 is available". Click **Open in Browser**.
   If you missed it: open the **PORTS** tab (next to TERMINAL), find port **5080**, click the small globe icon.
4. Log in with `admin` / `admin123` (or `customer1` / `password123`). Change the admin password right away in My Account.

### If the page does not open
- In the terminal type `cat /tmp/dotnet.log` to read the error.
- Start it by hand (leave the terminal open):
  ```
  cd Syscle/Syscle.Web
  dotnet run --no-launch-profile --urls http://0.0.0.0:5080
  ```
- Database not ready: wait one minute and start again. The app now retries for about 40 seconds by itself.
- Port is **Private** by default, so only you (signed in to GitHub) can open it. To let a teacher or friend see it: PORTS tab, right-click port 5080, **Port Visibility**, **Public**.

## Part 6 - Email for "Forgot password" (optional)
Without email, Forgot Password still works while testing: the page shows the reset link on screen after you click SEND LINK.

To send real emails with a Gmail account:
1. On that Google account turn on **2-Step Verification**, then create an **App password** at https://myaccount.google.com/apppasswords (16 letters).
2. On GitHub open your profile picture, **Settings**, **Codespaces**, **New secret**. Add these (repository access: Web-Syscle):
   - `EmailSettings__SmtpHost` = `smtp.gmail.com`
   - `EmailSettings__SmtpPort` = `587`
   - `EmailSettings__SmtpUser` = `your-gmail-address`
   - `EmailSettings__SmtpPassword` = `the 16-letter app password`
   - `EmailSettings__SenderEmail` = `your-gmail-address`
3. Stop and start the codespace (or **Rebuild Container**) so the secrets load.

## Part 7 - Storage, cost and keeping your data
- A Codespace stops by itself after 30 minutes idle. Stop it yourself when finished: github.com/codespaces, click the three dots, **Stop codespace**. Stopped codespaces keep your data.
- **Deleting** a codespace deletes its database and every picture uploaded through the admin pages. Make a backup first, in the codespace terminal:
  ```
  mysqldump -h db -uroot -psyscle WebSyscleDB > backup.sql
  ```
  Download `backup.sql` (right-click it in the file list, Download). To restore later: `mysql -h db -uroot -psyscle WebSyscleDB < backup.sql`.
- Pictures uploaded while testing (`prod_*`, `cat_*`) are not uploaded to GitHub on purpose. The starter pictures are.
- Free monthly limit: 120 core-hours = 60 hours on the default 2-core machine. See usage at github.com/settings/billing.

## Part 8 - Updating the code later
From your PC (inside the project folder in Command Prompt):
```
git add .
git commit -m "Describe what you changed"
git push
```
Inside a codespace, get the newest code with:
```
git pull
```
then restart the site (Ctrl+C in the terminal that runs it, then run the `dotnet run ...` command again).

If you change the database layout, add a new `.sql` update file in `Syscle/Database` and run it with
`mysql -h db -uroot -psyscle WebSyscleDB < Syscle/Database/YourFile.sql`.

## Running on your own PC instead (no Codespaces)
Install **.NET 8 SDK** (https://dotnet.microsoft.com/download/dotnet/8.0) and **MySQL Server 8 + Workbench**, then follow `Syscle/README.md`. For an existing database run `Database/Update_Schema.sql` once.
