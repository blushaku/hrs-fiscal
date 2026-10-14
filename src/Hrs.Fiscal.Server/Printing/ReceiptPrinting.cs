using System.Threading.Channels;
using Dapper;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Npgsql;

namespace Hrs.Fiscal.Server.Printing;

public sealed record PrintOutcome(bool Ok, string Message);

/// <summary>
/// Prints fiscal receipts on the workstation's thermal printer (Settings › Printing). The first print of a receipt is the
/// original; every later print is a copy marked "KOPJE E KUPONIT" (Doc. 01-06-1967 Art 25.18). Every print and every
/// failure is written to the audit log.
/// </summary>
public sealed class ReceiptPrinting(
    NpgsqlDataSource db, ReceiptQueries receipts, SettingsStore settings, AuditLog audit, PropertyClock clock,
    IPrinterTransport transport, ILogger<ReceiptPrinting> log)
{
    /// <param name="copy">null = original if the original was never printed, otherwise a copy.</param>
    public async Task<PrintOutcome> PrintAsync(long receiptId, string actor, bool? copy = null, CancellationToken ct = default)
    {
        var r = await receipts.GetAsync(receiptId, ct);
        if (r is null) return new(false, "Receipt not found.");
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == r.TerminalId);
        if (terminal is null || !terminal.HasPrinter)
            return new(false, $"Workstation POS {r.PosId} has no printer. Set it up under Settings › Printing.");

        var isCopy = copy ?? await OriginalPrintedAsync(receiptId, ct);
        var coupon = PosCoupon.Parser.ParseFrom(r.PosCoupon);
        var rates = (await settings.VatRatesAsync(ct)).ToDictionary(v => v.Letter, v => v.Percent);
        DateTime? referenceAt = r.ReferenceCouponId is { } refId ? await receipts.IssuedAtAsync(refId, ct) : null;
        var job = ThermalReceipt.Build(r, coupon, rates, clock, terminal.PrinterPaperMm, isCopy, referenceAt);
        var printer = Describe(terminal);
        try
        {
            await transport.SendAsync(terminal.PrinterKind, terminal.PrinterAddress!, job, ct);
        }
        catch (PrinterException ex)
        {
            log.LogWarning("Receipt {CouponId} could not be printed on {Printer}: {Message}", r.CouponId, printer, ex.Message);
            await audit.WriteAsync(actor, AuditLog.Actions.PrintFailed, "receipt", receiptId.ToString(),
                new { couponId = r.CouponId, copy = isCopy, printer, error = ex.Message }, terminal.Id, ct);
            return new(false, $"Receipt {r.CouponId} was not printed: {ex.Message}");
        }
        await audit.WriteAsync(actor, AuditLog.Actions.ReceiptPrinted, "receipt", receiptId.ToString(),
            new { couponId = r.CouponId, copy = isCopy, printer }, terminal.Id, ct);
        return new(true, isCopy ? $"Copy of receipt {r.CouponId} printed on {printer}." : $"Receipt {r.CouponId} printed on {printer}.");
    }

    /// <summary>Whether the original of this receipt was printed successfully at least once.</summary>
    public async Task<bool> OriginalPrintedAsync(long receiptId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM fiscal.audit_log WHERE action = 'RECEIPT_PRINTED' AND entity = 'receipt'
                           AND entity_id = @id AND details->>'copy' = 'false')
            """, new { id = receiptId.ToString() });
    }

    public async Task<PrintOutcome> TestPrintAsync(long terminalId, string actor, CancellationToken ct = default)
    {
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId);
        if (terminal is null) return new(false, "Unknown workstation.");
        if (!terminal.HasPrinter) return new(false, $"Workstation POS {terminal.PosId} has no printer yet.");
        var printer = Describe(terminal);
        var job = ThermalReceipt.TestPage($"POS {terminal.PosId} · {terminal.OperaTerminalId ?? terminal.Hostname}", printer, terminal.PrinterPaperMm, clock);
        try { await transport.SendAsync(terminal.PrinterKind, terminal.PrinterAddress!, job, ct); }
        catch (PrinterException ex)
        {
            await audit.WriteAsync(actor, AuditLog.Actions.TestPrint, "terminal", terminalId.ToString(), new { printer, ok = false, error = ex.Message }, terminalId, ct);
            return new(false, $"Test print failed: {ex.Message}");
        }
        await audit.WriteAsync(actor, AuditLog.Actions.TestPrint, "terminal", terminalId.ToString(), new { printer, ok = true }, terminalId, ct);
        return new(true, $"Test page sent to {printer}.");
    }

    public static string Describe(TerminalEdit t) => t.PrinterKind switch
    {
        "network" => $"network printer {t.PrinterAddress}",
        "windows" => $"Windows printer \"{t.PrinterAddress}\"",
        _ => "no printer",
    };
}

/// <summary>Receipts waiting to be printed automatically. Fiscalization only adds them here, so a slow or switched-off
/// printer never delays OPERA's answer.</summary>
public sealed class PrintQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long receiptId) => _channel.Writer.TryWrite(receiptId);
    public ChannelReader<long> Reader => _channel.Reader;

    /// <summary>Queues the receipt when receipts are printed on printers and its workstation prints automatically.</summary>
    public async Task EnqueueIfAutomaticAsync(SettingsStore settings, long receiptId, long terminalId, CancellationToken ct = default)
    {
        if (await settings.ReceiptPrintModeAsync(ct) != "printer") return;
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId);
        if (terminal is { HasPrinter: true, PrinterAuto: true }) Enqueue(receiptId);
    }
}

public sealed class PrintQueueService(PrintQueue queue, IServiceScopeFactory scopes, ILogger<PrintQueueService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var receiptId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReceiptPrinting>().PrintAsync(receiptId, "printer", null, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Automatic print of receipt {ReceiptId} failed", receiptId);
            }
        }
    }
}
