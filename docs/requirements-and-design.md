# HRS Fiscal Solution — Kosovo SEF Requirements, ATK API & Architecture

Sources:
- UA MF Nr. 01/2026 (Administrative Instruction), Ch. IV Arts 31–47, Arts 5–7, 49–50
- ATK "Kërkesat specifike teknike dhe funksionale" Doc. 01-06-1967, 29.05.2026 (Part II, Arts 22–31)
- ATK amendment to Doc. 01-06-1967, signed 22.06.2026 (in force on signing)
- ATK reference integrations: github.com/fiskalizimi (pos-csharp is the most current, last commit 24.07.2026; pos-golang/pos-php protos are outdated)
- Swagger v0.9: https://fiskalizimi-test.atk-ks.org/swagger/index.html

---

## 1. Legal / functional requirements (summary)

- **Scope:** B2C retail and services to the public. Pure-B2B and bank-only public bodies exempt. Receipt issued at payment, for every advance payment, and at supply when payment is deferred.
- **Certification:** The developer is registered and authorized by the ATK Commission (15 working days). The SEF gets an ApplicationId. Every legally driven update is re-tested and re-certified. A separate test environment exists, and production must not be used for testing. Certification can be revoked.
- **Real time:** Receipt data reaches ATK before printing, over HTTPS, signed. Errors are shown to the user and retried automatically. Available 24/7, with clock synced to ATK.
- **Offline:** Keep issuing receipts, show a visible offline indicator, mark receipts "OFFLINE", keep numbering intact, and auto-send when the connection returns. Fiscalized correctly if sent within 48h.
  - Outage over 48h: notify ATK. If still unsent by the 10th of the next month, submit them anyway.
  - Device dead: use the paper block and enter those receipts into SEF within 5 days.
- **Integrity:** Write-once storage with no deletion. Corrections are new linked transactions. Full audit log (logins, sales, cancellations, config changes), exportable as CSV/PDF.
- **Receipt types:** Sale, Cancel (with reason, linked to the original), Return (linked to the original). Exchange is a return plus a new sale. Reprints are marked "KOPJE E KUPONIT".
- **Receipt content:**
  - NUI, VAT number, unit address and unit number, operator
  - Items grouped per line, with unit price to 4 decimals and totals to 2
  - VAT A/C/D/E, payment split
  - SEF ID `[Unit]-[NUI]-[PosId]`, NUIKF (max 16 characters), QR, "e-kupon" text, RKS logo
  - Albanian and Serbian, Latin script
- **Transition:** Taxpayers without a PEF/SF device install SEF within 60 days. Migration of existing devices follows a separate Minister's decision.

### Amendment of 22.06.2026
1. **Art 25, Installation:** SEF needs to run on **at least one** device or environment (PC, laptop, tablet, phone or another suitable device). It no longer has to run on all of them.
2. **Art 26.4, Storage:** The standard is now **data integrity** (previously "no loss"). The mandatory encryption of local data is **removed**. Data not yet transmitted must be **exportable as CSV/PDF**, only at the request of ATK or another state authority.

---

## 2. Technical integration (ATK reference implementation)

### Environments
| | TEST | PROD |
|---|---|---|
| Base URL | https://fiskalizimi-test.atk-ks.org | https://fiskalizimi.atk-ks.org |

### Endpoints (Swagger v0.9, all JSON)
| Endpoint | Purpose | Request | Success response |
|---|---|---|---|
| `POST /ca/verify/{nui}` | Onboarding step 1 | `fiscalization_no` (from EDI), `pos_id`, `branch_id`, `application_id` | `business_name`, `verification_code` (404 if the POS is already registered or has no ApplicationId) |
| `POST /ca/signcsr` | Onboarding step 2: the ATK CA signs the CSR | `business_name`, `business_id`, `branch_id`, `verification_code`/`verification_no`, `pos_id`, `application_id`, `csr` (PEM) | `signed_certificate` (PEM) |
| `POST /pos/coupon` | Fiscalize a receipt | `details` (Base64 protobuf PosCoupon), `signature` (Base64) | `message`, `transaction_id` (uint64). Errors: 400/500 `{error}` |
| `POST /citizen/coupon` | Citizen app QR verification (not used by POS) | `citizen_id`, `qr_code` | coupon object |

### Onboarding / PKI (one key pair per POS)
- Key: **ECDSA P-256**, generated **on the POS machine**. ATK's guidance: "the private key must never leave the machine on which it was generated".
- CSR subject:
  - C = RKS (or XK)
  - O = BusinessId (NUI)
  - OU = PosId
  - L = BranchId
  - CN = business name
