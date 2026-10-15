# ATK Compliance Review — Opera Cloud Fiscal Solution - Kosovo

Behar Lushaku · 12.10.2026

## Summary

**Updated 14.10.2026: the app side is ready, including printing the receipt on a thermal printer.** Of 40 technical and functional requirements, 30 are met, 6 are partly met, none is missing, 1 depends on Oracle (e-receipt delivery by OPERA) and 3 need a written answer from ATK. Update 15.10.2026: with the Fiscal Client (workstation-client signing), T6 (offline notice at the workstation) is met too: 31 met and 5 partly met in that configuration.

The six missing functions were built and tested on 12.10.2026: daily receipt number, OFFLINE mark, Cancel receipt with reason, entry of paper-block receipts, Albanian/Serbian interface, clock check against ATK. Central signing is now the default, so a fresh installation can fiscalize. The description (5.5) and the user manual (5.6) were updated to match.

What still stands before submission:

1. **The receipt the guest gets.** Since 14.10.2026 the fiscal server can print the whole receipt on each workstation's thermal printer (58/80 mm), as in ATK's samples. Printing the fiscal block on the OPERA folio instead still needs Oracle's FLIP response format and the folio template.
2. **The ATK TEST profile returns 404.** The Commission demo needs a new TEST fiscalization number.
3. **Three written answers from ATK** (central signing for several POS, developer identification, hotel deposits) and, only for properties that keep the A4 folio, whether it is accepted instead of thermal paper.
4. **Signatures:** application form (from ATK) and the declaration, signed last.

## Sources reviewed

Every requirement below is taken from the ATK documents themselves (read in full, the scanned ones through OCR) and checked against the code and the documents as they stand today.

| Source | Date | Parts that apply to this SEF |
| --- | --- | --- |
| Administrative Instruction (MF) No. 01/2026 | 27.04.2026 | Arts 5–7 (who issues, when, receipt content), 31–46 (SEF, certificate and key, offline, paper block, retention), 48 (personal data) |
| ATK *Kërkesat specifike teknike dhe funksionale*, Doc. 01-06-1967 | 29.05.2026 | Part II: Arts 24–28 (SEF functions, technical rules, receipt and QR), 31 (ATK system), Annex F (SEF receipt samples), units list |
| Amendment to Doc. 01-06-1967 | 22.06.2026 | Art 25 installation (one device type is enough); Art 26.4 storage (integrity; unsent data exportable as CSV/PDF on request; local encryption dropped) |
| ATK *Kushtet dhe procedurat për aplikimin, certifikimin dhe mirëmbajtjen e SEF* | 03.07.2026 | Art 5 (application package), 6 (test phase), 7–8 (decision, obligations, withdrawal) |
| ATK API (Swagger v0.9, fiskalizimi reference code) and our ATK TEST run | 08.10.2026 | Data model, signing, onboarding, citizen QR check |
| This app | version 1.0.0, commit of 11.10.2026 | Code, database migrations V001–V012, tests |
| Submission documents and manuals | 09–11.10.2026 | Description (5.5), user manual (5.6), declaration (5.7), application request (5.1), installation and OPERA manuals, certification guide |

## Application package (Art. 5)

All documents ATK asks for exist. The description and the user manual match the app as of 12.10.2026; what remains is ATK's annex form, the ID copy and the signatures.

