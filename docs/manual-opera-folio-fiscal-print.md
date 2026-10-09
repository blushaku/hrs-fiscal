# HRS Fiscal Solution Kosovo — Printing fiscal data and the QR code on the OPERA Cloud folio

**Audience:** OPERA Cloud administrator / implementation consultant. **Applies to:** OPERA Cloud with OFIS
(on-premise FLIP) and HRS Fiscal Server 0.9 in live mode.

Items marked **[Oracle]** depend on how FLIP stores the partner response in OPERA and must be confirmed with Oracle
(FLIP installation guide / Oracle support) before go-live. Everything else uses standard OPERA Cloud features.

---

## 1. How the data reaches the folio

```
Settle folio in OPERA ──► OPERA builds the fiscal payload ──► FLIP ──► HRS Fiscal Server
                                                                         │ validate, sign, archive, send to ATK
Folio PDF ◄── folio template (RTF) ◄── OPERA stores fiscal data ◄── FLIP ◄┘ answer: receipt no., NUIKF, SEF ID, QR …
```

OPERA waits for the answer (up to the fiscal timeout) and only then renders the folio. The folio template prints
what OPERA stored from the answer; the QR image is drawn by OPERA from a **QR Code Definition**.

## 2. What must be printed (Kosovo)

| On the folio | Comes from HRS answer field | Notes |
|---|---|---|
| QR code | `QrCode` | Exactly as received: `<base64 CitizenCoupon>\|<base64 signature>` — no spaces, no line breaks. Min. 12 × 12 mm; recommended 30 × 30 mm. **No logo inside.** |
| NUIKF (verification no.) | `VerificationNo` | Max 16 characters |
| Receipt number | `FiscalBillNo` | ATK coupon number |
| SEF ID | `SefId` | Unit–NUI–POS |
| ATK transaction | `AtkTransactionId` | Empty while `AtkStatus` = `pending` (receipt is still valid; HRS sends it later) |
| Date/time of the receipt | `IssuedAt` | |
| Text **e-kupon** | fixed text | |

The QR text is typically 250–400 characters and grows with the number of VAT groups, so it needs a QR version
that holds at least ~450 bytes (see step 4).

## 3. Before you start

- OPERA Cloud tasks for your role: **Reports › Manage Reports**, **Copy Reports**, **QR Code Configuration**.
- OPERA controls: **Cashiering › Fiscal Folio Printing** = On (already on for OFIS).
- On the workstation: Microsoft Word + **Oracle BI Publisher Desktop** (template builder add-in, same bitness as Office).
- One test folio already fiscalized by HRS in the TEST environment (HRS › Receipts shows it).
- **[Oracle]** The OPERA data elements that hold the fiscal answer. Confirm with Oracle which FLIP response fields are
  stored and under which names in the folio data model. OPERA documents a *Fiscal Bill No* returned by the fiscal
  service; the QR string, NUIKF, SEF ID and ATK transaction need the extended response
  (`FLIP_EXTENDED_RESPONSE = YES`, already set in your fiscal folio parameters). HRS will adapt its answer to the
  exact FLIP response format once Oracle confirms it — the current JSON answer is provisional.

## 4. Create the QR Code Definition

1. **OPERA Cloud menu › Reports › Configure Reports › QR Code Configuration › New.**
2. Fill in:
   | Field | Value |
   |---|---|
   | Property | your property |
   | Code | `HRSFISCALQR` (referenced in the template; letters/digits only) |
   | Description | Kosovo fiscal QR (ATK) |
   | Stationery (Template) Type | Folio |
   | Stationery (Template) Section | the folio header/body section that lists the fiscal fields **[Oracle]** |
   | QR Code Version | 15 or higher (holds the full QR text; 20 if you have many VAT rates) |
   | QR Code Quiet Zone | 6 (default) |
   | QR Code Size | Custom → large enough to print ≥ 30 mm |
   | Add Logo to QR Code | **Off** (ATK: no logo inside the QR) |
3. **QR Code Configuration** (rich text): click **Merge Codes** and insert **only** the merge code for the fiscal QR
   string **[Oracle]**. No other text, no spaces, no empty line after it — any extra character makes the QR invalid.
4. **Preview** — with real data the preview must scan to the same text HRS shows on the receipt. **Save.**

## 5. Copy the folio template

1. **Reports › Manage Reports › New Report.**
2. Property; **Report Group:** folio stationery; **Report Type:** Customized Report; **Sample Report:** the folio
   template you use today (or the standard sample folio).
3. **Download Sample Report (RTF)** and **Download Sample Data (XML)**.
4. Rename the RTF so it does **not** start with `SAMPLE` — e.g. `xk_fiscal_folio.rtf` (`e_xk_fiscal_folio.rtf` for
   multi-language).
5. Open the XML in a text editor and search for `FISCAL` to find the fiscal elements and their exact spelling
   (case-sensitive). Compare them with the list from Oracle (step 3).

## 6. Add the fiscal block in Word

