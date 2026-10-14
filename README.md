# Opera Cloud Fiscal Solution - Kosovo

Kosovo fiscalization software (**SEF – Softuer Elektronik Fiskal**) for hotels on **Oracle OPERA Cloud**.
OPERA Cloud sends fiscal payloads via **OFIS with FLIP** (installed on-premise). FLIP delivers them over the hotel LAN
to the fiscal server. Every workstation that issues folios is its own ATK POS with its own key and certificate.
**Signing is selectable** (Settings › General, overridable per workstation). The default is central signing: the
server signs with a separate non-exportable key per workstation. The alternative, signing by the Fiscal Client on each
workstation, is not built yet.
Everything is archived in an append-only PostgreSQL store.

```
OPERA Cloud ─OFIS─▶ FLIP (on-prem) ──LAN──▶ fiscal server ──route by Fiscal Terminal ID──▶ Fiscal Client (each workstation)
   ▲                  │ mapping, numbering                                  │ own PosId + non-exportable key
   │                  │ archive + audit log (PostgreSQL)                    │ signs PosCoupon + QR
   └── fiscal data ◀──┘ offline queue, admin UI, exports   ◀── result ─────┘──▶ ATK /pos/coupon
```

## Documentation

Manuals (English):

- [Server installation and configuration](docs/manual-server-installation.md)
- [OPERA Cloud OFIS/FLIP configuration](docs/manual-opera-cloud-ofis-flip.md)
- [OPERA folio: printing the fiscal block](docs/manual-opera-folio-fiscal-print.md)
- [Testing on an OPERA Cloud demo](docs/opera-demo-test-plan.md)

ATK certification:

- [Certification guide](docs/certification-guide.md): the procedure, the documents and what is still open
- [ATK compliance review](docs/atk-compliance-review.md): every ATK requirement checked against the app and documents
  (12.10.2026: 28 met, 6 partly, 0 missing, 2 depend on Oracle, 4 need a written answer from ATK)
- [ATK TEST results of 08.10.2026](docs/atk-test-results-2026-10-08.md)
- [Requirements and design notes](docs/requirements-and-design.md)

Submission package for ATK (Albanian; personal data and signatures are left blank on purpose and are filled in on
paper before submitting):

| Art. | Document |
|---|---|
| 5.5.1 | [Kërkesë për certifikim](docs/submission/01-kerkese-per-certifikim.md) (application request) |
| 5.5.5 | [Përshkrimi i përgjithshëm i zgjidhjes softuerike](docs/submission/02-pershkrimi-i-pergjithshem.md) (general description) |
| 5.5.6 | [Manuali i përdoruesit](docs/submission/03-manuali-i-perdoruesit.md) (user manual) |
| 5.5.7 | [Deklarata e pajtueshmërisë](docs/submission/04-deklarata-e-pajtueshmerise.md) (compliance declaration, signed last) |

## Repository layout

| Path | What |
|---|---|
| `src/Hrs.Fiscal.Core` | Shared fiscal library: ATK protobuf model, coupon builder and validation, VAT, ECDSA signing (DER), QR, CSR, ATK API client |
| `src/Hrs.Fiscal.Server` | Property server (ASP.NET Core, Windows service): admin web UI (receipts, audit log, exports, settings), archive, migrations, FLIP endpoint (capture / live), central signing, offline queue |
| `src/Hrs.Fiscal.Client` | Workstation client for *workstation client* signing mode (Windows service): key and certificate, onboarding, signing. **Skeleton** |
| `tests/Hrs.Fiscal.Core.Tests` | Unit tests (xUnit) |
| `tests/Hrs.Fiscal.Server.Tests` | Integration tests against PostgreSQL (set `HRS_TEST_PG`) |
| `db/migrations` | PostgreSQL schema |
| `db/tests` | Schema self-test (immutability, hash chain, tamper detection) |
| `deploy/` | Windows installer (`install-server.ps1`) and daily backup script (`backup-db.ps1`) |
| `docs/` | Manuals, certification guide, ATK compliance review, submission documents (`docs/submission`) |
| `tools/i18n` | Source of the Albanian and Serbian translations and the script that builds `i18n/*.json` |

## Build and test

Requirements: .NET 8 SDK, PostgreSQL 14+ (16 recommended). Open `HrsFiscal.sln` in Visual Studio 2022, or:

```bash
dotnet build
dotnet test
```

Database:

```bash
createdb hrs_fiscal
psql -d hrs_fiscal -v ON_ERROR_STOP=1 -f db/migrations/V001__initial_schema.sql
# self-test on a throwaway database only (it writes and tampers with test rows):
createdb hrs_fiscal_test && psql -d hrs_fiscal_test -f db/migrations/V001__initial_schema.sql \
  && psql -d hrs_fiscal_test -v ON_ERROR_STOP=1 -f db/tests/schema_test.sql
```

