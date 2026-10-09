# Opera Cloud Fiscal Solution - Kosovo — Guide to ATK certification of the SEF

**Source:** ATK, *Kushtet dhe procedurat për aplikimin, certifikimin dhe mirëmbajtjen e Softuerëve Elektronikë Fiskalë
(SEF)* (Conditions and procedures for application, certification and maintenance of Electronic Fiscal Software), signed by
the ATK Director General on 03.07.2026, in force from signing. 7 pages, Articles 1–9.
**Prepared:** 09.10.2026 for Opera Cloud Fiscal Solution - Kosovo (OPERA Cloud via OFIS/FLIP).

---

## Glossary

- **OFIS — OPERA Fiscal Integration Solution.** Oracle's fiscal framework inside OPERA Cloud. At each fiscal event (folio
  settlement, check-out, payment) OPERA builds a standard payload with the whole folio — lines, taxes, payments, totals,
  hotel and terminal — and sends it to the configured fiscal partner. Configured in OPERA under Administration ›
  Financials › Fiscal Management. OFIS does not fiscalize; it only delivers the folio.
- **FLIP — Fiscal Layer Integration Platform.** Oracle software installed on the hotel's network. It receives the OFIS
  payload from OPERA Cloud, calls the local fiscal software (our server, over the LAN, with an access token) and returns
  the answer to OPERA. It is the bridge between OPERA in the cloud and the fiscal server in the hotel.
- **SEF** — Softuer Elektronik Fiskal: the certified fiscal software; here, Opera Cloud Fiscal Solution - Kosovo.

## 1. In one paragraph

HRS, as the **developer and maintainer** of HRS Fiscal, must be **registered and certified by the ATK SEF Certification
Commission before offering the software to any taxpayer** (Art. 5.3). We submit a written application with seven
documents, physically at ATK's Central Office or electronically. The Commission checks the file, **tests the software**,
and decides within **15 working days**. If approved, HRS gets a **certificate** and the software gets an **SEF
identification number**; HRS Fiscal is then listed in ATK's public register of certified SEF. The certificate stays
valid until revoked, but **every functional or technical change affecting how fiscal data is generated, stored or
transmitted must be notified to ATK in advance** (Art. 7.4).

## 2. Legal basis

| Act | What it says for us |
|---|---|
| Law 08/L-257 on Tax Administration and Procedures, Art. 3 (1.29, 1.30, 1.33) | Defines "person": natural person (with or without a registered business) and legal person |
| Administrative Instruction (MF) 01/2026 of 27.04.2026, Art. 35.5 | Conditions and procedures for SEF certification and maintenance are set by a special ATK Commission |
| ATK Director General Decision 01-06-1670 of 04.05.2026 | Establishes the Commission that drafted this document |
| This document (03.07.2026) | Application, testing, decision, register, suspension/withdrawal/annulment |
| ATK technical and functional requirements, Doc. 01-06-1967 (29.05.2026) + amendment 22.06.2026 | The specification the software is tested against (see *sef-requirements-and-api.md*) |

**Definitions to use in our documents (Art. 3):**
- **SEF** — software for automated recording and reporting of taxpayers' financial transactions; registers retail
  supplies of goods/services, **generates the fiscal receipt and reports the data in real time** to ATK's system.
- **Kuponi fiskal** — the document recording a supply to the final consumer; may be printed or sent electronically;
  **must carry the fiscal logo and the signature as a QR code.**
- **Refuzim** (refusal), **Tërheqje** (temporary withdrawal of validity until the cause is fixed), **Anulim**
  (cancellation — all rights end), **Pezullim** (suspension of the review or of validity until conditions are met).

## 3. The application package (Art. 5.5)