1. Open the RTF in Word, **BI Publisher › Load XML** (the sample data).
2. Below the totals (before the footer) insert a 2-column table with no borders:
   left column the QR, right column the text fields.
3. Wrap the block so it only prints on fiscalized folios (replace `FISCAL_BILL_NO` with the real element name):
   ```
   <?if:FISCAL_BILL_NO!=''?>
     … fiscal block …
   <?end if?>
   ```
4. **QR code** — insert a form field (BI Publisher › Field) in the left cell and, in its *Help text*, enter:
   ```
   <fo:instream-foreign-object content-type="image/jpg"><xsl:value-of select=".//HRSFISCALQR"/></fo:instream-foreign-object>
   ```
   (`HRSFISCALQR` = the code from step 4.)
5. **Text fields** in the right cell (insert each with BI Publisher › Field, real element names **[Oracle]**):
   ```
   e-kupon
   Nr. i kuponit:  <?FISCAL_BILL_NO?>
   NUIKF:          <?…VERIFICATION…?>
   SEF ID:         <?…SEF_ID…?>
   Transaksioni ATK: <?…ATK_TRANSACTION…?>
   Data/ora:       <?…ISSUED_AT…?>
   ```
   If the ATK transaction can be empty (receipt sent later), wrap that line in `<?if:…!=''?> … <?end if?>`.
6. Keep fonts at least 8 pt; don't scale the QR below 30 mm. Save as **RTF**.

Tip: to check the layout in Word, put test values into the fiscal elements of the sample XML and use
**BI Publisher › Preview › PDF**. Word does not draw the QR — only OPERA does.

## 7. Upload and use it

1. Back in **Create Report**: **Choose File** → the RTF (max 10 MB), Print Copies, Language → **Save**.
2. Make it the folio template the cashiers use (the folio style / default folio template for the property).
3. Generate the test folio again. HRS recognises the same fiscal folio and answers with the **same** receipt, so a
   reprint never creates a new receipt.

## 8. Test checklist

| # | Test | Expected |
|---|---|---|
| 1 | Settle a folio (cash), print | Fiscal block with QR, receipt no., NUIKF, SEF ID, ATK transaction, e-kupon |
| 2 | Scan the QR with the ATK citizen app (TEST) | Coupon found, totals = folio |
| 3 | Compare with HRS › Receipts | Same receipt no., NUIKF and QR text |
| 4 | Two VAT rates on one folio | QR still scans (QR version large enough) |
| 5 | ATK offline (HRS test) | Folio prints; ATK transaction empty/hidden; HRS sends later |
| 6 | Reprint the folio | Same fiscal data; no new receipt in HRS |
| 7 | Credit folio (return) | New receipt no. and QR for the return |
| 8 | Folio refused by HRS (e.g. wrong property) | OPERA shows the HRS reason; no fiscal block |
| 9 | Non-fiscal / information folio | No fiscal block |

## 9. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| No fiscal block at all | Condition element name wrong (case-sensitive), or OPERA did not store the answer → check *Miscellaneous › Monitoring › Fiscal Business Event Status* and HRS › FLIP messages |
| QR missing, text OK | QR Definition code ≠ name in the expression, wrong stationery type/section, or the merge code is empty |
| QR prints but the app says invalid | Extra characters in the QR definition (space, line break, label) — the content must be the merge code only |
| QR cut or not readable | QR version too small or printed too small; use version ≥ 15 and ≥ 30 mm |
| Fiscal fields blank, QR OK | Element names in the template do not match the data model |

## 10. Open points

- **[Oracle]** FLIP partner response format and the OPERA folio elements that receive the fiscal fields (QR string,
  NUIKF, SEF ID, ATK transaction). Until confirmed, only the receipt number is likely to be stored, and HRS's answer
  is provisional.
- **Alternative if FLIP cannot pass the QR text:** OPERA templates can load an image by URL. HRS could serve each
  receipt's QR as an image (`url:{…}` in the template, keyed by property + fiscal folio ID). This needs development
  in HRS and an HTTPS endpoint reachable from OPERA Cloud — not available yet.
- **[ATK]** Whether an OPERA folio reprint must carry a "copy" mark (*KOPJE*) on the fiscal block.

## Sources

- [Managing QR Code Definitions](https://docs.oracle.com/en/industries/hospitality/opera-cloud/23.1/ocsuh/t_reports_managing_QR_code_definitions.htm) — OPERA Cloud User Guide
- [Using Stationery Template Editor](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/c_reports_using_stationery_editor_new.htm) — QR expression, conditional regions, images by URL
- [Configuring Stationery Templates](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/t_reports_creating_and_running_custom_bi_pub_reports.htm)
- [Prerequisites for Reports](https://docs.oracle.com/en/industries/hospitality/opera-cloud/23.1/ocsuh/r_prerequisites_for_reports.htm)
- [Monitoring Fiscal Business Event Status](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.2/ocsuh/t_monitoring_fiscal_business_event_status.htm)
- [OPERA Cloud 25.3 Release Readiness — Fiscal Bill No returned via the fiscal service](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.3/oprnc/c_feature_summary.htm)