## Admin web UI (fiscal server)

| Page | What it does | Roles |
|---|---|---|
| Dashboard | Today's receipts, accepted, waiting to send (with the 48 h deadline), rejected; workstation status; certificate expiry; alerts | all |
| Receipts | Search by receipt no., NUIKF, folio or cashier; filter by date, workstation, type (sale, return, cancel) and status; paging | all |
| Receipt detail | Fiscal data, daily number, OFFLINE mark, QR, every send attempt to ATK, items and VAT, linked returns and cancellation, original OPERA/FLIP message, signed payload | all |
| Cancel receipt | Cancel receipt ("Kupon i anulimit") with a required reason, same lines and amounts, linked to the original; once per receipt | Supervisor, Admin |
| Enter paper receipt | Receipts written from the special paper block while the system was down: serial, written date and time, workstation, lines, payment; signed and sent to ATK; serial unique | Supervisor, Admin |
| Download copy (PDF) | 80 mm receipt copy (Annex F layout) marked "KOPJE E KUPONIT", logged as REPRINT | Cashier, Supervisor, Admin |
| Print on thermal printer | Prints the receipt on its workstation's printer: the original if it was never printed, otherwise a copy marked "KOPJE E KUPONIT" | Cashier, Supervisor, Admin |
| Audit log | Search all events; **integrity check** of both hash chains (logged) | all |
| Export | Receipts, unsent receipts or audit log as CSV (UTF-8) or PDF; every export logged with a SHA-256 | Supervisor, Admin, Auditor |
| Settings | General (retention, backup, ATK environment, timeouts, VAT rounding); business and unit, set up in two steps (NUI, fiscalization no., unit no. and ApplicationId validated with ATK, then the rest prefilled from ATK's answer); signing mode; workstations (POS ID ↔ OPERA Fiscal Terminal, ATK registration); VAT rates, user-defined (the list starts empty; one rate per percentage matches OPERA); units with ATK's list as suggestions; optional OPERA overrides per transaction code and payment method; users | Admin |

**Printing** (Settings › Printing): the guest's fiscal receipt is printed either in the fiscal block of the OPERA folio
(default) or by the fiscal server on each workstation's **thermal printer** (58/80 mm, ESC/POS, e.g. Epson TM-T88VII):
connected by network (IP:port 9100), by USB to the fiscal server (Windows printer, raw) or by serial COM port to the
fiscal server (speed, flow control). The printer model sets the characters per line (Epson TM-T88: 42 on 80 mm). With thermal printing the whole receipt (items, VAT, totals,
NUIKF, daily number, OFFLINE, QR, e-kupon) prints automatically after fiscalization (`Printing/`); failures are
logged and shown on the dashboard, and the receipt page reprints it as a copy.

The interface is in **Albanian** (default), **Serbian** (Latin script) and **English**, switched with SQ · SR · EN in
the header (cookie `lang`). Translations live in `src/Hrs.Fiscal.Server/i18n/*.json`, generated by
`tools/i18n/build.py`. Receipt texts are Albanian, as in ATK's samples.

Background checks: the server compares its clock with ATK's server time every 15 minutes (alert and audit entry above
30 s drift, `CLOCK_DRIFT` / `CLOCK_OK`); the dashboard warns about receipts unsent for 48 hours and, around the 10th of
the month, about last month's receipts still unsent.

Every settings change is written to the audit log with its old and new values. Failed logins are logged, and an
account is blocked for 15 minutes after 5 failures. Users and workstations are deactivated, never deleted.

All receipt fields (item name, unit, quantity, price, VAT letter, ATK category, payment type) come from OPERA via
FLIP. The per-code mappings in Settings are **optional overrides** for exceptions, switched on or off under Settings › General
(`opera_overrides_enabled`, off by default). The fiscal server validates every folio
(known VAT letter, VAT consistent with the rates, totals = payments) before sending it to ATK.

No fiscal printer is involved. With folio printing, OPERA prints the fiscal data (SEF ID, receipt no., daily no., NUIKF, ATK transaction,
QR, "e-kupon", OFFLINE when ATK was unreachable) on the folio from the FLIP response, which also carries the
ready-to-print lines (`FiscalText`). The OPERA folio template needs a fiscal block for this.

### Run it locally

