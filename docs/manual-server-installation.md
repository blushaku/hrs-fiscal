# HRS Fiscal Server — Installation and Configuration Manual

For HRS implementation engineers. One HRS Fiscal Server is installed per hotel (property), on the hotel LAN, close to
Oracle FLIP. Time needed: about 1 hour, plus ATK registration of the workstations.

> Status (October 2026): the server, admin UI, archive, central signing and FLIP capture mode are ready. Live
> fiscalization of OPERA folios follows once Oracle's FLIP interface specification is implemented. The HRS Fiscal
> Client (workstation signing) is not released yet: until then, use **central signing** for tests.

## 1. What gets installed

| Component | Where | Purpose |
|---|---|---|
| HRS Fiscal Server (Windows service `HRSFiscalServer`) | Hotel server | Receives folios from FLIP, builds and signs receipts, sends them to ATK, archives everything, admin web UI |
| PostgreSQL 16 | Same server (recommended) or a database server on the LAN | Append-only archive of receipts, ATK transmissions, OPERA/FLIP messages and the audit log |
| HRS Fiscal Client (only in *workstation client* signing mode) | Every PC that can issue a folio | Holds that workstation's ATK key and signs its receipts |

```
OPERA Cloud ─OFIS─▶ Oracle FLIP (on-premise) ──LAN :5100──▶ HRS Fiscal Server ──HTTPS :443──▶ ATK (fiskalizimi.atk-ks.org)
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
- `HRS-Fiscal-Server-win-x64.zip` (release package: application, `install-server.ps1`, `backup-db.ps1`).

**Network**

| Direction | From → To | Port | Why |
|---|---|---|---|
| Inbound | FLIP machine → HRS server | TCP 5100 (HTTP) | Folios from OPERA via FLIP |
| Inbound | Admin PCs → HRS server | TCP 5080 (HTTP) | Admin web UI |
| Outbound | HRS server → `fiskalizimi.atk-ks.org` (production), `fiskalizimi-test.atk-ks.org` (test) | TCP 443 | ATK API. Allow through the hotel firewall/proxy without TLS inspection |
| Local | HRS server → PostgreSQL | TCP 5432 | Only if PostgreSQL is on another machine |

Nothing is exposed to the internet. Do not forward ports 5080/5100 from outside.

**From the hotel / ATK** (collect before installation)
- Business **NUI**, **Fiscalization number** (from ATK's EDI), **unit (branch) number** registered with ATK, business name, city.
- **Application ID** of HRS Fiscal Solution (issued by ATK on certification; for tests the ATK TEST Application ID).
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
   machine, allow only the HRS server's IP in `pg_hba.conf` (`host hrs_fiscal hrs_fiscal_owner <HRS-IP>/32 scram-sha-256`).

The tables are created automatically when the HRS service starts the first time (migrations `V001`…).

## 4. Install the HRS Fiscal Server

1. Install the **.NET 8 Windows Hosting Bundle**, then restart the server (or run `net stop was /y` and `net start w3svc`).
2. Unzip `HRS-Fiscal-Server-win-x64.zip` to a temporary folder.
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
   | `-InstallDir` | `C:\Program Files\HRS Fiscal\Server` | Program folder |
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
   the `AdminPassword` value from `C:\Program Files\HRS Fiscal\Server\appsettings.Production.json` and restart the service.

**Roles**

| Role | Can |
|---|---|
| Cashier | View receipts, download receipt copies (marked *KOPJE E KUPONIT*, logged) |
| Supervisor | + exports (CSV/PDF), FLIP messages page |
| Auditor | Read everything, exports; no changes. For the hotel's accountant or a tax inspector |
| Admin | + all settings, users and workstations |

Accounts are never deleted, only deactivated, so the audit log always shows who did what.

## 5. Configure

All settings below are stored in the database and every change is written to the audit log with old and new value.

### 5.1 Settings › General

| Setting | Value |
|---|---|
| ATK environment | **Test** until the hotel goes live, then **Production**. Never send test receipts to production |
| Application ID | The ID ATK issued for HRS Fiscal Solution (test or production) |
| Timeout / retry | 10 s / 2 min (defaults) |
| **Signing** | *Workstation client* or *Central* — see 5.4 |
| OPERA overrides | Off (default). Turn on only if OPERA cannot send a field correctly; then set the codes under *OPERA overrides & VAT* |
| OPERA / FLIP connection | **Capture** during setup (records what FLIP sends). **Live** once fiscalization is enabled |
| FLIP access token | **Generate token**, copy it into FLIP's partner configuration (shown only once), keep *Require the token* on. Header: `Authorization` unless FLIP uses another one. *Generate new token* replaces the old one at once, so update FLIP at the same time |
| VAT calculation | Round VAT half-up (default) — pending ATK's official rule |
| Retention | Placeholder 10 years until the legal period is confirmed. Nothing is ever deleted automatically |

### 5.2 Settings › Business

NUI, business name, fiscalization number, VAT number, unit (branch) number, unit name, city (printed as location),
address and the OPERA property code. **NUI and unit number cannot be changed** after saving, because every receipt
refers to them — check them against the ATK registration before saving.

### 5.3 Settings › Workstations

One line per workstation that can issue a folio. Each one is a separate ATK POS with its own POS ID and certificate.

| Field | Value |
|---|---|
| POS ID | 1, 2, 3 … unique in the unit. Cannot be changed later |
| OPERA Fiscal Terminal ID | Exactly the *Terminal ID* configured in OPERA Cloud for this workstation (see the OPERA manual) |
| Computer name | Windows name of the PC, e.g. `FRONTDESK-01` |
| Signing | *Property default*, or force *Workstation client* / *Server (central)* for this workstation |
| Client address | Only for workstation-client mode: `https://<PC IP>:5100` |

