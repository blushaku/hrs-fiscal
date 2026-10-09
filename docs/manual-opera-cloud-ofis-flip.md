# OPERA Cloud — OFIS / FLIP Configuration for HRS Fiscal Solution (Kosovo)

For the HRS PMS team. Connects an OPERA Cloud property to the HRS Fiscal Server through Oracle's fiscal integration
(**OFIS**) with **FLIP** installed on-premise. Install the HRS Fiscal Server first
([manual-server-installation.md](manual-server-installation.md)).

> **What is confirmed and what is not.** Menu paths, OPERA controls and fields below are from Oracle's OPERA Cloud
> User Guide (links in *Sources*). Oracle has not yet given HRS the FLIP-to-fiscal-partner interface specification,
> so three points are marked **[Oracle]**: they must be confirmed with Oracle before go-live. Use the HRS server's
> *capture mode* to see exactly what OPERA/FLIP sends while these are open.

## 1. How the pieces connect

```
OPERA Cloud (Oracle data centre)
   │  fiscal payload when a folio is generated   (OFIS)
   ▼
Oracle FLIP — on-premise, hotel LAN              (OPERA Fiscal Folio Parameter "FLIP Server Address")
   │  HTTP over the LAN to the fiscal terminal   (OPERA Fiscal Terminal "Address and Port")
   ▼
HRS Fiscal Server  http://<HRS-IP>:5100/flip
   │  receipt signed for the workstation's ATK POS, sent to ATK, archived
   ▼
answer to FLIP → OPERA: NUIKF, receipt no., SEF ID, ATK transaction, QR → printed on the folio
```

Each OPERA **Fiscal Terminal** corresponds to one workstation in HRS (Settings › Workstations, field *OPERA Fiscal
Terminal ID*), which is one ATK POS with its own certificate.

*Alternative, not used here:* **OFIS Cloud**, where OPERA Cloud calls the partner's web service directly over the
internet without FLIP. It would need the HRS server reachable from the internet over HTTPS and an OPERA outbound
allowlist entry. HRS uses FLIP so that nothing in the hotel is exposed to the internet.

## 2. Prerequisites

| # | Item | Who | Notes |
|---|---|---|---|
| 1 | HRS Fiscal Server installed, business and workstations configured | HRS | Server manual §4–5 |
| 2 | **Oracle FLIP** installed on a Windows machine on the hotel LAN and registered for the property | Oracle / HRS with Oracle's installer | **[Oracle]** FLIP's own installation guide is supplied by Oracle; it is not public |
| 3 | **HRS available as a Fiscal Partner** for the property | Oracle | **[Oracle]** In OPERA the partner is *selected from a list*, and its Fiscal Folio Parameters come from the partner's *template*. Oracle must enable the HRS / Kosovo partner (or tell us which generic partner to use) |
| 4 | OPERA Controls switched on (§3) | Oracle or property admin with rights | Some controls can only be activated by Oracle |
| 5 | Role with the Fiscal Management tasks (§3) | Property admin | |
| 6 | Network: FLIP machine → HRS server TCP 5100 | Hotel IT | The HRS installer restricts port 5100 to the FLIP IP (`-FlipSourceIp`) |

## 3. OPERA Controls and user tasks

**OPERA Controls** — Administration › Enterprise › OPERA Controls › group **Cashiering**:
- Parameter **Fiscal Folio Printing** = On
- Parameter **Fiscal Service Terminals** = On (needed for Fiscal Terminals and Fiscal Commands)

Do **not** switch on *Fiscal Cloud Integration* (that is OFIS Cloud, see §1).

**Tasks** — Role Manager, group **Financial Admin › Fiscal Management**: give the implementation role
*Fiscal Commands (Edit)*, *Fiscal Partners (New/Edit, Delete)*, *Fiscal Terminal Setup (New/Edit, Delete)*,
*Fiscal Folio Parameters (New/Edit, Template, Delete)* and *Fiscal Folio Buckets (New/Edit, Delete)*.
Front-office supervisors also need **Financials › Cashiering › Fiscal Commands** to send commands to a terminal.