```bash
# 1. PostgreSQL database and owner (migrations run automatically on start)
createdb hrs_fiscal
# 2. Configure src/Hrs.Fiscal.Server/appsettings.json (ConnectionStrings:Fiscal), or use environment variables:
export ConnectionStrings__Fiscal="Host=localhost;Database=hrs_fiscal;Username=postgres;Password=..."
# 3. Optional: demo data (empty database only). It creates users admin/supervisor/cashier/auditor
#    with the passwords demo-password-0..3. Change them before any real use.
dotnet run --project src/Hrs.Fiscal.Server -- seed-demo
# 4. Start it and open http://localhost:5080
dotnet run --project src/Hrs.Fiscal.Server
```

Without demo data, create the first admin with `Bootstrap:AdminPassword` (used once, then remove it) or with
`dotnet run --project src/Hrs.Fiscal.Server -- create-user admin Admin "Administrator"`, which reads the password from stdin.

Integration tests: `HRS_TEST_PG="Host=localhost;Username=postgres;Password=..." dotnet test`. Each run creates and drops
its own database.

### Configuration layers
- **appsettings.json** on each machine: DB connection, Kestrel endpoints (LAN IP and port), time zone (default Europe/Belgrade).
- **Database settings** (Admin UI): everything business-related, audited.

### TLS inside the hotel (planned)
No public certificates are needed; nothing is exposed to the internet. The server will create a property CA at
install, issue its own HTTPS certificate and a client certificate for each workstation on enrolment, and use
mutual TLS between server and clients. The CA certificate is installed on admin PCs once, e.g. through group policy.
ATK signing certificates are separate: one per workstation, issued by ATK's CA.

### Branding
The admin UI uses a Microsoft 365-style theme (Segoe UI, square corners) in navy `#000254` with orange `#D6470F` for
main buttons. The product logo (cloud with a fiscal receipt and check mark) is original artwork in `wwwroot/img`
(`logo.svg`, `mark.svg`, `mark-white.svg`) and `wwwroot/favicon.png`; everything is served locally (no internet needed).

### Licences
- QuestPDF (PDF exports) is used under its **Community licence**: free for companies under USD 1M annual revenue.
  Confirm eligibility; otherwise buy a Professional licence or swap the PDF library.
- QRCoder, Dapper, Npgsql, Google.Protobuf: MIT / Apache 2.0 / BSD.

## Live fiscalization (OPERA → FLIP → ATK)

With Settings › General › OPERA / FLIP connection = **Live**, each OPERA Cloud fiscal payload (JSON, as sent by
on-premise FLIP) is parsed (`Flip/OperaPayload.cs`), turned into an ATK receipt for the workstation named by
`DocumentInfo.TerminalId`, signed on the server (central mode), archived together with the original payload, sent to
ATK, and answered with the fiscal data (`Flip/FlipFiscalizer.cs`). Idempotent per FiscalFolioId; ATK outages go to the
offline queue (`Flip/AtkResendService.cs`). Verified on 09.10.2026 against ATK TEST with a payload captured from OPERA
Cloud 26.4: accepted, transaction 14174883308481107345. The response format to FLIP is provisional until Oracle's
specification. Mapping rules: [manual-opera-cloud-ofis-flip.md §8](docs/manual-opera-cloud-ofis-flip.md).

## Testing on an OPERA Cloud demo

See [docs/opera-demo-test-plan.md](docs/opera-demo-test-plan.md). The server's **capture mode** stores every
message FLIP sends (any path under `/flip`, or raw TCP via `Flip:TcpPort`) and answers with a configurable test reply.
That lets the real OFIS/FLIP payload be recorded on a demo before Oracle's specification arrives. Windows install:
`deploy/install-server.ps1`.

## Testing against ATK (atk-test-console)

`src/Hrs.Fiscal.Cli` talks to ATK's **TEST** environment (`fiskalizimi-test.atk-ks.org`). It refuses production unless
explicitly forced, because ATK forbids testing in production (technical requirements Art 26).