A new workstation starts as *pending*. It becomes *active* only after ATK registration (5.4).

### 5.4 Signing mode and ATK registration

Every workstation is its own ATK POS with its own key and certificate in both modes. The difference is where the key lives.

| | Workstation client | Central (server) |
|---|---|---|
| Key and certificate | On each PC, in that PC's Windows key store / TPM | On the HRS server, one per workstation, Windows key store / TPM |
| Installed on PCs | HRS Fiscal Client | Nothing |
| If a PC is off | Its folios wait in the queue until it is back | No effect |
| ATK registration | From the client on each PC | Settings › Workstations › Edit › **Register with ATK** |
| Status | Matches ATK's statement that every invoicing workstation needs the software | **Pending ATK's confirmation** |

**Central mode — registering a workstation**
1. Settings › General: ATK environment and Application ID are set; Settings › Business is complete.
2. Settings › Workstations › **Edit** the workstation › **Register with ATK**.
3. The server checks the business with ATK, creates a new non-exportable key for this workstation, sends ATK only the
   certificate request (public key) and stores the certificate. The workstation becomes *active*; the certificate expiry
   is shown in the list and on the dashboard.
4. Use **Renew certificate** before it expires (the dashboard warns 30 days ahead).

Typical ATK errors: *404 / not found* — NUI, fiscalization number, unit number or Application ID do not match ATK's
records (check them in EDI); *timeout* — outbound HTTPS to ATK is blocked.

**Changing the mode** (property-wide or for one workstation) means the affected workstations show
*register again* and cannot sign until they are registered in the new place. The old certificates remain in the audit log.

**Workstation client mode** — install the HRS Fiscal Client on each PC and register it there (separate client manual,
available with the client release).

### 5.5 OPERA Cloud and FLIP

Follow [manual-opera-cloud-ofis-flip.md](manual-opera-cloud-ofis-flip.md). Afterwards, check the **FLIP messages** page (left menu):
every folio OPERA sends must appear there.

## 6. Check the installation