## 4. Fiscal Partner

Administration › **Financials › Fiscal Management › Fiscal Partners** › **New**

| Field | Set to | Why |
|---|---|---|
| Property | the hotel | |
| Partner | the HRS partner **[Oracle]** | Saving creates the partner's Fiscal Folio Parameters from its template |
| Priority | 1 | Up to three partners per property are possible; HRS must be the only fiscal partner |
| Fiscal Folio Payment Methods | empty | Empty = a payload for every folio, whatever the payment. Kosovo: every sale is fiscalized |
| Electronic / Fiscal Folio Profile Handling | **A** | A = generated for every payee, regardless of the profile setting |
| Fiscal Payloads | **Folio Generation** only | See below |
| Fiscal Payment Methods | empty | Only used with *Post Payment* |
| Send Folio after Fiscal Payload | On | OPERA sends the folio to the partner afterwards; HRS archives it with the receipt |

**Which payloads.** OPERA offers three:
- *Folio Generation* — when a folio window is settled; contains the unsettled transactions of that window. **This is the
  fiscal receipt.**
- *Check Out* — when the reservation is checked out; consolidated totals over all folios and windows. Enabling it
  together with *Folio Generation* risks fiscalizing the same charges twice.
- *Post Payment* — a payment without settling a folio (deposits, advances). Hotels in Kosovo do not take deposits, and
  advance payments are an open question with ATK.

Production: **Folio Generation only**. On the demo, all three can be enabled in capture mode to see what each sends
(the HRS server only records them).

## 5. Fiscal Folio Parameters

Administration › **Financials › Fiscal Management › Fiscal Folio Parameters** › search the property and partner.

The parameters are created from the HRS partner template when the partner is saved. Check:

| Code | Value | Notes |
|---|---|---|
| **FLIP Server Address** | Address of the **FLIP machine**, as given by Oracle (e.g. `http://192.168.1.10:<port>`) | **[Oracle]** exact format. This is FLIP, not the HRS server. Must be empty only for OFIS Cloud |
| other template parameters | as defined in the HRS partner template **[Oracle]** | Record every parameter and value in the hotel's implementation sheet |

## 6. Fiscal Terminals

Administration › **Financials › Fiscal Management › Fiscal Terminals** › **New** — one per workstation that issues folios.

| Field | Set to |
|---|---|
| Property | the hotel |
| Terminal ID | Same as *OPERA Fiscal Terminal ID* in HRS › Settings › Workstations, e.g. `FO1`, `FO2`, `NA1` |
| Partner | the HRS partner |
| Primary | **Off** (see below) |
| Terminal Label | Readable name, e.g. `Front desk 1` |
| Address and Port | HRS Fiscal Server: `<HRS-IP>:5100` (FLIP forwards to this address) **[Oracle]** confirm whether FLIP expects host:port or a full URL (`http://<HRS-IP>:5100/flip`) |
| Device Value | HRS **POS ID** of that workstation (for reference; HRS identifies the workstation by Terminal ID) |

**Primary terminal and terminal selection.** OPERA prompts the user to choose a Fiscal Terminal when several exist. If
one terminal is marked *Primary*, OPERA always uses it **without asking**. Because every workstation is a separate ATK
POS, do not mark a terminal as Primary unless the property has only one workstation; otherwise every receipt would be
issued under that one POS. Train front-office staff to pick their own workstation's terminal.

## 6a. FLIP → HRS authentication (access token)

FLIP authenticates to the HRS Fiscal Server with a token:
1. HRS › Settings › General › *OPERA / FLIP connection* › **Generate token**. Copy the token: it is shown only once
   (HRS stores only a hash). *Require the token* is switched on automatically.
2. Enter the token in FLIP's configuration for the HRS fiscal partner **[Oracle]** (field name per FLIP's guide).
   HRS accepts it in the `Authorization` header as `Bearer <token>` or as the bare token. If FLIP sends it in another
   header, enter that header name in HRS (*Header that carries the token*).