- Inputs needed: NUI, Fiscalization No. (from EDI), PosId (numeric, unique per branch), BranchId, ApplicationId (from ATK certification).
- ATK provides an onboarder GUI tool (Windows, macOS, Linux; `-env=TEST|PROD`) that does key, CSR and signing and can export `private-key.pem` and `signed-certificate.pem`.

### Data model (protobuf `package atk`, from pos-csharp/models.proto)
- `CouponType`: 0 Unknown, 1 Sale, 2 Cancel, 3 Return
- `PaymentType`: 1 Cash, 2 CreditCard, 3 Voucher, 4 Cheque, 5 CryptoCurrency, 6 Other
- `PosCoupon` fields:
  - Ids: BusinessId, CouponId (unique across the **whole business**), BranchId, PosId, ApplicationId
  - Location, OperatorId
  - VerificationNo (≤16 characters, **generated by the POS**)
  - Type, Time (Unix seconds)
  - Items[] {Name, Price, Unit, Quantity (float), Total, TaxRate, Type}
  - Payments[] {Type, Amount}
  - Total, TaxGroups[] {TaxRate, TotalForTax (net), TotalTax}, TotalTax, TotalNoTax, TotalDiscount
  - ReferenceNo (original CouponId, required for Cancel/Return), TransactionNo
- `CitizenCoupon` (the QR content): BusinessId, CouponId, BranchId, PosId, VerificationNo, Type, Time, Total, TaxGroups, TotalTax, TotalNoTax. It **must match** the PosCoupon, otherwise the receipt is flagged "FAILED VERIFICATION".
- Money:
  - Item prices are integers in **€0.0001** (€1.00 = 10000)
  - Totals are integers in **€0.01** (€1.00 = 100)
  - The example item `Total` values also use 4 decimals

### Signing
1. Serialize the protobuf to bytes and **Base64-encode** them.
2. Take the UTF-8 bytes of the Base64 string, hash with SHA-256, and sign with ECDSA P-256. Then Base64-encode the signature.
   - The bytes signed are the **Base64 text**, not the raw protobuf.
3. POS: send `{details, signature}`. QR: `"<base64 CitizenCoupon>|<base64 signature>"`.
- Signature encoding: the Go sample uses ASN.1/DER, and the example signatures (`MEQCI…`) are DER. The .NET sample's `SignHash` returns raw r‖s (P1363), so use **DER** to be safe.
- The PHP sample passes an already-computed hash into `openssl_sign(..., SHA256)`, which hashes it again. Don't copy it.

---

## 3. Legal requirements vs the API: gaps to clarify with ATK

| # | Requirement | API status | Question for ATK |
|---|---|---|---|
| 1 | Receipts issued offline marked "OFFLINE" and sent within 48h | No offline flag in the model and no batch endpoint | Is a late `Time` enough? Is replay ordering or rate-limiting enforced? |
| 2 | Idempotency on retry | Not documented | What happens if a CouponId is resent (timeout after the server already accepted it)? Duplicate error or the same `transaction_id`? |
| 3 | Advance payment receipts (UA Art 6) | No coupon type | Sale with a special item Type? How do we offset the advance on the final receipt? |
| 4 | Reprint "KOPJE" | Nothing to send | Confirm it is print-only. |
| 5 | Unit number / SEF ID `[Unit]-[NUI]-[PosId]` | Only BranchId | Is BranchId the "unit number" from EDI? |
| 6 | VAT number, operator ID, per-line discount | OperatorId is free text. No VAT number, no line discount | Should line discounts be reflected in Price/Total? |
| 7 | Payment methods: bank, SMS | Not in the enum | Map them to Other? |
| 8 | Unit price 4 decimals | Swagger says "all prices are in cents" | Confirm the readme (€0.0001) is authoritative. |
| 9 | Rounding / VAT calculation | Examples suggest VAT-inclusive prices with the net base truncated | Ask for the official rounding rule (per line or per group). |
| 10 | Private key never leaves the POS | Conflicts with cloud/central SEF and with OPERA Cloud folio printing | Can one certified central service hold keys for several logical POS? |
| 11 | Field name `verification_code` vs `verification_no` | Readme and Swagger differ | Which one does `/ca/signcsr` expect? |
| 12 | Status or lookup endpoint | None | How do we reconcile? Is there a daily report or Z-report? |
| 13 | Cancel reason (TR Art 25) | No field | Not transmitted? |