| # | Document (Albanian) | What it is | What HRS submits | Status |
|---|---|---|---|---|
| 5.1 | *Kërkesa për certifikim të SEF-it* | ATK's standard application form (annex to the document), completed and signed | Form signed by HRS's authorised representative | **Form missing** — the annex is not in our PDF; get it from ATK |
| 5.2 | Personal ID copy | Applicant is a natural person without a registered business | Copy of the applicant's valid ID card or passport | **Applies** — the applicant applies as a natural person |
| 5.3 | *Certifikata e Regjistrimit të Biznesit* (ARBK) | Business registration certificate, for a business person or legal entity | — | Not applicable (natural person) |
| 5.4 | *Certifikata e Numrit Fiskal* | Only for a consortium | — | Only if HRS applies together with a partner |
| 5.5 | *Përshkrimi i përgjithshëm i zgjidhjes softuerike* | General description: functionality, **how it integrates with ATK's system**, list of technologies (free format) | Technical description (see §6) | To write — in Albanian |
| 5.6 | *Manuali i përdoruesit dhe udhëzimet bazë* | User manual and basic instructions (free format) | Hotel-user manual + installation manual | We have installation and OPERA manuals in English; a **front-desk/admin user manual in Albanian** is needed |
| 5.7 | *Deklarata e pajtueshmërisë së softuerit* | Signed declaration that the software complies with fiscal law and ATK's specification | Declaration signed by HRS's authorised representative | To prepare (template from ATK if they have one) |

**Where:** physically at the **ATK Central Office (Zyra Qendrore), Prishtina**, or electronically with the documents
attached (Art. 5.4). Ask ATK which electronic channel they accept (email address or EDI).

**ATK can ask for more at any time** (Art. 5.9): documents, information, evidence, guarantees or extra conditions for
public interest, legal compliance, or security and integrity of the fiscal system. Not delivering them in time can
suspend or reject the application, or — after certification — suspend, withdraw or cancel the certificate (Art. 5.10).
This is the basis on which ATK may ask for **source code**: the document does not list it in the package, but have a
tagged, buildable release and its source ready to hand over.

## 4. Procedure and deadlines

```
 Submit package ──► Completeness check ──► Technical testing ──► Decision ──► Certificate + SEF ID ──► Public register
                    │ incomplete:           │ non-critical gaps:   │ refused:
                    │ written notice,       │ 7 working days to    │ written reasons;
                    │ 7 working days to     │ fix, then we notify  │ re-apply after 30
                    │ complete, else        │ and ATK re-tests     │ calendar days, causes
                    │ WITHDRAWN (Art 5.6–8) │ (Art 6.3–6.5)        │ removed (Art 7.5–7.6)
```

| Step | Rule | Article |
|---|---|---|
| Missing or unclear documents | Commission notifies in writing; **7 working days** to complete, otherwise the application is **considered withdrawn** | 5.6–5.8 |
| Technical testing | Commission assesses compliance (criteria in §5) | 6.2 |
| Deficiencies found in testing | If not critical to fiscal function: **7 working days** to correct, then notify the Commission and allow re-testing | 6.3–6.5 |
| Decision | Certify / not certify within **15 working days from receipt of the request** | 7.1 |
| Approval | Certificate issued; the software gets its **SEF identification number**; valid **until revoked** | 7.2–7.3 |
| Refusal | Written reasons; **re-apply only after 30 calendar days** and only with the causes removed | 7.5–7.6 |
| Register | ATK keeps and **publishes** the register: developer/maintainer name, SEF name, SEF ID number, certification date, certificate status | 7.7–7.9 |
| Appeal | Against a Commission decision, under ATK's appeal procedures | 8.3 |

The deadlines are short and run from ATK's notice: keep one person and a test environment on standby for the whole
review.

## 5. What the Commission tests (Art. 6.2) — and where HRS Fiscal stands

