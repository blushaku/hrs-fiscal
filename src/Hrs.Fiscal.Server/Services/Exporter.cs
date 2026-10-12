using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Hrs.Fiscal.Server.Services;

public sealed record ExportFile(byte[] Content, string ContentType, string FileName, int Rows);

/// <summary>
/// CSV/PDF exports for ATK or another authority (ATK technical requirements Art 26; amendment 22.06.2026),
/// and PDF copies of single receipts ("KOPJE E KUPONIT"). Every export is written to the audit log with its SHA-256.
/// </summary>
public sealed class Exporter(ReceiptQueries receipts, AuditLog audit, SettingsStore settings, PropertyClock clock)
{
    static Exporter()
    {
        // QuestPDF Community licence: free for companies under USD 1M annual revenue.
        // Check HRS's eligibility; otherwise buy a Professional licence (see README).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static readonly string[] Datasets = ["receipts", "pending", "audit", "summary"];

    public async Task<ExportFile?> ExportAsync(string dataset, string format, DateOnly? from, DateOnly? to,
        string? requestedBy, string actor, CancellationToken ct, long? terminalId = null, string? status = null, string? action = null)
    {
        if (!Datasets.Contains(dataset) || format is not ("csv" or "pdf")) return null;
        TerminalRow? terminal = null;
        if (terminalId is not null)
            terminal = (await receipts.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId) ?? throw new ArgumentException("Unknown workstation.");

        var title = dataset switch
        {
            "receipts" => "Fiscal receipts",
            "pending" => "Receipts not yet sent to ATK",
            "summary" => "Summary by workstation",
            _ => "Audit log",
        };
        string[] header;
        var rows = new List<string[]>();

        if (dataset == "audit")
        {
            header = ["Id", "Time", "Actor", "Workstation", "Action", "Entity", "Entity id", "Details"];
            await foreach (var a in audit.StreamAsync(new AuditFilter { From = from, To = to, TerminalId = terminalId, Action = action }, ct))
                rows.Add([a.Id.ToString(), clock.Format(a.At), a.Actor, a.TerminalLabel ?? "", a.Action, a.Entity ?? "", a.EntityId ?? "", a.Details]);
        }
        else if (dataset == "summary")
        {
            header = ["POS ID", "Workstation", "Computer", "Sales", "Sales EUR", "Returns", "Returns EUR", "Net EUR", "Net VAT EUR",
                      "Accepted", "Waiting", "Rejected", "First receipt", "Last receipt"];
            string E(long c) => (c / 100m).ToString("0.00", CultureInfo.InvariantCulture);
            var list = await receipts.SummaryAsync(new ReceiptFilter { From = from, To = to, TerminalId = terminalId }, ct);
            foreach (var w in list)
                rows.Add([w.PosId.ToString(), w.TerminalLabel, w.Hostname, w.Sales.ToString(), E(w.SalesCents), w.Returns.ToString(), E(w.ReturnsCents),
                          E(w.NetCents), E(w.NetTaxCents), w.Accepted.ToString(), w.Pending.ToString(), w.Rejected.ToString(),
                          w.FirstAt is { } f ? clock.Format(f) : "", w.LastAt is { } l ? clock.Format(l) : ""]);
            if (list.Count > 1)
                rows.Add(["", "Total", "", list.Sum(w => w.Sales).ToString(), E(list.Sum(w => w.SalesCents)), list.Sum(w => w.Returns).ToString(),
                          E(list.Sum(w => w.ReturnsCents)), E(list.Sum(w => w.NetCents)), E(list.Sum(w => w.NetTaxCents)),
                          list.Sum(w => w.Accepted).ToString(), list.Sum(w => w.Pending).ToString(), list.Sum(w => w.Rejected).ToString(), "", ""]);
        }
        else
        {
            header = ["Issued", "Receipt no.", "NUIKF", "Type", "Refers to", "Source document", "Workstation", "POS ID", "Cashier",
                      "Total EUR", "Status", "Issued offline", "ATK transaction"];
            var filter = new ReceiptFilter { From = from, To = to, TerminalId = terminalId, Status = dataset == "pending" ? "pending" : status };
            await foreach (var r in receipts.StreamAsync(filter, ct))
                rows.Add([clock.Format(r.IssuedAt), r.CouponId.ToString(), r.VerificationNo, CouponTypes.Label(r.CouponType),
                          r.ReferenceCouponId?.ToString() ?? "", r.SourceDocument ?? "", r.TerminalLabel, r.PosId.ToString(), r.OperatorId,
                          (r.TotalCents / 100m).ToString("0.00", CultureInfo.InvariantCulture), r.Status, r.IssuedOffline ? "yes" : "no",
                          r.AtkTransactionId?.ToString("0", CultureInfo.InvariantCulture) ?? ""]);
        }

        var business = await settings.BusinessAsync(ct);
        var period = $"{from?.ToString("dd.MM.yyyy") ?? "start"} – {to?.ToString("dd.MM.yyyy") ?? clock.Today.ToString("dd.MM.yyyy")}"
                     + (terminal is null ? "" : $" · workstation {terminal.OperaTerminalId ?? terminal.Hostname} (POS {terminal.PosId})");
        if (terminal is not null) title += $" — POS {terminal.PosId}";
        var content = format == "csv"
            ? Csv(header, rows)
            : TablePdf(title, business, period, requestedBy, actor, header, rows);

        var stamp = $"{from?.ToString("yyyy-MM-dd") ?? "all"}_{to?.ToString("yyyy-MM-dd") ?? clock.Today.ToString("yyyy-MM-dd")}";
        var fileName = terminal is null ? $"{dataset}_{stamp}.{format}" : $"{dataset}_pos{terminal.PosId}_{stamp}.{format}";
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        await audit.WriteAsync(actor, format == "csv" ? AuditLog.Actions.ExportCsv : AuditLog.Actions.ExportPdf, "export", fileName,
            new { dataset, from, to, workstation = terminal?.PosId, status, action, rows = rows.Count, requestedBy, sha256 = sha }, terminalId, ct);

        return new ExportFile(content, format == "csv" ? "text/csv; charset=utf-8" : "application/pdf", fileName, rows.Count);
    }

    /// <summary>
    /// PDF copy of one fiscal receipt, marked "KOPJE E KUPONIT", laid out like ATK's SEF receipt samples
    /// (Doc. 01-06-1967 Art 25.18, Annex F). Logged as REPRINT.
    /// </summary>
    public async Task<ExportFile?> ReceiptCopyPdfAsync(long id, string actor, CancellationToken ct)
    {
        var r = await receipts.GetAsync(id, ct);
        if (r is null) return null;
        var coupon = PosCoupon.Parser.ParseFrom(r.PosCoupon);
        var qr = QrRenderer.Png(r.QrString, 8);
        var rates = (await settings.VatRatesAsync(ct)).ToDictionary(v => v.Letter, v => v.Percent);
        var inv = CultureInfo.InvariantCulture;
        string Eur(long cents) => (cents / 100m).ToString("0.00", inv);
        string Price(long units) => (units / 10000m).ToString("0.00##", inv);
        DateTime? referenceAt = null;
        if (r.ReferenceCouponId is { } refId)
            referenceAt = await receipts.IssuedAtAsync(refId, ct);

        var pdf = Document.Create(doc => doc.Page(page =>
        {
            page.Size(80, 297, Unit.Millimetre);       // thermal-receipt width (Art 26.14: 58–100 mm)
            page.ContinuousSize(80, Unit.Millimetre);
            page.Margin(4, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(7.5f));
            page.Content().Column(col =>
            {
                col.Spacing(2.5f);
                void Head(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) col.Item().Text($"{k}: {v}"); }
                Head("TATIMPAGUESI", r.BusinessName);
                Head("EMRI I NJËSISË", r.BranchName);
                Head("ADRESA E NJËSISË", r.Address);
                Head("VENDI", r.Location);
                Head("NF-NUI", r.BusinessNui.ToString(inv));
                Head("NUMRI I TVSH-SË", r.VatNo);
                Head("EMRI I PUNËTORIT", r.OperatorId);
                col.Item().PaddingVertical(2).Border(1).Padding(2).AlignCenter().Text("KOPJE E KUPONIT").Bold().FontSize(10);
                col.Item().AlignCenter().Text(ReceiptTexts.TypeTitle(r.CouponType)).Bold().FontSize(9);
                if (r.ReferenceCouponId is { } reference)
                    col.Item().Text($"KUPON FISKAL NR. {reference}" + (referenceAt is { } at ? $"  DATË: {clock.ToLocal(at):dd/MM/yyyy HH:mm}" : ""));
                if (r.CancelReason is { } reason) col.Item().Text($"ARSYEJA E ANULIMIT: {reason.ToUpperInvariant()}").SemiBold();
                if (r.PaperSerial is { } serial) col.Item().Text($"NGA BLLOKU I VEÇANTË, NR. {serial}").SemiBold();

                col.Item().PaddingTop(3).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(3.2f); c.RelativeColumn(2.4f); c.RelativeColumn(1.6f); c.ConstantColumn(9); });
                    t.Header(h =>
                    {
                        foreach (var label in new[] { "ARTIKULLI", "SASIA X ÇMIMI", "SHUMA", "" })
                            h.Cell().BorderBottom(0.5f).PaddingBottom(1).Text(label).SemiBold();
                    });
                    foreach (var i in coupon.Items)
                    {
                        var gross = (long)Math.Round(i.Price * (decimal)i.Quantity, MidpointRounding.AwayFromZero);
                        t.Cell().Text(i.Name);
                        t.Cell().Text($"{((decimal)i.Quantity).ToString("0.###", inv)} X {Price(i.Price)}");
                        t.Cell().AlignRight().Text((gross / 10000m).ToString("0.00", inv));
                        t.Cell().AlignRight().Text(i.TaxRate);
                        if (gross != i.Total)
                        {
                            t.Cell().PaddingLeft(6).Text("Ulje");
                            t.Cell().Text("");
                            t.Cell().AlignRight().Text("-" + ((gross - i.Total) / 10000m).ToString("0.00", inv));
                            t.Cell().Text("");
                        }
                    }
                });

                col.Item().LineHorizontal(0.5f);
                void Row(string k, string v, bool bold = false) => col.Item().Row(row =>
                {
                    var left = row.RelativeItem().Text(k); var right = row.ConstantItem(70).AlignRight().Text(v);
                    if (bold) { left.Bold(); right.Bold(); }
                });
                if (coupon.TotalDiscount > 0) Row("ULJE GJITHSEJ", "-" + Eur(coupon.TotalDiscount));
                Row("TOTALI PËR PAGESË", Eur(coupon.Total) + " €", bold: true);
                foreach (var p in coupon.Payments) Row($"MËNYRA E PAGESËS {ReceiptTexts.PaymentLabel((int)p.Type)}", Eur(p.Amount) + " €");
                foreach (var g in coupon.TaxGroups)
                    Row($"TVSH {g.TaxRate}" + (rates.TryGetValue(g.TaxRate, out var pct) ? $" {pct.ToString("0.##", inv)}%" : ""), Eur(g.TotalTax) + " €");
                Row("TOTAL PA TVSH", Eur(coupon.TotalNoTax) + " €");
                col.Item().Text($"ARTIKUJ - {coupon.Items.Count}");
                col.Item().LineHorizontal(0.5f);

                col.Item().Text(r.CouponId.ToString("0000000000", inv));
                col.Item().Text($"DATA DHE ORA {clock.ToLocal(r.IssuedAt):dd/MM/yyyy HH:mm}");
                col.Item().Text($"NR. IDENTIFIKUES I SEF {r.BranchId}-{r.BusinessNui}-{r.PosId}");
                col.Item().Text($"NUIKF: {r.VerificationNo}").Bold();
                if (r.DailyNo is { } daily) col.Item().Text($"KUPON FISKAL DITOR NR. {daily:0000}");
                if (r.Offline) col.Item().PaddingVertical(1).AlignCenter().Text("OFFLINE").Bold().FontSize(11);
                col.Item().PaddingTop(2).AlignCenter().Width(34, Unit.Millimetre).Image(qr);
                col.Item().AlignCenter().Text("e-kupon").Bold();
                // Fiscal logo (Annex: min 15 × 8 mm, max 20 × 10 mm).
                col.Item().AlignCenter().Width(18, Unit.Millimetre).Height(9, Unit.Millimetre).Border(1).AlignMiddle().Column(logo =>
                {
                    logo.Item().AlignCenter().Text("RKS").Bold().FontSize(8);
                    logo.Item().AlignCenter().Text("MF").Bold().FontSize(6);
                });
                col.Item().PaddingTop(3).AlignCenter().Text($"Transaksioni ATK: {r.AtkTransactionId?.ToString("0", inv) ?? "në pritje"}").FontSize(6.5f);
                col.Item().AlignCenter().Text($"Kopje e gjeneruar {clock.Format(DateTime.UtcNow)} nga {actor}").FontSize(6).FontColor(Colors.Grey.Darken1);
            });
        })).GeneratePdf();

