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

    /// <summary>PDF copy of one fiscal receipt, marked "KOPJE E KUPONIT". Logged as REPRINT.</summary>
    public async Task<ExportFile?> ReceiptCopyPdfAsync(long id, string actor, CancellationToken ct)
    {
        var r = await receipts.GetAsync(id, ct);
        if (r is null) return null;
        var coupon = PosCoupon.Parser.ParseFrom(r.PosCoupon);
        var qr = QrRenderer.Png(r.QrString, 8);

        var pdf = Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontSize(9));
            page.Content().Column(col =>
            {
                col.Spacing(6);
                col.Item().AlignCenter().Text(r.BusinessName).Bold().FontSize(12);
                col.Item().AlignCenter().Text($"NUI: {r.BusinessNui}" + (string.IsNullOrEmpty(r.VatNo) ? "" : $" · TVSH: {r.VatNo}"));
                col.Item().AlignCenter().Text($"{r.Address ?? r.Location} · Njësia: {r.BranchId}");
                col.Item().Border(1.5f).Padding(4).AlignCenter().Text("KOPJE E KUPONIT").Bold().FontSize(12);
                col.Item().AlignCenter().Text($"KUPON FISKAL · {CouponTypes.Label(r.CouponType).ToUpperInvariant()}").Bold();

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(4); c.RelativeColumn(1.4f); c.RelativeColumn(1.6f); c.RelativeColumn(0.8f); c.RelativeColumn(1.6f); });
                    t.Header(h =>
                    {
                        foreach (var label in new[] { "Artikulli", "Sasia", "Çmimi", "TVSH", "Vlera" })
                            h.Cell().BorderBottom(0.5f).PaddingBottom(2).Text(label).SemiBold();
                    });
                    foreach (var i in coupon.Items)
                    {
                        t.Cell().Text(i.Name);
                        t.Cell().AlignRight().Text($"{i.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {i.Unit}");
                        t.Cell().AlignRight().Text((i.Price / 10000m).ToString("0.0000", CultureInfo.InvariantCulture));
                        t.Cell().AlignCenter().Text(i.TaxRate);
                        t.Cell().AlignRight().Text((i.Total / 10000m).ToString("0.00", CultureInfo.InvariantCulture));
                    }
                });

                col.Item().LineHorizontal(0.5f);
                foreach (var g in coupon.TaxGroups)
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text($"TVSH {g.TaxRate}: baza {(g.TotalForTax / 100m):0.00}");
                        row.ConstantItem(80).AlignRight().Text($"{(g.TotalTax / 100m):0.00}");
                    });
                col.Item().Row(row =>
                {
                    row.RelativeItem().Text("TOTAL EUR").Bold().FontSize(11);
                    row.ConstantItem(80).AlignRight().Text($"{(coupon.Total / 100m):0.00}").Bold().FontSize(11);
                });
                foreach (var p in coupon.Payments)
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text(p.Type.ToString());
                        row.ConstantItem(80).AlignRight().Text($"{(p.Amount / 100m):0.00}");
                    });

                col.Item().LineHorizontal(0.5f);
                void Field(string k, string v) => col.Item().Row(row => { row.RelativeItem().Text(k); row.RelativeItem(2).AlignRight().Text(v); });
                Field("Data", clock.Format(r.IssuedAt));
                Field("Arkëtari", r.OperatorId);
                if (!string.IsNullOrEmpty(r.SourceDocument)) Field("Dokumenti", r.SourceDocument);
                Field("Kupon nr.", r.CouponId.ToString());
                if (r.ReferenceCouponId is not null) Field("Referenca", r.ReferenceCouponId.ToString()!);
                Field("SEF ID", $"{r.BranchId}-{r.BusinessNui}-{r.PosId}");
                Field("Transaksioni ATK", r.AtkTransactionId?.ToString("0", CultureInfo.InvariantCulture) ?? "në pritje");
                if (r.IssuedOffline) col.Item().AlignCenter().Text("OFFLINE").Bold();

                col.Item().AlignCenter().Width(140).Image(qr);
                col.Item().AlignCenter().Text($"NUIKF: {r.VerificationNo}").Bold();
                col.Item().AlignCenter().Text("e-kupon").Bold();
                col.Item().AlignCenter().Text($"Kopje e gjeneruar {clock.Format(DateTime.UtcNow)} nga {actor}").FontSize(7).FontColor(Colors.Grey.Darken1);
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
            page.Footer().AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf();
}
