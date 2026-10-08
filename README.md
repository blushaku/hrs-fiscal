# HRS Fiscal Solution

Kosovo fiscalization software (**SEF – Softuer Elektronik Fiskal**) for hotels on **Oracle OPERA Cloud**.
OPERA Cloud sends fiscal payloads via **OFIS with FLIP** (Oracle's Fiscal Layer Integration Platform, installed on-premise). FLIP delivers them over the hotel LAN to the HRS Fiscal Server. The server routes each receipt to the
HRS Fiscal Client on the workstation that issued it. The client signs the receipt and fiscalizes it with ATK.
Everything is archived in an append-only PostgreSQL store.

```
OPERA Cloud ─OFIS─▶ FLIP (on-prem) ──LAN──▶ HRS Fiscal Server ──route by Fiscal Terminal ID──▶ HRS Fiscal Client (each workstation)
   ▲                  │ mapping, numbering                                  │ own PosId + non-exportable key
   │                  │ archive + audit log (PostgreSQL)                    │ signs PosCoupon + QR
   └── fiscal data ◀──┘ offline queue, admin UI, exports   ◀── result ─────┘──▶ ATK /pos/coupon
```

## Repository layout

| Path | What |
|---|---|
| `src/Hrs.Fiscal.Core` | Shared fiscal library: ATK protobuf model, coupon builder and validation, VAT, ECDSA signing (DER), QR, CSR, ATK API client |
| `src/Hrs.Fiscal.Server` | Property server (ASP.NET Core, Windows service): **admin web UI** (receipts, audit log, exports, settings), archive, migrations. The FLIP endpoint, routing and offline queue are still to come |
| `src/Hrs.Fiscal.Client` | Workstation client (Windows service): key and certificate, onboarding, signing, transmission. **Skeleton** |
| `tests/Hrs.Fiscal.Core.Tests` | Unit tests (xUnit) |
| `tests/Hrs.Fiscal.Server.Tests` | Integration tests against PostgreSQL (set `HRS_TEST_PG`) |
| `db/migrations` | PostgreSQL schema |
| `db/tests` | Schema self-test (immutability, hash chain, tamper detection) |
| `docs/` | Requirements and design notes |

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

## Admin web UI (HRS Fiscal Server)

| Page | What it does | Roles |
|---|---|---|
| Dashboard | Today's receipts, accepted, waiting to send (with the 48 h deadline), rejected; workstation status; certificate expiry; alerts | all |
| Receipts | Search by receipt no., NUIKF, folio or cashier; filter by date, workstation, type and status; paging | all |
| Receipt detail | Fiscal data, QR, every send attempt to ATK, items and VAT, linked returns, original OPERA/FLIP message, signed payload | all |
| Download copy (PDF) | Receipt copy marked "KOPJE E KUPONIT", logged as REPRINT | Cashier, Supervisor, Admin |
| Audit log | Search all events; **integrity check** of both hash chains (logged) | all |
| Export | Receipts, unsent receipts or audit log as CSV (UTF-8) or PDF; every export logged with a SHA-256 | Supervisor, Admin, Auditor |
| Settings | General (retention, backup, ATK environment and ApplicationId, timeouts, VAT rounding); business and unit; workstations (POS ID ↔ OPERA Fiscal Terminal); OPERA transaction code and payment mapping; VAT rates; users | Admin |

Every settings change is written to the audit log with its old and new values. Failed logins are logged, and an
account is blocked for 15 minutes after 5 failures. Users and workstations are deactivated, never deleted.

No fiscal printer is involved. OPERA prints the fiscal data (SEF ID, receipt no., NUIKF, ATK transaction, QR,
"e-kupon") on the folio from the FLIP response. The OPERA folio template needs a fiscal block for this.

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

### Licences
- QuestPDF (PDF exports) is used under its **Community licence**: free for companies under USD 1M annual revenue.
  Confirm HRS's eligibility; otherwise buy a Professional licence or swap the PDF library.
- QRCoder, Dapper, Npgsql, Google.Protobuf: MIT / Apache 2.0 / BSD.

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
- **Corrections** are made only with Return coupons that reference the original. Cancel coupons are not used
  in Kosovo.
- **Keys:** one key per workstation, non-exportable (CNG, TPM when available). Only the CSR and public key
  leave the machine.
- **Archive:** the receipt, transmission, audit and source-payload tables are append-only, enforced by
  triggers and grants. Receipts and the audit log are hash-chained, and `fiscal.verify_chain()` detects
  tampering.

## Open questions (ATK / Oracle)

1. Is a signing client on each workstation enough, with central numbering and archive, and may the server
   transmit coupons that a client has signed?
2. The official VAT rounding rule.
3. The behaviour of `/pos/coupon` when a CouponId is resent (idempotency).
4. `verification_code` vs `verification_no` in `/ca/signcsr`. Both are sent for now.
5. Prices: the README says €0.0001, Swagger says "cents". The README is followed.
6. How to flag offline receipts (there is no field in the proto).
7. Advance payments (UA Art 6.2) versus the hotel practice of not taking deposits.
8. The retention period (Law on Tax Administration Procedures).
9. Oracle: the FLIP-to-fiscal-partner interface spec (message format, response, timeout, terminal addressing) and how FLIP connects to OPERA Cloud.