| Test criterion | HRS Fiscal today | Evidence to show | Status |
|---|---|---|---|
| **Compliance with ATK's technical and functional specification** | Protobuf PosCoupon/CitizenCoupon, ECDSA P-256 signing, onboarding (verify + CSR), receipt types Sale/Return | ATK TEST run 08.10.2026: 11 scenarios incl. tampered signature and missing reference correctly rejected | ✅ core; ⚠ open spec questions (§7) |
| **Data security and integrity** | Append-only PostgreSQL archive, hash chains, no update/delete triggers, integrity check, audit log of every login/change/export, roles & permissions, non-exportable keys (central mode), FLIP token | Audit › Integrity check; database triggers; audit log export | ✅ |
| **Accuracy of fiscal receipt generation** | Folio validation (property, tax number, line/VAT/total arithmetic), independent recalculation before signing, VAT groups, 4-decimal prices, QR verified by ATK's citizen endpoint | Refused-folio examples; CouponVerifier tests | ✅ receipt data; ⚠ **printing on the OPERA folio** (QR, NUIKF, e-kupon, RKS logo) depends on Oracle's FLIP response mapping |
| **Correct, real-time reporting to ATK** | Live mode: OPERA → FLIP → HRS → ATK within the settlement, ATK transaction stored | Live run with an OPERA test property | ✅ in live mode (demo needs a working TEST profile) |
| **Automatic storage and transmission after communication is restored** | Offline queue, automatic resend every *n* minutes, 48 h deadline shown on the dashboard, alerts | Stop network → settle folio → restore → receipt sent | ✅ queue; ⚠ "OFFLINE" mark on the printed folio still to do |
| **Other technical/functional aspects** | Copy marked KOPJE E KUPONIT, CSV/PDF export, version shown, workstation per POS | — | ⚠ see gaps |

## 6. What to put in the technical description (5.5)

Write it in Albanian (English annex if helpful). Suggested chapters, all material already exists in our docs:
1. **Product and developer** — Opera Cloud Fiscal Solution - Kosovo, version, developer Behar Lushaku, contact for the Commission.
2. **Purpose and scope** — fiscalization of hotel folios from Oracle OPERA Cloud (rooms, F&B and other services) for
   B2C sales in Kosovo.
3. **Architecture** — OPERA Cloud → OFIS → on-premise FLIP → fiscal server (hotel LAN) → ATK; workstation = ATK POS; explain OFIS and FLIP (glossary below);
   diagram.
4. **Integration with ATK** — environments, onboarding (`/ca/verify`, `/ca/signcsr`, CSR subject), `/pos/coupon`,
   protobuf, signing over the Base64 text, QR = CitizenCoupon | signature, money units.
5. **Receipt rules** — Sale, Return with reference; numbering (CouponId unique per business, NUIKF); VAT letters;
   payment types; mapping from OPERA.
6. **Validation** — property and tax number checks, arithmetic checks, recalculation before signing.
7. **Offline operation** — queue, resend, 48 h / 10th-of-month alerts, duplicate protection.
8. **Security and integrity** — append-only archive, hash chains, audit log, roles, key storage (Windows key store /
   TPM, non-exportable), FLIP authentication, backups.
9. **Retention and exports** — nothing deleted; CSV/PDF exports with SHA-256; KOPJE copies.
10. **Technologies** — .NET 8 / ASP.NET Core, PostgreSQL 16, Windows Server service, protobuf, ECDSA P-256 (CNG),
    QuestPDF, QRCoder, Npgsql, Dapper.
11. **Versioning and change management** — version + build number in the app; how changes are notified to ATK (Art. 7.4).

## 7. Readiness — what to do before applying

**Decisions**
1. **Which configuration we certify.** Central signing on the HRS server (built, tested) or signing on each workstation
   with the HRS Fiscal Client (default setting, but the client is **not built yet**). Recommendation: certify central
   signing first, with ATK's written agreement (open question #10), and notify the client later as a change under Art. 7.4.
2. **Applicant.** Decided 09.10.2026: the application is made by a **natural person without a registered business**
   (5.2: copy of personal ID). Product name: **Opera Cloud Fiscal Solution - Kosovo**.

