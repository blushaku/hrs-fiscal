# Opera Cloud Fiscal Solution - Kosovo — Installation and Configuration Manual

For implementation engineers. One fiscal server is installed per hotel (property), on the hotel LAN, close to
Oracle FLIP. Time needed: about 1 hour, plus ATK registration of the workstations.

> Status (October 2026): the server, admin UI, archive, central signing and FLIP capture mode are ready. Live
> fiscalization of OPERA folios follows once Oracle's FLIP interface specification is implemented. Both signing modes
> are available: **central signing** on the server, or the **Fiscal Client** on each workstation (section 5.5).

## 1. What gets installed

| Component | Where | Purpose |
|---|---|---|
| fiscal server (Windows service `OperaCloudFiscalKosovo`) | Hotel server | Receives folios from FLIP, builds and signs receipts, sends them to ATK, archives everything, admin web UI |
| PostgreSQL 16 | Same server (recommended) or a database server on the LAN | Append-only archive of receipts, ATK transmissions, OPERA/FLIP messages and the audit log |
| Fiscal Client (Windows service `OperaCloudFiscalKosovoClient`, only in *workstation client* signing mode) | Every PC that can issue a folio | Holds that workstation's ATK key, signs its receipts, prints on a receipt printer connected to that PC |

```
OPERA Cloud ─OFIS─▶ Oracle FLIP (on-premise) ──LAN :5100──▶ fiscal server ──HTTPS :443──▶ ATK (fiskalizimi.atk-ks.org)
                                                             │ PostgreSQL :5432 (local)
                                         admin browser ──LAN :5080──┘
```

## 2. Requirements

**Server**
- Windows Server 2019/2022/2025, or Windows 10/11 Pro (small properties). 64-bit.
- 2 vCPU, 4 GB RAM, 50 GB disk (receipts are small; the archive grows by roughly 1 GB per 100,000 folios).
- A TPM 2.0 chip is recommended (central signing keys are then held by the TPM).
- Static IP address on the hotel LAN; joined to the hotel domain if there is one.
- Clock synchronised (NTP / domain time). Receipt times come from this server and ATK checks them.