- [ ] `http://<server>:5080/health` answers `{"status":"ok"}`.
- [ ] Sign-in works; the default admin password is changed and removed from the settings file.
- [ ] Dashboard shows the business, all workstations and certificate dates.
- [ ] Audit log › **Integrity check** reports the chain as intact.
- [ ] From the FLIP machine: `Invoke-WebRequest http://<HRS-IP>:5100/flip -Method Post -Body test -Headers @{Authorization="Bearer <token>"}`
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
   Arguments: -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\HRS Fiscal\Server\backup-db.ps1" -Target "\\nas\hrs-fiscal-backup" -KeepDays 90
   ```
4. The script writes `hrs_fiscal_<date>.dump` and a `.sha256` file, checks the dump is readable and writes
   *Backup OK* / *Backup FAILED* to the Windows Application log (source *HRS Fiscal Backup*). Monitor that event.

Keep at least one monthly copy off site. `-KeepDays` only removes old backup files, never data in the database.

**Restore** (new server or disaster): install PostgreSQL, create the empty database and owner (section 3), then
`pg_restore -h localhost -U hrs_fiscal_owner -d hrs_fiscal --no-owner <file>.dump`, install the HRS server
pointing to it, and run Audit log › Integrity check. In central mode the signing keys are **not** in the backup
(they cannot leave the server): register the workstations again on the new server.

## 8. Upgrade and uninstall

**Upgrade**: back up first, then unzip the new release and run `install-server.ps1` again with the same parameters
(without `-AdminPassword`). The service is replaced; the database is kept and migrated automatically on start.
Settings in `appsettings.Production.json` are rewritten from the parameters.

**Uninstall**: `Stop-Service HRSFiscalServer; sc.exe delete HRSFiscalServer`, remove the program folder and the two
firewall rules *HRS Fiscal …*. **Do not drop the database**: the receipts and logs must be kept for the legal
retention period. Export or hand over a backup instead.

## 9. Troubleshooting

| Symptom | Check |
|---|---|
| Service stops right after start | Event Viewer › Windows Logs › Application, source *Hrs.Fiscal.Server* (or *.NET Runtime*). Faster: stop the service and run `& "C:\Program Files\HRS Fiscal\Server\Hrs.Fiscal.Server.exe" --contentRoot "C:\Program Files\HRS Fiscal\Server" --environment Production` in an elevated PowerShell to see the error on screen. Usual causes below |
| *Role hrs_fiscal_app is missing* / *permission denied to create role* | As `postgres`: `CREATE ROLE hrs_fiscal_app NOLOGIN;` then `Start-Service HRSFiscalServer` (section 3) |
| *No such host is known* | The `Host=` in the connection string cannot be resolved. For a database on the same server use `Host=127.0.0.1`; for another machine use its IP address |
| *password authentication failed* / *database does not exist* | Connection string in `appsettings.Production.json`: user, password, database name |
| *address already in use* | Port 5080 or 5100 is used by another program: re-run the installer with `-AdminPort`/`-FlipPort` |
| Admin page not reachable from another PC | Firewall rule *HRS Fiscal admin (5080)*; network profile must be Domain or Private, not Public |
| FLIP gets *401 Unauthorized* | Token missing or wrong in FLIP, or FLIP uses another header: the FLIP messages page shows refused requests (mode *rejected*, header check *missing*/*invalid*) and which headers FLIP sent. Set the header name or generate a new token |
| FLIP messages do not arrive | Firewall rule *HRS Fiscal FLIP (5100)* and its allowed IP (`-FlipSourceIp`); FLIP's partner address; OPERA fiscal configuration |
| ATK registration or sending times out | Outbound HTTPS to `fiskalizimi(-test).atk-ks.org` through the hotel firewall/proxy |
| "Register with ATK" says to use the client | The workstation is in workstation-client mode (Signing column) |
| Workstation shows *register again* | The signing mode was changed after registration. Register it again |
| Dashboard: receipts waiting | ATK unreachable; receipts are kept and resent automatically. ATK's limit is 48 hours |
| Integrity check fails | Stop and escalate to HRS: the archive was changed outside the application. Do not repair it yourself |

## 10. Security checklist

- [ ] Only the FLIP machine can reach port 5100, and FLIP authenticates with the access token (*Require the token* on);
      only hotel admin PCs need port 5080.
- [ ] Personal user accounts with strong passwords; no shared *admin* account in daily use.
- [ ] `appsettings.Production.json` readable only by Administrators and SYSTEM (the installer sets this).
- [ ] The `postgres` superuser password is in the hotel's password safe and not used by the application.
- [ ] Daily backup verified; monthly copy off site.
- [ ] Windows updates and antivirus active; exclude the PostgreSQL data folder from real-time scanning.