## 4. Business rules confirmed by the user (08.10.2026)
- Deposits and cancellations are not used in Kosovo. Corrections are made with Return (kthim) receipts that reference the original.
  - Note: UA Art 6.2 still says a fiscal receipt is required "for every payment made before the supply is completed". Confirm how ATK treats hotel advance payments.
- All receipts (fiscal coupons) and logs must be kept (UA Art 46, retention per the Law on Tax Administration Procedures; period to be confirmed).

## 5. Solution: local Fiscal Agent for OPERA Cloud via OFIS
**OPERA side (OFIS):**
- OPERA Cloud sends fiscal payloads (Folio Generation / Post Payment / Check Out) to the partner endpoint configured under Fiscal Management > OFIS Cloud Configuration.
- That configuration holds the endpoint URL, auth type and retries (3 × 5000 ms by default), and requires an outbound allowlist.
- Optional "Send Folio after Fiscal Payload" sends a folio copy for archiving.

**Agent modules:**
1. **OFIS listener:** HTTPS endpoint that receives the payload, validates it, and replies with the fiscal data (VerificationNo, TransactionNo, QR string) for OPERA to print on the folio.
2. **Mapping:**
   - Transaction codes become items: name, unit, VAT letter A/C/D/E, category (HT/SUA/UR)
   - OPERA payment methods become PaymentType
   - Group identical lines, apply rounding, and check that tax groups and totals reconcile
3. **Fiscal core:**
   - CouponId sequence unique across the business, and a VerificationNo generator
   - ECDSA signing (DER) with the terminal key, and the CitizenCoupon/QR
   - POST /pos/coupon with retry and idempotency
4. **Offline queue:** persistent; resends automatically; watchdog alerts at 48h and at the 10th of the month; visible offline status.
5. **Archive (append-only):**
   - Stores the original OPERA payload, the protobuf, the signature, the ATK response, the QR and the folio copy
   - Hash-chained records, no deletes, backups
6. **Audit log:** logins, sends and failures, configuration changes, exports; CSV/PDF export (amendment of 22.06.2026).
7. **Admin UI:** status and queue, receipt search, "KOPJE E KUPONIT" reprint, daily/periodic reports, role-based access (RBAC).
8. **Onboarding & PKI:** key generation on the host, CSR, certificate store, expiry monitoring.
9. **Release management:** ApplicationId and versioning. Every legally driven change is re-certified by the ATK Commission.

**Open items:**
- The OFIS payload schema and response contract come from Oracle's fiscal-partner documentation; partner access is needed.
- OFIS Cloud calls the endpoint from OCI, so the local agent needs public HTTPS ingress (static IP or DNS, TLS, firewall restricted to OCI) or a relay.
- PosId and key model: one key per agent host versus one per OPERA cashier/terminal; confirm with ATK, given ATK's guidance that the private key must never leave the machine.
- Deposit handling (see section 4) and the retention period.

## 6. HRS Fiscal Solution: server + workstation clients (revised 08.10.2026)
ATK requirement (stated by the user): every workstation that can generate an invoice must have the SEF installed.

**HRS Fiscal Server** (one per property):
- OFIS endpoint for OPERA Cloud
- Mapping (transaction codes, VAT, payment methods)
- Receipt numbering: CouponId unique across the business, VerificationNo
- Central archive (append-only, hash-chained) and audit log
- Offline queue, 48h and 10th-of-month alerts, CSV/PDF export
- Admin UI and reports
- Registry of clients, PosIds and certificates

**HRS Fiscal Client** (on every OPERA cashier workstation):
- Holds its own PosId, private key (non-exportable) and ATK certificate
- Signs the PosCoupon and the CitizenCoupon/QR
- Sends to ATK, with a fallback to the server queue
- Shows the online/offline indicator
- Can register itself (CSR) with ATK

**Flow:**
1. A folio is generated in OPERA at Fiscal Terminal X.
2. OFIS sends the payload, including the terminal ID, to the Server.
3. The Server maps the payload, assigns the receipt numbers and routes the request to Client X.
4. Client X signs and transmits to ATK, then returns the signature, QR and TransactionNo.
5. The Server archives everything and replies to OPERA. The fiscal data prints on the folio.
- OPERA Fiscal Terminals (Terminal ID, one per workstation) map 1:1 to the PosIds of the HRS clients.

**Signing mode is selectable (09.10.2026).** Settings › General sets the property default; each workstation can
override it. In both modes every workstation is its own ATK POS with its own key and certificate:
- *Workstation client* (default): the flow above.
- *Central*: the server holds one non-exportable key per workstation (Windows key store / TPM) and signs on its
  behalf in step 4. Workstations are registered with ATK from Settings › Workstations. Nothing is installed on the PCs,
  and a switched-off PC no longer blocks its folios.