        await audit.WriteAsync(actor, AuditLog.Actions.Reprint, "receipt", r.CouponId.ToString(),
            new { format = "pdf", sha256 = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant() }, r.TerminalId, ct);
        return new ExportFile(pdf, "application/pdf", $"kopje_{r.CouponId}.pdf", 1);
    }

    private static byte[] Csv(string[] header, List<string[]> rows)
    {
        var sb = new StringBuilder();
        void Line(IEnumerable<string> cells) => sb.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
        Line(header);
        foreach (var r in rows) Line(r);
        // UTF-8 with BOM so Excel shows ë/ç correctly.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Escape(string v)
    {
        // Neutralise spreadsheet formula injection, then quote if needed.
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private byte[] TablePdf(string title, BusinessInfo? business, string period, string? requestedBy, string actor,
        string[] header, List<string[]> rows) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(7.5f));
            page.Header().Column(c =>
            {
                c.Item().Text(title).Bold().FontSize(14);
                c.Item().Text($"{business?.Name} · NUI {business?.Nui} · Njësia {business?.BranchId} · Period {period}");
                c.Item().Text($"Exported {clock.Format(DateTime.UtcNow)} by {actor}" + (string.IsNullOrWhiteSpace(requestedBy) ? "" : $" · requested by {requestedBy}") + $" · {rows.Count} rows");
                c.Item().PaddingBottom(6);
            });
            page.Content().Table(t =>
            {
                t.ColumnsDefinition(c => { foreach (var _ in header) c.RelativeColumn(); });
                t.Header(h => { foreach (var col in header) h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(col).SemiBold(); });
                foreach (var row in rows)
                    foreach (var cell in row)
                        t.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten1).Padding(2).Text(cell);
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"{AppVersion.ProductName} {AppVersion.Display} · {AppVersion.Developer}").FontSize(8).FontColor(Colors.Grey.Darken1);
                r.RelativeItem().AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
            });
        })).GeneratePdf();
}