**Software** (download before going on site)
- [.NET 8 ASP.NET Core Runtime — Windows Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
- [PostgreSQL 16 for Windows](https://www.postgresql.org/download/windows/) (EDB installer).
- `Opera-Cloud-Fiscal-Solution-Kosovo-win-x64.zip` (release package: application, `install-server.ps1`, `backup-db.ps1`,
  and in `client\` the Fiscal Client with `install-client.ps1`). PCs with the Fiscal Client need the
  [ASP.NET Core Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0) (x64 installer); the client installer checks for it.

**Network**

| Direction | From → To | Port | Why |
|---|---|---|---|
| Inbound | FLIP machine → fiscal server | TCP 5100 (HTTP) | Folios from OPERA via FLIP |
| Inbound | Admin PCs → fiscal server | TCP 5080 (HTTP) | Admin web UI |
| Outbound | fiscal server → `fiskalizimi.atk-ks.org` (production), `fiskalizimi-test.atk-ks.org` (test) | TCP 443 | ATK API. Allow through the hotel firewall/proxy without TLS inspection |
| Local | fiscal server → PostgreSQL | TCP 5432 | Only if PostgreSQL is on another machine |
| Local | fiscal server → receipt printers | TCP 9100 | Only with thermal printing on network printers (Settings › Printing) |
| Local | Fiscal Client PCs → fiscal server | TCP 5080 (HTTP) | Only in workstation-client mode: pairing, ATK registration, status every minute |
| Local | fiscal server → Fiscal Client PCs | TCP 5110 (HTTP) | Only in workstation-client mode: signing receipts, printing on the PC's printer. The client installer opens it for the fiscal server's IP only |

Nothing is exposed to the internet. Do not forward ports 5080/5100 from outside.

**From the hotel / ATK** (collect before installation)
- Business **NUI**, **Fiscalization number** (from ATK's EDI), **unit (branch) number** registered with ATK, business name, city.
- **Application ID** of Opera Cloud Fiscal Solution - Kosovo (issued by ATK on certification; for tests the ATK TEST Application ID).
- List of workstations that can issue folios: computer name, OPERA **Fiscal Terminal ID** to be used, location.

## 3. Install PostgreSQL

1. Run the PostgreSQL 16 installer. Components: *PostgreSQL Server* and *Command Line Tools* (pgAdmin optional).
   Data directory on the data disk, e.g. `D:\PostgreSQL\16\data`. Port 5432. Set a strong password for `postgres`
   and keep it in the hotel's password safe.
2. Create the database and its owner. Open *SQL Shell (psql)* as `postgres` and run (choose your own password):
   ```sql
   CREATE ROLE hrs_fiscal_owner LOGIN PASSWORD 'choose-a-long-password';
   CREATE ROLE hrs_fiscal_app NOLOGIN;   -- least-privilege role the schema grants to; the owner may not create roles
   CREATE DATABASE hrs_fiscal OWNER hrs_fiscal_owner ENCODING 'UTF8' TEMPLATE template0;
   ```
3. If PostgreSQL runs on the same server, leave `listen_addresses = 'localhost'` (the default). If it is on another
   machine, allow only the fiscal server's IP in `pg_hba.conf` (`host hrs_fiscal hrs_fiscal_owner <SERVER-IP>/32 scram-sha-256`).

The tables are created automatically when the service starts the first time (migrations `V001`…).

## 4. Install the fiscal server

1. Install the **.NET 8 Windows Hosting Bundle**, then restart the server (or run `net stop was /y` and `net start w3svc`).
2. Unzip `Opera-Cloud-Fiscal-Solution-Kosovo-win-x64.zip` to a temporary folder.
3. Open **PowerShell as Administrator** in that folder and run:
   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\install-server.ps1 `
       -ConnectionString "Host=127.0.0.1;Database=hrs_fiscal;Username=hrs_fiscal_owner;Password=choose-a-long-password" `
       -AdminPassword "a-temporary-first-password" `
       -FlipSourceIp 192.168.1.10
   ```

   | Parameter | Default | Meaning |
   |---|---|---|
   | `-ConnectionString` | (required) | PostgreSQL connection from step 3 |
   | `-AdminPassword` | empty | Creates the first user `admin` with this password on first start |
   | `-InstallDir` | `C:\Program Files\Opera Cloud Fiscal Solution\Server` | Program folder |
   | `-AdminPort` | 5080 | Admin web UI |
   | `-FlipPort` | 5100 | Endpoint for FLIP (`http://<server>:5100/flip`) |
   | `-FlipSourceIp` | empty (any LAN address) | IP of the FLIP machine; only it may connect to the FLIP port. **Recommended** |
   | `-FlipTcpPort` | 0 (off) | Extra raw-TCP listener, only if Oracle's FLIP uses plain sockets instead of HTTP |
   | `-SigningKeyStore` | `auto` | Central signing keys: Windows key store, non-exportable, in the TPM when present. `file` is for test machines only |

   The script copies the program, writes `appsettings.Production.json` (readable by Administrators and the service only,
   because it holds the database password), registers the Windows service with automatic start and automatic restart
   after a failure, opens the two firewall ports for the Domain/Private profiles, starts the service and checks
   `http://localhost:5080/health`.
4. Open `http://<server>:5080` and sign in as `admin` with the temporary password.
5. **Immediately**: Settings › Users › change the admin password, create personal accounts (see roles below), then remove
   the `AdminPassword` value from `C:\Program Files\Opera Cloud Fiscal Solution\Server\appsettings.Production.json` and restart the service.

**Roles and permissions**

Each user has one role: Cashier, Supervisor, Auditor or Admin. What a role may see and do is set under
*Settings › Roles & permissions* (a checkbox matrix, every change logged). The menu, the dashboard and the buttons
follow these permissions. For example, a cashier sees *Find a receipt* instead of the system status.
Defaults:

| Permission | Cashier | Supervisor | Auditor | Admin |
|---|---|---|---|---|
| System status on the dashboard (OPERA → FLIP → ATK, alerts) | | ✓ | | ✓ |
| View receipts | ✓ | ✓ | ✓ | ✓ |
| Download receipt copies (*KOPJE E KUPONIT*, logged) | ✓ | ✓ | | ✓ |
| Export reports (receipts, workstation summary, audit log) | | ✓ | ✓ | ✓ |
| View audit log / run integrity check | | ✓ | ✓ | ✓ |
| View FLIP messages (contain guest data) | | ✓ | | ✓ |
| Manage settings / workstations / users and permissions | | | | ✓ |

Admin always has every permission, so nobody can lock the system. Changes apply on the next page a user opens.
Accounts are never deleted, only deactivated, so the audit log always shows who did what.

## 5. Configure

All settings below are stored in the database and every change is written to the audit log with old and new value.

### 5.1 Settings › General

| Setting | Value |
|---|---|
| ATK environment | **Test** until the hotel goes live, then **Production**. Never send test receipts to production |
| Application ID | Shown here; entered under *Business & unit* together with the fiscalization number |
| Timeout / retry | 10 s / 2 min (defaults) |
| **Signing** | *Central* (default) or *Workstation client* — see 5.5 |
| OPERA overrides | Off (default). Turn on only if OPERA cannot send a field correctly; then set the codes under *OPERA overrides & VAT* |
| OPERA / FLIP connection | **Capture** during setup (records what FLIP sends). **Live** once fiscalization is enabled |
| FLIP access token | **Generate token**, copy it into FLIP's partner configuration (shown only once), keep *Require the token* on. Header: `Authorization` unless FLIP uses another one. *Generate new token* replaces the old one at once, so update FLIP at the same time |
| VAT calculation | Round VAT half-up (default) — pending ATK's official rule |
| Folio validation | Property always checked; tax number check on; tolerance €0.01 per line. See the OPERA manual §8 |
| Retention | Placeholder 10 years until the legal period is confirmed. Nothing is ever deleted automatically |
| Clock check | Automatic every 15 minutes against ATK's server time. If the difference exceeds 30 s (`clock_max_drift_seconds`), the dashboard shows an alert and the audit log records `CLOCK_DRIFT`; synchronise Windows time (`w32tm /resync`) |

**Language.** The interface is in **Albanian** by default; **Serbian (Latin)** and **English** are chosen with
*SQ · SR · EN* in the header or on the sign-in page and remembered per browser. Receipt texts (copies, the fiscal
lines sent to OPERA) are always in Albanian, as in ATK's samples. To start the interface in another language for
everyone, set `I18n:DefaultLanguage` (`sq`, `sr` or `en`) in `appsettings.json`.

### 5.2 Settings › Business

**The first step after installation**: until the business exists, the dashboard opens this page. Setup runs in two
steps:

1. **Issued by ATK.** Enter the **NUI**, the **fiscalization number** (EDI), the **unit number** registered with ATK and
   the **Application ID**, then press **Validate with ATK**. The fiscal server asks ATK, in the selected environment
   (Test or Production), whether these values belong together. It uses ATK's registration check with a POS number that
   is not registered yet (the first free number is suggested); nothing is registered and no certificate is created.
   If ATK refuses, the dialog says why: a value does not match, the fiscalization number has expired, or that POS number
   is already registered (try another one). Every validation is in the audit log (`ATK_CHECK`).
2. **Business and unit details.** Only after ATK confirms do the other fields open. The **registered name** is filled in
   from ATK's answer; complete the VAT number, unit name, city (printed as location), address and the OPERA property
   code, then **Save**. **The OPERA property code is required for live mode**: folios from any other property are
   refused.

**Validation and saving are separate.** Validate asks ATK and nothing else; **Save** stores the form and never contacts
ATK. If ATK refuses or cannot be reached and you are sure the values are right, **Fill in the details anyway** opens step
2 without ATK's confirmation (then *Name at ATK* is not updated). If NUI, fiscalization number, unit number or Application
ID is changed after validating, step 2 closes until you validate again or choose to continue anyway. When these four
values stay as saved, the other details can be edited straight away.

Everything can be edited later with **Edit** (same two steps). Changing **NUI or unit number** switches the taxpayer: receipts already
issued stay in the archive under the previous taxpayer (receipt pages show the taxpayer they belong to), and every
workstation moves to the new taxpayer and must be **registered with ATK again** (its certificate names the old NUI and
unit). This is refused while receipts still wait to be sent to ATK.

**Start over with a new business** (bottom of the page) deletes the business, workstations, receipts, audit log, FLIP
messages, OPERA overrides and settings, so the application starts from zero; users, roles and VAT rates are kept. Type
the current NUI to confirm. It is only possible while the server has never been switched to ATK **Production**: receipts
sent to Production are legal records, so a new business then gets a new installation with an empty database.

The name ATK answered with (at validation, at workstation registration or from an imported ATK certificate) is kept and
shown on the Business tile as **Name at ATK** — *matches* or *differs*. If it differs, the
registration message says so and **Use ATK's name** (with confirmation, logged) takes it over, so receipts carry
the name ATK has on record. Address and VAT details cannot be looked up automatically: ATK's API returns only the
name, and ARBK has no public API.

### 5.3 Settings › OPERA overrides & VAT: VAT rates

**The VAT list starts empty.** Before the first receipt, add every VAT rate the property uses with **Add VAT rate**:
the **letter** ATK expects for that rate (one capital letter), the **percent** and a description. Until at least one
rate exists, the dashboard shows an alert and every folio is refused with "no VAT rates are set up".

OPERA sends a VAT percentage for each folio line; the fiscal server uses the rate marked **Use for OPERA lines with this
percentage**. Only one rate per percentage can have it. Two rates with the same percentage are possible (for example
*exempt* and *0 %*): mark the one OPERA lines should get, and use the other only through OPERA overrides per
transaction code. A folio line whose percentage matches no marked rate is refused with a message naming the line.

**Edit** changes the percent, description or matching; **Delete this rate** (in the edit window) removes a rate that no
OPERA override uses. Changes apply to receipts issued afterwards; receipts already issued keep their letter and rate.
Every change is in the audit log.

### 5.4 Settings › Workstations

One line per workstation that can issue a folio. Each one is a separate ATK POS with its own POS ID and certificate.

| Field | Value |
|---|---|
| POS ID | 1, 2, 3 … unique in the unit. Cannot be changed later |
| OPERA Fiscal Terminal ID | Exactly the *Terminal ID* configured in OPERA Cloud for this workstation (see the OPERA manual) |
| Computer name | Windows name of the PC, e.g. `FRONTDESK-01` |
| Signing | *Property default*, or force *Workstation client* / *Server (central)* for this workstation |
| Client address | Only for workstation-client mode. Filled in by the Fiscal Client (`http://<PC IP>:5110`); change it only if the server must reach the PC at another address |

Each workstation is shown as a tile with its POS ID, OPERA terminal, signing and certificate state. **Add workstation** opens one form: in central signing it adds the workstation *and* registers it with ATK in the same step (or imports the key and certificate from ATK's onboarder tool, or registers later). If registration fails, the workstation stays *pending* and its tile offers **Register with ATK** again.

**Removing a workstation.** *Edit › Remove this workstation*: a workstation that has **not issued any receipt** (added by
mistake, wrong POS ID) can be deleted; the deletion is logged with its details. If it was already registered with ATK,
the registration stays at ATK and ATK does not register that POS ID again, so give the replacement a new POS ID. A
workstation **with receipts** cannot be deleted, because its receipts must be kept with it: set its *Status* to
*disabled*; it then no longer fiscalizes.

### 5.5 Signing mode and ATK registration

Every workstation is its own ATK POS with its own key and certificate in both modes. The difference is where the key lives.

| | Workstation client | Central (server) |
|---|---|---|
| Key and certificate | On each PC, in that PC's Windows key store / TPM | On the fiscal server, one per workstation, Windows key store / TPM |
| Installed on PCs | Fiscal Client | Nothing |
| If a PC is off | Its folios wait in the queue until it is back | No effect |
| ATK registration | From the client on each PC | Settings › Workstations › Edit › **Register with ATK** |
| Status | Matches ATK's statement that every invoicing workstation needs the software | **Pending ATK's confirmation** |

**Central mode — registering a workstation**
1. Settings › Business is complete (including fiscalization number and Application ID); Settings › General: ATK environment.
2. Settings › Workstations › **Add workstation** with *Register with ATK now* selected (for an existing workstation: **Register with ATK** on its tile).
3. The server checks the business with ATK, creates a new non-exportable key for this workstation, sends ATK only the
   certificate request (public key) and stores the certificate. The workstation becomes *active*; the certificate expiry
   is shown in the list and on the dashboard.
4. Use **Renew certificate** before it expires (the dashboard warns 30 days ahead).

Typical ATK errors: *404 / not found* — NUI, fiscalization number, unit number or Application ID do not match ATK's
records (check them in EDI); *timeout* — outbound HTTPS to ATK is blocked.

**Changing the mode** (property-wide or for one workstation) means the affected workstations show
*register again* and cannot sign until they are registered in the new place. The old certificates remain in the audit log.

**Workstation client mode — installing the Fiscal Client**

The Fiscal Client is one standard program for all workstations. What makes it *this* workstation's client is a
one-time **pairing code** created on the server. There are two ways to install it:

*A. Installation package (recommended)*
1. Settings › Workstations › the workstation (set to *Workstation client*) › **Fiscal Client**. Check *Server address,
   as &lt;PC&gt; reaches it* (the address the PC uses to open the admin page, e.g. `http://192.168.1.10:5080`) and click
   **Create installation package**.
2. **Download installation package**: a zip named after the POS and the PC, with the client program, `install-client.ps1`,
   `README.txt` and `pairing.json` (server address and the code). The code works **once**, for this workstation, for 2 days.
3. On that PC: unzip, open PowerShell **as administrator** in the folder and run
   `powershell -ExecutionPolicy Bypass -File .\install-client.ps1`

*B. Standard client and code* — copy the release's `client\` folder to the PC and run
`powershell -ExecutionPolicy Bypass -File .\install-client.ps1 -ServerUrl http://192.168.1.10:5080 -PairingCode ABCD-EFGH-JKLM`
with the server address and pairing code shown on the server after *Create installation package*.

The installer copies the client to `C:\Program Files\Opera Cloud Fiscal Solution\Client`, keeps its data in
`%ProgramData%\Opera Cloud Fiscal Solution\Client` (Administrators and SYSTEM only), installs the service
*Opera Cloud Fiscal Solution - Kosovo - Client* (restarts by itself) and opens TCP 5110 for the fiscal server's IP only.
Within a minute the client then, by itself:

1. **pairs** with the server using the code (the code is then used up; the server and the client share a secret that
   signs every request between them, HMAC-SHA256 with the time, so requests cannot be forged or replayed);
2. creates the workstation's **key in the PC's Windows key store**, non-exportable, in the TPM when there is one;
3. **registers with ATK through the server**: the server checks the business with ATK and sends ATK the certificate
   request from the PC; the private key never leaves the PC. The workstation becomes *active* on the server;
4. reports to the server every minute (version, address, printers and COM ports of the PC).

Settings › Workstations shows *Fiscal Client: online / offline since …*, the address, the version and the last contact.
On the PC, `http://localhost:5110` shows the client's status (only on that PC).

**How receipts are signed.** The server builds the receipt and asks the PC's client to sign it. The client checks that
the receipt is for its own business, unit and POS before signing and keeps a local log of everything it signed
(`signed.log` in the data folder). The server checks the signature against the workstation's certificate before the
receipt is archived and sent to ATK. If the PC is off or the service is stopped, the folio is refused with
*the Fiscal Client … does not answer*: start the PC or the service and send the folio again from OPERA.

**Reinstalling or replacing the PC**: *Create a new installation package* and install it; the client pairs again,
creates a new key and registers with ATK again (the old certificate stays in the audit log). Running
`install-client.ps1` again without a new package only upgrades the program; the key and registration are kept.
**Uninstall**: `Stop-Service OperaCloudFiscalKosovoClient; sc.exe delete OperaCloudFiscalKosovoClient`, then remove the
program folder and the firewall rule *Fiscal Client (5110)*.

> The connection between the server and the clients is HTTP on the hotel LAN, protected by the per-workstation secret
> (HMAC) and the firewall rule; the receipts themselves are signed by the workstation's key. HTTPS between server and
> clients is planned.

### 5.6 OPERA Cloud and FLIP

Follow [manual-opera-cloud-ofis-flip.md](manual-opera-cloud-ofis-flip.md). Afterwards, check the **FLIP messages** page (left menu):
every folio OPERA sends must appear there. The answer to OPERA contains the fiscal lines to print (`FiscalText`),
including the daily number and, when ATK was unreachable, OFFLINE; see the folio printing manual.

### 5.7 Corrections: cancellation and paper-block receipts

**Cancellation** (*Receipts › open a receipt › Cancel receipt…*, permission *Cancel receipts*; Admin and Supervisor
by default). For an error found after a sale receipt was issued: enter the reason (printed on the receipt as
*ARSYEJA E ANULIMIT*). A **cancellation receipt** (*KUPON FISKAL I ANULUAR*) with the same lines and amounts is issued,
linked to the original, signed with the same workstation's key and sent to ATK. A receipt can be cancelled once, and
not when a return already refers to it; to give back part of an amount, issue a return (credit folio) in OPERA.

**Paper-block receipts** (*Receipts › Enter paper receipt*, permission *Enter paper-block receipts*). When a
workstation or the fiscal server stops completely, receipts are written from ATK's special paper block. Within 48
hours the system must work again; within 5 days after that, enter every paper receipt: workstation, the serial number
printed on the paper, the date and time written on it, who wrote it, the lines and the payment. The receipt is signed,
keeps the paper serial and the written time, and is sent to ATK. A serial can be entered only once.

**Daily number and OFFLINE.** Every receipt gets a daily number per workstation (*Kupon fiskal ditor nr.*). A receipt
issued while ATK cannot be reached is marked **OFFLINE**: on its copy, on the receipt page and in the answer to OPERA,
which must print it on the folio. Unsent receipts are resent automatically; the dashboard warns at 48 hours and before
the 10th of the following month.

### 5.8 Settings › Printing: where the guest's receipt is printed

**Fiscal receipt for the guest › Change** chooses one of two ways:

| Choice | What happens |
|---|---|
| **In the fiscal block of the OPERA folio** (default) | OPERA prints the fiscal data and the QR on its folio from the answer it receives through FLIP. Set up the folio template as in the folio printing manual |
| **On a thermal printer at the workstation** | Right after fiscalization the fiscal server prints the **whole receipt** (taxpayer, items, totals, payments, VAT, receipt number, date, SEF ID, NUIKF, daily number, OFFLINE when it applies, QR, *e-kupon*, fiscal logo) on the workstation's own receipt printer, 58 or 80 mm, as in ATK's sample receipts. OPERA still receives the fiscal data |

**Workstation printers › Set up** (one printer per workstation):

- **Connection**
  - *Network (Ethernet / Wi-Fi)* — for printers at the front desk. The Epson TM-T88VII has a built-in Ethernet port.
    Enter the printer's IP address; the port is 9100 unless you add another (`192.168.1.50:9100`). Give the printer a
    fixed IP address (or a DHCP reservation) and allow the fiscal server to reach port 9100.
  - *USB, on the fiscal server* — the printer is plugged into the fiscal server and installed in Windows (Epson
    Advanced Printer Driver, or Windows' generic driver). Pick its name from the list of printers found on the server.
    The service runs as Local System, so install the printer **for all users**. The job is sent as raw ESC/POS
    through the spooler.
  - *Serial (COM port), on the fiscal server* — RS-232 cable to the server's COM port (or a USB printer exposed as a
    COM port by Epson's *TM Virtual Port Driver*). Choose the port, the speed and the flow control; 8 data bits, no
    parity, 1 stop bit. Epson TM printers leave the factory at 38400 bps with hardware (DTR/DSR) flow control: check the
    printer's self-test page (hold FEED while switching on) and use the same values.

  - *USB / Serial (COM port), on this workstation (Fiscal Client)* — only for workstations in workstation-client mode:
    the printer is plugged into the front-desk PC and the Fiscal Client on that PC prints. Choose the printer or COM
    port from the ones the client reported (install a USB printer on the PC for all users).

  Without the Fiscal Client, USB and serial printers must be connected to the **fiscal server**: the front-desk PCs
  only run a browser. Connect front-desk printers by network, or use the Fiscal Client.
- **Printer model** sets the paper width and characters per line: *Epson TM-T88 (VII, VI, V), 80 mm* — 42 characters
  (180 dpi), the default; *Epson TM-T88, 58 mm* — 30; *Other ESC/POS printer, 80 mm* — 48 (most 203 dpi printers);
  *Other, 58 mm* — 32.
- **Print automatically after each fiscalization**: on by default. When off, receipts are printed from the receipt page.

**Test print** sends a test page (not a fiscal receipt) with the workstation, the paper width, the letters ë Ë ç Ç and a
QR code. If the letters print wrongly, set the printer's code page to PC850 (most printers' default for Europe).

**Originals and copies.** The first successful print of a receipt is the original. *Receipts › open a receipt › Print
on thermal printer* prints it if it was never printed (e.g. the printer was off); afterwards the button reads *Print copy
on thermal printer* and the print carries **KOPJE E KUPONIT** (permission *Download and print receipt copies*). An
OPERA reprint of the same folio returns the same receipt and prints nothing. A cancellation receipt is printed on the
original workstation's printer.

**When a print fails** (printer off, out of paper, wrong address) the receipt is still fiscalized and sent to ATK; the
failure is written to the audit log (*PRINT_FAILED*) and shown on the dashboard until the receipt is printed. Every print
is logged (*RECEIPT_PRINTED*, original or copy) and every test page (*TEST_PRINT*).

### 5.9 Reports and logs per workstation

Receipts, Audit log and Export all filter by **workstation**. *Receipts* and *Audit log* have **Export CSV / PDF**
buttons that export exactly the filtered view (period, workstation, status or event). *Export* also offers a
**Summary by workstation**: sales, returns, net total, net VAT and ATK status counts per POS for the period, with a
total line. The audit log for one workstation contains the events linked to it: its receipts, ATK transmissions,
reprints, registration and settings changes. Every export is itself logged with the workstation and a SHA-256 of the file.

## 6. Check the installation

- [ ] `http://<server>:5080/health` answers `{"status":"ok", "version": …}`; the version is also shown at the bottom of the menu.
- [ ] Sign-in works; the default admin password is changed and removed from the settings file.
- [ ] Dashboard shows the business, all workstations and certificate dates.
- [ ] Audit log › **Integrity check** reports the chain as intact.
- [ ] From the FLIP machine: `Invoke-WebRequest http://<SERVER-IP>:5100/flip -Method Post -Body test -Headers @{Authorization="Bearer <token>"}`
      returns the test reply and the message appears under FLIP messages; without the header it returns 401.
- [ ] Backup task (section 7) ran once and the event log shows *Backup OK*.

## 7. Backup

The database is the legal archive of the hotel's fiscal receipts and logs. Back it up daily to a different disk or NAS.

1. Create a folder on the NAS, e.g. `\\nas\hrs-fiscal-backup`, writable by a dedicated backup account.
2. As that account, store the database password in `%APPDATA%\postgresql\pgpass.conf`:
   `127.0.0.1:5432:hrs_fiscal:hrs_fiscal_owner:choose-a-long-password` (file readable only by that account).
3. Task Scheduler › Create Task: run as the backup account, *whether user is logged on or not*, daily at 04:00:
   ```
   Program:   powershell.exe
   Arguments: -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\Opera Cloud Fiscal Solution\Server\backup-db.ps1" -Target "\\nas\hrs-fiscal-backup" -KeepDays 90
   ```
4. The script writes `hrs_fiscal_<date>.dump` and a `.sha256` file, checks the dump is readable and writes
   *Backup OK* / *Backup FAILED* to the Windows Application log (source *Fiscal Solution Backup*). Monitor that event.

Keep at least one monthly copy off site. `-KeepDays` only removes old backup files, never data in the database.

**Restore** (new server or disaster): install PostgreSQL, create the empty database and owner (section 3), then
`pg_restore -h localhost -U hrs_fiscal_owner -d hrs_fiscal --no-owner <file>.dump`, install the fiscal server
pointing to it, and run Audit log › Integrity check. In central mode the signing keys are **not** in the backup
(they cannot leave the server): register the workstations again on the new server.

## 8. Upgrade and uninstall

**Upgrade**: back up first, then unzip the new release and run `install-server.ps1` again with the same parameters
(without `-AdminPassword`). The service is replaced; the database is kept and migrated automatically on start.
Settings in `appsettings.Production.json` are rewritten from the parameters.

**Uninstall**: `Stop-Service OperaCloudFiscalKosovo; sc.exe delete OperaCloudFiscalKosovo`, remove the program folder and the two
firewall rules *Fiscal Solution …*. **Do not drop the database**: the receipts and logs must be kept for the legal
retention period. Export or hand over a backup instead.

## 9. Troubleshooting

| Symptom | Check |
|---|---|
| Service stops right after start | Event Viewer › Windows Logs › Application, source *Hrs.Fiscal.Server* (or *.NET Runtime*). Faster: stop the service and run `& "C:\Program Files\Opera Cloud Fiscal Solution\Server\Hrs.Fiscal.Server.exe" --contentRoot "C:\Program Files\Opera Cloud Fiscal Solution\Server" --environment Production` in an elevated PowerShell to see the error on screen. Usual causes below |
| *Role hrs_fiscal_app is missing* / *permission denied to create role* | As `postgres`: `CREATE ROLE hrs_fiscal_app NOLOGIN;` then `Start-Service OperaCloudFiscalKosovo` (section 3) |
| *No such host is known* | The `Host=` in the connection string cannot be resolved. For a database on the same server use `Host=127.0.0.1`; for another machine use its IP address |
| *password authentication failed* / *database does not exist* | Connection string in `appsettings.Production.json`: user, password, database name |
| *address already in use* | Port 5080 or 5100 is used by another program: re-run the installer with `-AdminPort`/`-FlipPort` |
| Admin page not reachable from another PC | Firewall rule *Fiscal Solution admin (5080)*; network profile must be Domain or Private, not Public |
| FLIP: *HttpClient.Timeout … elapsing*, but PowerShell tests work | FLIP's EndPoint Url uses the computer name: use the IP address (`http://<SERVER-IP>:5100/flip`) |
| FLIP gets *401 Unauthorized* | Token missing or wrong in FLIP, or FLIP uses another header: the FLIP messages page shows refused requests (mode *rejected*, header check *missing*/*invalid*) and which headers FLIP sent. Set the header name or generate a new token |
| FLIP messages do not arrive | Firewall rule *Fiscal Solution FLIP (5100)* and its allowed IP (`-FlipSourceIp`); FLIP's partner address; OPERA fiscal configuration |
| ATK registration or sending times out | Outbound HTTPS to `fiskalizimi(-test).atk-ks.org` through the hotel firewall/proxy |
| "Register with ATK" says to use the client | The workstation is in workstation-client mode (Signing column): install the Fiscal Client (5.5) |
| Fiscal Client stays *not installed* | On the PC: `http://localhost:5110` shows the last problem. *pairing code … expired / used*: create a new package. Cannot reach the server: the server address in the package, firewall rule *Fiscal Solution admin (5080)* |
| Folio refused: *Fiscal Client … does not answer* | PC on, service *Opera Cloud Fiscal Solution - Kosovo - Client* running, firewall rule *Fiscal Client (5110)* on the PC, address shown under Settings › Workstations |
| Workstation shows *register again* | The signing mode was changed after registration. Register it again |
| Dashboard: receipts waiting | ATK unreachable; receipts are kept and resent automatically. ATK's limit is 48 hours |
| Receipts do not print / dashboard *Receipt … was not printed* | Settings › Printing › Test print. *Cannot reach the printer*: IP address, printer on, port 9100 open, same network. *Windows printer … was not found*: exact name, printer installed for all users on the server. *Serial*: port number, speed and flow control as on the printer's self-test page; no other program (e.g. OPOS) holding the port |
| Printed letters wrong (ë, ç) | Set the printer's code page to PC850 with its utility or self-test menu |
| Integrity check fails | Stop and escalate to the software maintainer: the archive was changed outside the application. Do not repair it yourself |

## 10. Security checklist

- [ ] Only the FLIP machine can reach port 5100, and FLIP authenticates with the access token (*Require the token* on);
      only hotel admin PCs need port 5080.
- [ ] Personal user accounts with strong passwords; no shared *admin* account in daily use.
- [ ] `appsettings.Production.json` readable only by Administrators and SYSTEM (the installer sets this).
- [ ] The `postgres` superuser password is in the hotel's password safe and not used by the application.
- [ ] Daily backup verified; monthly copy off site.
- [ ] Windows updates and antivirus active; exclude the PostgreSQL data folder from real-time scanning.