A certificate is bound to where it was registered; after a mode change the workstation must be registered again.

**Clarify with ATK:**
- For a cloud PMS whose workstations are browsers: is central signing with one key and certificate per workstation
  acceptable, or must software run on each workstation?
- Must the client also transmit, or may the server transmit coupons the client has signed?
- OPERA prompts the user to pick the Fiscal Terminal unless one is marked Primary: is the POS identified by the
  terminal the user selects acceptable?

## 7. OPERA integration options (if OFIS isn't available for Kosovo)
OFIS needs Oracle to enable a fiscal partner (and in practice a country localization). The payload spec is only available to partners. Fallback options:
- **A. OHIP event-driven:**
  - HRS Server consumes OPERA business events (polling `/int/v1/externalSystem/{code}/hotels/{hotelId}/businessEvents`, or the OHIP Streaming API), e.g. payment posted / folio / checkout.
  - It then fetches the folio and transactions via the OHIP Cashiering REST API and routes the receipt to the cashier's workstation client.
  - The client fiscalizes and prints a separate fiscal coupon (thermal printer) with the QR.
- **B. Workstation-initiated:** the cashier clicks "Fiscalize" in the HRS Client for a reservation or folio. The client pulls the folio via OHIP, fiscalizes and prints. The workstation is always known; one extra click.
- **C. Virtual printer:** the HRS Client installs as a printer, captures the folio PDF that OPERA prints, parses it, fiscalizes and prints. No Oracle dependency, but the parsing is fragile.
- **Recommendation: A + B combined.** Events fill a "pending fiscalization" list on the client; the cashier confirms with one click and the coupon prints. OFIS stays a later plug-in.
- The fiscal core is unaffected: the OPERA connector is a replaceable module.
- To verify in OHIP: which business events exist for folio generation and payments, whether they carry cashier/workstation info, and whether a fiscal number can be written back to OPERA.

## 8. Decision (08.10.2026): OPERA integration via OFIS, not OHIP
Section 7 options are dropped. OFIS Cloud is the only OPERA connector.

Prerequisites:
- Oracle fiscal-partner access for Kosovo, plus the OFIS payload schema and response contract
- OPERA Cloud test environment with:
  - OFIS Cloud configuration (endpoint URL, auth type)
  - Fiscal Partner and Fiscal Folio Parameters
  - one Fiscal Terminal per workstation
  - outbound allowlist entry for our host
- Public HTTPS endpoint for the HRS Fiscal Server (DNS, TLS certificate, firewall allowing OCI), or a relay
- Sample payloads: Folio Generation, Post Payment, Check Out

## 9. Decision (08.10.2026): OFIS with on-premise FLIP (supersedes the OFIS Cloud assumptions in sections 5 and 8)
- Path: OPERA Cloud → OFIS → **FLIP** (Oracle Fiscal Layer Integration Platform, installed at the hotel) → HRS Fiscal Server over the LAN (local IP:port).
- OPERA configuration:
  - Fiscal Folio parameter **FLIP Server Address**
  - Fiscal Partner and payload types (Folio Generation / Post Payment / Check Out)
  - **Fiscal Terminals** (Terminal ID + LAN Address/Port per workstation)
- No public endpoint, domain or port forwarding is needed for the HRS components. The HRS Server still needs outbound HTTPS to ATK (fiskalizimi.atk-ks.org).
- Still needed from Oracle (the FLIP fiscal-partner interface spec):
  - message format and transport
  - expected response fields printed on the folio
  - sync/async behaviour and timeout
  - whether FLIP calls one partner address or each Fiscal Terminal address
  - how FLIP connects to OPERA Cloud

## 10. First ATK TEST run (08.10.2026), full report in repo docs/atk-test-results-2026-10-08.md
- Test business MARIGONA TOWER SH.P.K (NUI 811159898), unit 1, ApplicationId 356526644730.
- POS 1 was registered with ATK's onboarder; POS 901 with hrs-fiscal-cli, so HRS onboarding works.
- Sales, multi-VAT, discount, 4-decimal price and return were accepted. A tampered signature and a return without a reference were rejected. The citizen QR verification returned our exact totals.
- ATK does NOT de-duplicate: a resent payload and a reused CouponId were both accepted. HRS must guarantee uniqueness and avoid blind resends.
- ATK does not check VAT rounding on intake. `/citizen/coupon` needs citizen_id as a number.