**Product gaps**
| # | Gap | Why it matters | Owner |
|---|---|---|---|
| 1 | Fiscal block on the OPERA folio: QR, NUIKF, SEF ID, *e-kupon*, RKS logo | The receipt the guest gets is the folio; "accuracy of receipt generation" will be judged on it | HRS + Oracle (FLIP response format) |
| 2 | "OFFLINE" mark on folios issued while ATK is unreachable | Required by the technical spec | HRS (after #1) |
| 3 | KOPJE E KUPONIT on OPERA reprints | Required for copies; we only mark HRS's own PDF copies | HRS + Oracle/ATK |
| 4 | Albanian (and Serbian) texts on the receipt and in the user interface | Receipt content in Albanian and Serbian, Latin script | HRS |
| 5 | Clock check against ATK | Spec requires time synchronised with ATK | HRS (add a check + alert) |
| 6 | Working ATK TEST profile | The current fiscalization number returns 404 on `/ca/verify`; the Commission demo needs registration to work | Hotel/HRS: new number from EDI |
| 7 | HRS Fiscal Client (only if certifying workstation signing) | Not built | HRS |
| 8 | Release for certification | Freeze a version (e.g. 1.0.0), tag it, keep source and build reproducible; Art. 7.4 applies to every later change | HRS |

**Documents**
- Application form (annex) — **ask ATK for it.**
- Technical description in Albanian (§6).
- User manual in Albanian: front desk (what changes at check-out, offline, reprint), administrator (settings,
  workstations, exports, audit), installation (existing manual, translated).
- Declaration of compliance, signed.
- ARBK certificate.

**Written answers to get from ATK before submitting** (they decide what the Commission will test against):
- Central signing on one server for several POS — acceptable? (#10)
- VAT rounding rule — per line or per VAT group? (#9; ATK TEST accepts both)
- Duplicate transactions for one CouponId after a timeout (ATK does not de-duplicate)
- Advance payments/deposits in hotels (UA Art. 6.2)
- Offline marking when the receipt is a folio printed by OPERA
- Is the *SEF identification number* in the certificate the same as the `ApplicationId` used in the API?
- Foreign developer: documents instead of the ARBK certificate
- Electronic submission channel; whether source code or a test installation must be provided

## 8. After certification — obligations

| Obligation | Article |
|---|---|
| Notify ATK **in advance** of any functional or technical change affecting generation, storage or transmission of fiscal data | 7.4 |
| Keep the software up to date when ATK changes the conditions; otherwise the certificate can be suspended, withdrawn or cancelled | 8.2.3 |
| Allow ATK to verify how the SEF works after installation at a hotel | 8.1 |
| Avoid serious operational problems for users and any breach of the rules — both are grounds for suspension, withdrawal or cancellation | 8.2.1–8.2.2 |
| Deliver additional documents/guarantees when ATK asks, on time | 5.9–5.10 |

Practical consequences for HRS:
- Keep a **change log** per version and classify each change: fiscal (notify ATK first) or non-fiscal (UI, design).
  The redesign of the admin screens, for example, is non-fiscal; a change to VAT rounding or signing is fiscal.
- Keep **support capacity**: serious user problems can cost the certificate.
- Monitor ATK publications and the fiskalizimi repositories for specification changes.

## 9. Suggested plan

| Week | Work |
|---|---|
| 1 | Send ATK the written questions (§7) and ask for the application form; confirm the applicant entity; get a fresh TEST fiscalization number |
| 1–3 | Close product gaps 1–6 (folio fiscal block depends on Oracle's answer); freeze release 1.0.0 |
| 2–3 | Write the Albanian technical description, user manual and declaration |
| 4 | Full rehearsal of the Commission test on the TEST environment (live OPERA test property, offline test, return, reprint) |
| 4–5 | Submit; keep a developer on standby for the 7-working-day correction windows; decision within 15 working days |

Sources: ATK document of 03.07.2026 (Arts. 1–9); *sef-requirements-and-api.md*; ATK TEST run of 08.10.2026.

## 10. Submission package (drafted 09.10.2026; developer and applicant: Behar Lushaku)

| Doc | Status |
|---|---|
| 5.1 Application request (cover letter / data for ATK's form) | Drafted (Albanian) — fill in personal data, sign |
| 5.2 Copy of personal ID | Applicant attaches |
| 5.5 General description of the software solution | Drafted (Albanian), with architecture diagram |
| 5.6 User manual and basic instructions | Drafted (Albanian); installation manual attached as technical annex (English) |
| 5.7 Declaration of software compliance | Drafted (Albanian) — fill in, sign |