**GUI:** start `atk-test-console` without arguments (or double-click `atk-test-console.exe`) to open the **ATK Test Console** in the
browser: workstation setup (import from ATK's onboarder or register), receipt editor with examples, send and see ATK's
answer with the QR, QR verification, one-click returns, the full test set and a history of everything sent. It listens on
127.0.0.1 only and requires a per-session token, so other websites cannot use it.

```bash
# 1. Register a test workstation (generates a P-256 key, ATK verify + CSR signing, saves the certificate)
dotnet run --project src/Hrs.Fiscal.Cli -- onboard --nui <NUI> --fiscal-no <EDI fiscalization no> \
    --branch <unit no> --pos <test POS id> --app <ApplicationId> --dir ./atk-test-profile
# 2. One hotel test receipt
dotnet run --project src/Hrs.Fiscal.Cli -- send --dir ./atk-test-profile
# 3. The full test set -> ./atk-test-profile/report.md (+ signed payloads and QR PNGs under runs/)
dotnet run --project src/Hrs.Fiscal.Cli -- scenarios --dir ./atk-test-profile
```

Scenarios: hotel sale; all VAT letters with split payment; line discount; 4-decimal price and fractional quantity;
return referencing a sale; identical payload resent; same CouponId with new content; tampered signature; return
without reference; the alternative VAT rounding; citizen QR verification. The "observe" scenarios record what ATK
actually does, which answers the open questions on idempotency and rounding.

The test profile keeps the private key as a PEM file. That is for TEST only; production keys live non-exportable in
the Fiscal Client.

## Version

The release version is set once in `Directory.Build.props` (now **1.0.0**, the version submitted for ATK certification); the build adds
the git commit (`1.0.0+<commit>`). It is shown in the menu and on the sign-in page, returned by `/health`, printed in
the footer of every PDF export, and each service start is logged (`SERVICE_STARTED` with version and commit).

## Roles and permissions

Four roles (Cashier, Supervisor, Auditor, Admin) and twelve permissions (`Security/Permissions.cs`). The role → permission
matrix is stored in `fiscal.role_permission` and edited under Settings › Roles & permissions (audited); Admin always
has all permissions. Every page and endpoint is protected by a permission policy, and the menu, dashboard and buttons
are built from the same permissions.

## Key implementation decisions

- **Wire format** is ATK's `models.proto` from `github.com/fiskalizimi/pos-csharp`, the most current reference.
  The Go and PHP repositories use an older proto. A test proves that ATK's published sample payload round-trips
  byte for byte.
- **Signature:** ECDSA P-256 / SHA-256 over the UTF-8 bytes of the **Base64 text**, encoded as **DER**. This
  matches ATK's examples and Go reference; .NET's default P1363 format does not.
- **Money:** item price and line total in €0.0001; coupon totals, tax groups and payments in cents.
- **VAT** is extracted per tax group from VAT-inclusive totals. The default is to round tax half-up.
  `VatRounding.TruncateNet` reproduces ATK's sample numbers. **The official rule is pending ATK's answer.**
- **CouponId** must be unique across the whole business: `branch_id × 10^10 + sequence`.
- **VerificationNo (NUIKF)** is 16 random characters from an alphabet without 0/O/1/I/L.
- **Corrections:** credit folios from OPERA become Return coupons that reference the original. Errors found after
  issue are corrected with a Cancel coupon from the admin interface (reason required, linked to the original, at most
  one per receipt). Nothing is ever changed or deleted.
- **Daily number** ("Kupon fiskal ditor nr."): per workstation and property day, assigned inside the archive's insert
  trigger so concurrent receipts cannot share a number.
- **OFFLINE:** a receipt whose first sending at issue fails is marked OFFLINE (receipt status, copy, response to OPERA)
  and is resent automatically in order. ATK's data model has no field for it.
- **Keys:** one key per workstation, non-exportable (CNG, TPM when available). Only the CSR and public key
  leave the machine. In central mode the keys live on the server (`Signing/ServerKeyStore.cs`; `Signing:KeyStore`
  = `auto`/`cng`, or `file` for test machines only) and workstations are registered from Settings › Workstations.
  A certificate is bound to where it was registered: after a mode change the workstation must be registered again.
- **Archive:** the receipt, transmission, audit and source-payload tables are append-only, enforced by
  triggers and grants. Receipts and the audit log are hash-chained, and `fiscal.verify_chain()` detects
  tampering.

## Open questions (ATK / Oracle)

1. For a cloud PMS whose workstations are browsers: is central signing on the property server, with one key and
   certificate per workstation, acceptable, or must software run on each workstation? (Central signing is the default;
   the workstation client is not built yet.)
2. The official VAT rounding rule.
3. The behaviour of `/pos/coupon` when a CouponId is resent (idempotency).
4. `verification_code` vs `verification_no` in `/ca/signcsr`. Both are sent for now.
5. Prices: the README says €0.0001, Swagger says "cents". The README is followed.
6. How to flag offline receipts (there is no field in the proto).
7. Advance payments (UA Art 6.2) versus the hotel practice of not taking deposits.
8. The retention period (Law on Tax Administration Procedures).
9. A4 folio printed by OPERA instead of 58–100 mm thermal paper (thermal printing by the fiscal server is available either way).
10. Where the developer's identification goes when the developer is a natural person without a NUI.
11. Oracle: the FLIP-to-fiscal-partner interface spec (message format, response, timeout, terminal addressing) and how FLIP connects to OPERA Cloud.
