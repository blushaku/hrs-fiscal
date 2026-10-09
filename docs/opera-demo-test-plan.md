# Testing Opera Cloud Fiscal Solution on an OPERA Cloud demo (OFIS with FLIP)

Full configuration steps: [manual-server-installation.md](manual-server-installation.md) and
[manual-opera-cloud-ofis-flip.md](manual-opera-cloud-ofis-flip.md).

Goal: send real folios from an OPERA Cloud demo property through Oracle FLIP to the fiscal server. Stage 1 captures
the real message format. Stage 2 fiscalizes against ATK's TEST environment and prints the fiscal data on the folio.

## What is needed

| # | Item | Who |
|---|---|---|
| 1 | An OPERA Cloud **demo/test property** with the fiscal module available (OPERA Control *Fiscal Folio Printing*; Cashiering parameter *Fiscal Service Terminals*) | PMS team / Oracle |
| 2 | **Oracle FLIP installed** on a Windows machine that OPERA Cloud can reach, registered for that property | Oracle (or the implementer with Oracle's installer) |
| 3 | A Windows machine **on the same network as FLIP** for the fiscal server (can be the FLIP machine), with PostgreSQL 14+ | Implementer |
| 4 | ATK TEST identity: NUI 811159898 / ApplicationId 356526644730 (already working) | done |
| 5 | Oracle's FLIP fiscal-partner specification. Stage 1 works without it; stage 2 needs the response format | Oracle |

## Stage 1: capture (no Oracle specification needed)

1. Install the .NET 8 ASP.NET Core Runtime (Windows Hosting Bundle) and PostgreSQL; create an empty database `hrs_fiscal`.
2. Unzip `Opera-Cloud-Fiscal-Solution-Kosovo-win-x64.zip` and run, in an elevated PowerShell:
   ```powershell
   .\install-server.ps1 -ConnectionString "Host=127.0.0.1;Database=hrs_fiscal;Username=postgres;Password=..." -AdminPassword "<first admin password>"
   ```
   The admin page opens at `http://<server>:5080`; FLIP is accepted at `http://<server-LAN-IP>:5100/flip`.
   Add `-FlipTcpPort 5200` if FLIP turns out to use raw sockets instead of HTTP.
3. Sign in as `admin`. Settings › General › **OPERA / FLIP connection** = **Capture** (the default).
4. In OPERA Cloud (Administration › Financial › Fiscal Management):
   - **Fiscal Partners**: create the fiscal partner. For the demo enable the payloads *Folio Generation*, *Post Payment* and
     *Check Out* to see what each sends (production: *Folio Generation* only); tick *Send Folio after Fiscal Payload*.
   - **Fiscal Folio Parameters**: set **FLIP Server Address** to the FLIP host as Oracle instructs.
   - **Fiscal Terminals**: one per workstation, for example Terminal ID `FO1`, Address/Port = `<fiscal server LAN IP>:5100`.
     Do not mark a terminal *Primary* (OPERA would then use it for every folio without asking).
   - In FLIP's own partner configuration, point the partner endpoint to `http://<fiscal server LAN IP>:5100/flip`.
5. In OPERA, check in a test reservation, post a room charge, F&B, a payment, then **settle and generate the folio**.
   Also run a check-out, a payment without a folio, a split folio and a folio with a negative correction.
6. In Opera Cloud Fiscal Solution › **FLIP messages**, each message appears with its raw body (XML/JSON pretty-printed) and headers.
   Use *Download raw* for each scenario and add the files to `docs/flip-samples/`.
7. Try **test reply** variants in Settings (status code / body) to see how OPERA reacts. Does it wait? What does it show
   or print? How long is its timeout?

Result: the real payload and the behaviour OPERA expects, for building the mapping and the response.

## Stage 2: live fiscalization against ATK TEST

Built after stage 1, using the captured samples (and Oracle's specification as soon as it arrives):
1. FLIP payload → `ReceiptRequest` (all fields from OPERA; optional overrides when switched on).
2. Route by Fiscal Terminal ID → the workstation's ATK POS; signed by the Fiscal Client on that workstation or, in
   central mode, by the server with that workstation's key; sent to ATK TEST.
3. Archive the receipt and answer FLIP with the fiscal data: NUIKF, receipt no., SEF ID, ATK transaction, QR string.
4. OPERA folio template (PMS team): a fiscal block with QR, NUIKF, SEF ID, receipt no., ATK transaction and "e-kupon".
5. Switch Settings › OPERA / FLIP connection to **Live**, then rerun the stage 1 scenarios. Each folio now shows its QR,
   which can be verified with ATK's citizen check.

## Notes
- The FLIP port also serves the admin pages. For the demo that is acceptable; for production, the admin pages will be
  bound to their own port only, and FLIP traffic will be restricted to the FLIP host's IP.
- Capture mode stores guest names and amounts exactly as OPERA sends them. Use demo data only.