| Art. | Document ATK asks for | What exists | Status | What to do |
| --- | --- | --- | --- | --- |
| 5.1 | Application form (annex to ATK's document), filled in and signed | *Kërkesë për certifikim të SEF-it* (cover letter with all data) | Get from ATK | Ask ATK for the annex form; copy the data over; fill in personal number and address; sign |
| 5.2 | Copy of valid personal ID (natural person, non-business) | — | Applicant provides | Attach the ID copy |
| 5.3 | ARBK business registration certificate | — | Not applicable | Not needed for a natural person without a business; remove it from the certification guide's document list (§7 still lists it) |
| 5.4 | Fiscal number certificate (consortium) | — | Not applicable | Only if applying with a partner |
| 5.5 | General description: functions, integration with ATK, technologies | *Përshkrimi i përgjithshëm i zgjidhjes softuerike*, Albanian, with architecture diagram | Ready | Updated 12.10.2026: OFFLINE, user-defined VAT rates, cancellation, paper block, daily number, clock check, languages |
| 5.6 | User manual and basic instructions | *Manuali i përdoruesit*, Albanian; installation manual (English) as annex | Ready | Updated 12.10.2026: two-step business setup, VAT rates, Application ID, cancellation, paper block, Albanian screen names, language switch |
| 5.7 | Signed declaration of software compliance | *Deklarata e pajtueshmërisë së softuerit* | Applicant provides | The missing functions are built; fill in personal number and address and sign last, with the version actually submitted |
| 5.4 (procedure) | Submission at ATK's Central Office or electronically | — | Get from ATK | Confirm the electronic channel with ATK |

## Technical and functional requirements

30 of 40 are met (19 at the review on 12.10.2026 morning; 9 more after the build the same day; 2 more with thermal printing on 14.10.2026). *Spec* = Doc. 01-06-1967 (with the 22.06.2026 amendment); *UA* = Administrative Instruction 01/2026.

| # | Area | Requirement (source) | Current state in the app | Status |
| --- | --- | --- | --- | --- |
| T1 | ATK integration | Each receipt signed with the POS private key; SEF identified by an ATK certificate (UA 32.2, 34; Spec 26.2) | ECDSA P-256 over the Base64 text (DER); onboarding with /ca/verify + CSR, or import from ATK's onboarder; tested in ATK TEST 08.10.2026 | Met |
| T2 | ATK integration | HTTPS, structured format per ATK's implementation spec (UA 32.2.4, 37; Spec 25.17, 26.1–26.3) | Protobuf PosCoupon over HTTPS/TLS; money units per ATK | Met |
| T3 | Real time | Data reaches ATK before the receipt is printed (Spec 26.5) | Archived, sent to ATK, then answered to OPERA, which prints the folio after the answer | Met |
| T4 | Real time | Error message to the user for unsent or refused receipts; automatic retry (Spec 26.5) | Automatic resend; refusal reason returned to FLIP and shown on the dashboard. Whether the front desk sees it depends on how OPERA shows FLIP's answer | Partly met |
| T5 | Offline | Keep issuing, store securely, keep numbering in order, send automatically on reconnect, log a technical event (Spec 25.15, 26.13; UA 32.3, 44) | Offline queue with automatic resend in order; QUEUED\_OFFLINE and resend outcomes in the audit log; numbering unaffected | Met |
| T6 | Offline | Visual notice to the user while offline (Spec 26.13) | Since 15.10.2026 with the Fiscal Client: tray icon and an always-on-top window at the workstation while ATK (or the fiscal server) cannot be reached, with the number of waiting receipts and the 48-hour deadline; status from the real sendings and the clock check. Dashboard notice too. In central signing nothing runs on the PCs, so only the dashboard and the OFFLINE line on the folio | Met (client mode); partly met (central) |
| T7 | Offline | Every receipt issued offline marked "OFFLINE" (Spec 26.13, 28.6) | Since 12.10.2026: a receipt whose first sending at issue fails is marked OFFLINE (receipt status, receipt page, copy); the answer to OPERA has `Offline` and OFFLINE in the lines to print. Printing on the folio follows T23 | Met |
| T8 | Offline | 48-hour limit; notify ATK; submit unsent receipts by the 10th of next month (UA 44) | 48-hour overdue alert; since 12.10.2026 also a dashboard reminder for last month's unsent receipts before and after the 10th | Met |
| T9 | Device failure | Register receipts issued from the paper block in SEF within 5 days (Spec 25.16; UA 45) | Since 12.10.2026: Receipts › Enter paper receipt (serial, written time, POS, lines, payment); signed and sent; serial unique; permission-controlled | Met |
| T10 | Receipt types | Return with reference to the original; exchange as return + new sale (Spec 25.9–25.10; UA 43) | Credit folio becomes a Return with ReferenceNo; refused without reference; exchange happens in OPERA as two folios | Met |
| T11 | Receipt types | Cancel receipt with the reason, linked to the original (Spec 25.11, Annex F) | Since 12.10.2026: Cancel receipt on the receipt page; reason required and printed; same lines and amounts, linked to the original; once per receipt | Met |
| T12 | Storage | Unique ID per transaction, write-once, no deletion, only auditable corrections, full history (Spec 25.5.1–5.2) | Append-only tables with triggers forbidding UPDATE/DELETE/TRUNCATE; hash chain; corrections only by Return | Met |
| T13 | Audit | Audit log of all user actions: login, sale, cancellation, configuration change (Spec 25.5.3) | Logins, receipts, refusals, reprints, exports, setting changes with old and new value, registrations, service start | Met |
| T14 | Audit | Logs kept for the legal period and protected by encryption and integrity control (Spec 25.5.3) | Integrity: hash chain and integrity check. Encryption at rest: none in the app | Partly met |
| T15 | Audit | Traces of sent and failed transmissions (date, time to the second, receipt no., total), errors, changes to the SEF (Spec 26.6) | Transmission table per attempt; audit log; version logged at every service start | Met |
| T16 | Exports | Log export in CSV/PDF; unsent data exportable as CSV/PDF on ATK's request (Spec 26.7; amended 26.4) | Receipts, unsent receipts, summary and audit log in CSV and PDF, each export logged with SHA-256 | Met |
| T17 | Storage | Data integrity on failure or sudden stop (amended Spec 26.4) | Archived before sending, in database transactions; daily backup with SHA-256 | Met |
| T18 | Amounts | Euro; unit price with 4 decimals, totals with 2 (Spec 25.4) | Prices in €0.0001, totals in cents; folio validation and independent recalculation before signing | Met |
| T19 | VAT | At least four rates, letters A, C, D, E (A = exempt); no negative VAT turnover (Spec 25.12) | User-defined rates (since 11.10.2026), any capital letter; negative amounts refused | Met |
| T20 | Receipt content | Identical items on one line (Spec 25.18) | Identical lines grouped before signing | Met |
| T21 | Configuration | Units of measure and categories configurable, per ATK's unit list (Spec 25) | Unit per OPERA code with ATK's units list as suggestions (free text allowed, as ATK's note permits); categories with ATK codes | Met |
| T22 | Receipt content | Daily receipt number, "Kupon fiskal ditor nr." (UA 7.2.15; Spec 25.18, Annex F) | Since 12.10.2026: per workstation and property day, assigned in the archive; on the receipt page, copy and the answer to OPERA | Met |
| T23 | Receipt content | QR, NUIKF, SEF ID, receipt type, "e-kupon", RKS logo on the receipt the customer gets (UA 7, 39; Spec 25.18, 28) | Thermal printing (Settings › Printing, since 14.10.2026): the fiscal server prints the whole receipt with all these elements on the workstation's printer. Folio printing remains possible; it needs Oracle's FLIP response format and the folio template (manual written) | Met |
| T24 | Receipt content | Operator name and identification number (UA 7.2.5) | OPERA user with the OPERA cashier ID, e.g. "TEST.USER (2)"; manual receipts: name with user name | Met |
| T25 | Copies | Copies marked "KOPJE E KUPONIT" (Spec 25.18) | Fiscal server PDF copies marked and logged (80 mm layout per Annex F); folio reprints from OPERA are not marked | Partly met |
| T26 | Delivery | Print preview with scannable QR; electronic receipt with customer consent (Spec 25.18, 28.1–28.2) | Folio PDF/e-mail is OPERA's function | Depends on Oracle |
| T27 | Printing | Thermal paper 58–100 mm, legible (Spec 26.14) | Since 14.10.2026: receipts printed by the fiscal server on 58 or 80 mm thermal printers (ESC/POS, network or Windows), per workstation. Printing on the A4 folio instead is a property's choice that still needs ATK's answer | Met |
| T28 | Numbering | NUIKF alphanumeric, one value, max 16 characters (Spec 26.10) | 16 characters from 31 unambiguous letters and digits, generated per receipt | Met |
| T29 | Language | Interface and functions in the official languages, Latin script with special characters (Spec 25.14; UA 32.2.9) | Since 12.10.2026: interface in Albanian (default), Serbian (Latin) and English, switch in the header; receipt texts in Albanian as in ATK's samples | Met |
| T30 | Time | Time synchronised with ATK, to the second (Spec 26.5) | Since 12.10.2026: every 15 minutes against ATK's server time; drift over 30 s shown on the dashboard and logged (CLOCK\_DRIFT / CLOCK\_OK) | Met |
| T31 | Availability | Working 24/7 (Spec 26.5) | Windows service with automatic start and restart | Met |
| T32 | Security | Protection against unauthorised changes to the SEF, incl. digital signature; source code protected after certification (Spec 26.8–26.9) | Data side protected (roles, hash chain, FLIP token, non-exportable keys). Executables are not code-signed and the app does not check its own files | Partly met |
| T33 | Identification | SEF code and developer's NUI/NF built into every fiscalization (Spec 26.16; UA 35.8, 35.11) | ApplicationId sent in every receipt; ATK's data model has no field for the developer's number (natural person, no NUI) | Ask ATK |
| T34 | Integration | Interface with accounting or sales systems (Spec 26.11) | OPERA Cloud via OFIS/FLIP; CSV exports | Met |
| T35 | Installation | Runs on at least one device type (amended Spec 25) | Windows server, browser interface | Met |
| T36 | Environments | Test environment; production not used for tests (Spec 26.15) | Environment setting; Production only for live use; start-over blocked once Production was used | Met |
| T37 | Reports | Reports for ATK's control and verification (Spec 25) | Receipts, unsent receipts, summary per workstation, audit log; search by date, number, NUIKF, folio, cashier, POS | Met |
| T38 | Keys | One private key per installed SEF/POS, kept at a high security level (UA 34.2) | One key per POS, non-exportable. Central signing (default): all keys on the fiscal server. Workstation-client signing: each key in its own PC's key store / TPM via the Fiscal Client; selectable per property and per workstation | Ask ATK |
| T39 | Advance payments | Receipt for every payment before the supply is completed (UA 6.2) | Not handled: hotel deposits are not fiscalized | Ask ATK |
| T40 | Personal data | Apply the personal data protection law (UA 48) | Archived OPERA messages contain guest names; access limited by roles; not described in the documents | Partly met |

## What the Commission will test (Art. 6.2)

After the build, four of the five criteria would pass on the app side; receipt accuracy on the folio itself waits for Oracle.

| Criterion | Would pass now? | What decides it |
| --- | --- | --- |
| Security and integrity of data | Yes | Append-only archive, hash chain, audit log, roles (T12–T17). Code signing (T32) would strengthen it |
| Accuracy of fiscal receipt generation | Yes | Daily number, operator ID and copies done (T22, T24); the whole receipt prints on a thermal printer (T23, T27). The folio fiscal block depends on Oracle |
| Correct, real-time reporting to ATK | Yes, once the TEST profile works | Works in ATK TEST; the current TEST fiscalization number returns 404 |
| Storage and automatic sending after communication returns | Yes | Queue and resend (T5), OFFLINE mark (T7), 48 h and 10th-of-month reminders (T8) |
| Other technical and functional aspects | Yes | Cancel (T11), paper block (T9), languages (T29), clock check (T30) |

The Commission can give 7 working days to fix findings that are not critical (Art. 6.3–6.4). Rehearse the full test on ATK TEST once the new fiscalization number is in, including an offline run, a cancellation and a paper-block entry.

## Documentation against the app

Names, developer and version 1.0.0 match across all documents and the app. The seven mismatches found in the morning were corrected on 12.10.2026; only the declaration's signature is left.

| Document | Was | Now |
| --- | --- | --- |
| Description 5.5, §6; User manual §2 | Folio printed OFFLINE, not implemented | Implemented (T7); text says the folio must print the OFFLINE line from the answer |
| Description 5.5, §5; User manual §5 item 4 | Fixed VAT rates A/C/D/E | Rates start empty, added with ATK's letters; one rate per percentage matches OPERA |
| User manual §5 item 1 | NUI and unit cannot change | Two-step setup with Validate with ATK; taxpayer change; Start over |
| User manual §5 item 2 | Application ID under General | On Business & unit |
| User manual (all screens) | English screen names | Albanian screen names; language switch described |
| Certification guide §7 | ARBK certificate listed | Removed; gaps table updated |
| Declaration 5.7, point 2 | Full compliance stated while 6 functions were missing | Functions built; sign last with the submitted version |

The installation manual (English) and the OPERA manuals are current as of 11.10.2026. They are fine as technical annexes; ATK accepts free format, but an Albanian summary helps the Commission.

## Action list

Do the first two groups before applying. Most of the app work needs no outside input; the folio block waits for Oracle.

**Build before applying (developer)**

- [x] Daily receipt number per POS, archived with the receipt, returned to OPERA and on the copy PDF (T22)
- [x] OFFLINE: set when ATK is unreachable, offline flag to OPERA, printed on copies (T7)
- [x] Cancel receipt with a reason, linked to the original, from the admin interface (T11)
- [x] Entry of paper-block receipts (serial no., date, lines) that are then signed and sent (T9)
- [x] Albanian interface and receipt texts; Serbian (Latin) as a second language (T29)
- [x] Clock check against ATK's server time, with an alert when it drifts (T30)
- [x] Reminder before the 10th of the month for receipts still unsent (T8)
- [x] Default signing mode set to central (server); the Fiscal Client (key on each workstation) is built and selectable if ATK requires it
- [x] Operator identification number next to the name (T24); copy PDF with RKS logo and total without VAT
- [x] Offer ATK's units list as choices for the unit (T21)
- [x] Print the whole receipt on a 58/80 mm thermal printer per workstation, as an alternative to the folio (T23, T27)

**Get from outside**

- [ ] Oracle: FLIP response format and folio fields, so QR, NUIKF, SEF ID, daily no., OFFLINE, e-kupon and RKS logo print on the folio (T23, T25, T26)
- [ ] Hotel/EDI: a new TEST fiscalization number for the Commission demo
- [ ] ATK: the annex application form and the electronic submission channel
- [ ] ATK, in writing: central signing for several POS (T38); A4 folio instead of thermal paper (T27); where the developer's identification goes (T33); hotel deposits (T39); OFFLINE marking on an OPERA folio; VAT rounding rule; no de-duplication of resent receipts

**Documents (after the app work)**

- [x] Description 5.5: OFFLINE, VAT rates, new functions
- [x] User manual 5.6: business setup, VAT rates, Application ID, Albanian screen names, cancellation, paper block
- [x] Certification guide: remove ARBK from the document list
- [ ] Declaration 5.7: sign last, with the final version number

**Strengthen (not required to apply)**

- [ ] Code-sign the executables and publish the release hashes; check them at start-up (T32)
- [ ] Recommend disk encryption (BitLocker) for the database server in the installation manual (T14)
- [ ] Add a personal-data paragraph to the description (T40)