3. Send a test folio. Requests without the right token are answered with **401**, recorded on the FLIP messages page
   (mode *rejected*, without content) and logged as `FLIP_AUTH_FAILED`.

To rotate the token, generate a new one in HRS and update FLIP straight away: the old token stops working at once.
Together with the firewall rule (port 5100 open only to the FLIP machine) this means only FLIP can submit folios.

## 7. Fiscal Commands

Administration › **Financials › Fiscal Management › Fiscal Commands**: the commands offered depend on the fiscal
partner and country. Leave the HRS partner's commands active. Staff use them under **Financials › Cashiering › Fiscal
Commands** (select property › terminal › command). HRS will document each command once Oracle's partner template is
known.

## 8. HRS side

1. HRS › Settings › Workstations: each OPERA Terminal ID from §6 is entered for the matching workstation; each
   workstation is registered with ATK (central signing) or has the client installed (workstation-client mode).
2. HRS › Settings › General › **OPERA / FLIP connection**:
   - **Capture** while setting up: every message is stored and FLIP gets the configured test reply.
   - **Live** once go-live is approved (available after Oracle's specification is implemented).
3. ATK environment **Test** for all tests; switch to **Production** only for go-live.

## 9. Folio layout

The fiscal data must be printed on the folio: **QR code** (min. 12 × 12 mm, no logo inside), **NUIKF** (verification
number), **receipt number**, **SEF ID** (unit-business-POS), **ATK transaction** and the text **e-kupon**. Add a fiscal
block to the property's folio report that prints the fiscal data OPERA receives back from FLIP **[Oracle]** (field
names follow the partner response). No fiscal printer is used.

## 10. Test before go-live

Run each case in OPERA, then open HRS › **FLIP messages** (capture) or HRS › **Receipts** (live):

| # | Case | Expected |
|---|---|---|
| 1 | Check-in, room charge, settle folio by cash | One message / receipt; total = folio |
| 2 | Room + F&B + minibar (different VAT rates), card payment | VAT groups per rate |
| 3 | Split folio: two windows settled separately | One receipt per window |
| 4 | Two payments on one folio (cash + card) | Payments add up to the total |
| 5 | Settle on workstation FO2 | Receipt under FO2's POS ID |
| 6 | Negative correction / adjustment, then folio | Return receipt referencing the original (live) |
| 7 | Generate folio again (reprint) | No new receipt; copy marked *KOPJE E KUPONIT* |
| 8 | HRS server stopped, settle folio | What OPERA shows; receipt issued after restart (live) |
| 9 | ATK unreachable (live) | Receipt archived, sent later within 48 h |
| 10 | Check-out only, Post Payment only (demo, capture) | Recorded, but not used in production |

Download each captured message (*Download raw*) and send the files to HRS development.

## Sources

- [Prerequisites for Oracle Fiscal Integration Solution (OFIS)](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/c_prerequisites_for_financial_administration.htm) — OPERA Cloud User Guide 25.4
- [Configuring Fiscal Management](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/c_admin_financial_fiscal_management_title.htm)
- [Configuring Fiscal Partners](https://docs.oracle.com/en/industries/hospitality/opera-cloud/23.5/ocsuh/t_financial_admin_fiscal_management_configuring_fiscal_partners.htm)
- [Configuring Fiscal Folio Parameters](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/t_financial_admin_fiscal_management_configuring_fiscal_folio_parameters.htm)
- [Configuring Fiscal Terminals](https://docs.oracle.com/en/industries/hospitality/opera-cloud/23.5/ocsuh/t_financial_admin_fiscal_management_configuring_fiscal_terminals.htm)
- [Configuring Fiscal Commands](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/t_financial_admin_fiscal_management_configuring_fiscal_commands.htm) · [Sending Fiscal Commands](https://docs.oracle.com/en/industries/hospitality/opera-cloud/22.3/ocsuh/t_sending_fiscal_commands.htm)
- [Configuring OFIS Cloud Integrations](https://docs.oracle.com/en/industries/hospitality/opera-cloud/25.4/ocsuh/t_admin_financial_configuring_ofis_cloud_integrations.htm) (alternative without FLIP)
