using Dapper;
using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;
using Hrs.Fiscal.Core.Signing;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Npgsql;

namespace Hrs.Fiscal.Server.Services;

/// <summary>A line of a receipt written from the special paper block.</summary>
public sealed record PaperLine(string Name, decimal Quantity, decimal UnitPrice, string TaxRate, string Unit, string Category);

/// <summary>Result of a receipt issued from the admin interface.</summary>
public sealed record ManualReceiptResult(long ReceiptId, long CouponId, AtkOutcome Outcome, string? AtkMessage);

/// <summary>
/// Receipts issued from the admin interface rather than from OPERA:
/// <list type="bullet">
/// <item><b>Cancel</b> (Doc. 01-06-1967 Art 25.11): a "Kupon i anulimit" with the reason, linked to the original sale,
/// for an error found after the receipt was issued. It repeats the original's lines and amounts.</item>
/// <item><b>Paper block</b> (Art 25.16, UA Art 45): receipts written by hand from the special paper block while the
/// system was down are entered afterwards, signed and sent; the paper serial is kept with the receipt.</item>
/// </list>
/// Both are signed with the workstation's key (central signing), archived before sending and sent to ATK like any receipt.
/// </summary>
public sealed class ManualReceipts(
    NpgsqlDataSource db, SettingsStore settings, ReceiptArchive archive, ReceiptQueries receipts, AuditLog audit,
    TerminalEnrollment enrollment, IAtkClientFactory atkFactory, PropertyClock clock)
{
    /// <summary>Paper-block receipts must be entered within 48 hours + 5 days of the failure (UA Art 45.5–45.6).</summary>
    public static readonly TimeSpan PaperDeadline = TimeSpan.FromDays(7);

    /// <summary>Why this receipt cannot be cancelled, or null if it can.</summary>
    public async Task<string?> CannotCancelAsync(ReceiptDetail r, CancellationToken ct = default)
    {
        if (r.CouponType != 1) return "Only a sale receipt can be cancelled.";
        if (r.Status == "rejected") return "ATK rejected this receipt; there is nothing to cancel at ATK.";
        await using var conn = await db.OpenConnectionAsync(ct);
        var refs = (await conn.QueryAsync<(long CouponId, short Type)>(
            "SELECT coupon_id, coupon_type FROM fiscal.receipt WHERE reference_coupon_id = @c", new { c = r.CouponId })).ToList();
        if (refs.Any(x => x.Type == 2)) return $"Already cancelled by receipt {refs.First(x => x.Type == 2).CouponId}.";
        if (refs.Any(x => x.Type == 3)) return $"A return ({refs.First(x => x.Type == 3).CouponId}) already refers to this receipt; correct the rest with a return in OPERA.";
        var business = await settings.BusinessAsync(ct);
        if (business is null || business.Nui != r.BusinessNui || business.BranchId != r.BranchId)
            return "This receipt belongs to a previous taxpayer or unit.";
        return null;
    }

    public async Task<ManualReceiptResult> CancelAsync(long receiptId, string reason, string actor, string operatorName, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length < 3) throw new FiscalValidationException("Enter the reason for the cancellation.");
        if (reason.Length > 200) throw new FiscalValidationException("The reason can have at most 200 characters.");
        var original = await receipts.GetAsync(receiptId, ct) ?? throw new FiscalValidationException("Receipt not found.");
        if (await CannotCancelAsync(original, ct) is { } why) throw new FiscalValidationException(why);

        var (terminal, applicationId) = await TerminalForAsync(original.PosId, ct);
        var source = PosCoupon.Parser.ParseFrom(original.PosCoupon);
        var coupon = source.Clone();                       // same lines, VAT groups, payments and totals as the original
        coupon.CouponId = (ulong)await archive.NextCouponIdAsync(original.BranchId, ct);
        coupon.VerificationNo = VerificationNumber.New();
        coupon.Type = CouponType.Cancel;
        coupon.ReferenceNo = source.CouponId;
        coupon.Time = clock.Time.GetUtcNow().ToUnixTimeSeconds();
        coupon.OperatorId = operatorName;
        coupon.ApplicationId = (ulong)applicationId;
        coupon.TransactionNo = 0;

        var result = await SignArchiveSendAsync(coupon, terminal, original.SourceDocument, new ReceiptExtras(CancelReason: reason), ct);
        await audit.WriteAsync(actor, AuditLog.Actions.ReceiptCancelled, "receipt", result.ReceiptId.ToString(), new
        {
            cancels = original.CouponId, couponId = result.CouponId, reason, total = coupon.Total, atk = result.Outcome.ToString().ToLowerInvariant(),
        }, terminal.Id, ct);
        return result;
    }

    public async Task<ManualReceiptResult> EnterPaperAsync(long posId, string serial, DateTime issuedLocal, string operatorName,
        IReadOnlyList<PaperLine> lines, PaymentType payment, string actor, CancellationToken ct = default)
    {
        serial = (serial ?? "").Trim();
        if (serial.Length == 0) throw new FiscalValidationException("Enter the serial number printed on the paper receipt.");
        var issuedUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(issuedLocal, DateTimeKind.Unspecified), clock.TimeZone);
        var now = clock.Time.GetUtcNow().UtcDateTime;
        if (issuedUtc > now.AddMinutes(5)) throw new FiscalValidationException("The paper receipt's date and time are in the future.");
        if (lines.Count == 0) throw new FiscalValidationException("Enter at least one line.");
        var business = await settings.BusinessAsync(ct) ?? throw new FiscalValidationException("Business is not set up (Settings › Business).");
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            var existing = await conn.ExecuteScalarAsync<long?>(
                "SELECT coupon_id FROM fiscal.receipt WHERE business_nui = @nui AND paper_serial = @serial", new { nui = business.Nui, serial });
            if (existing is not null) throw new FiscalValidationException($"Paper receipt {serial} was already entered (receipt {existing}).");
        }
        var (terminal, applicationId) = await TerminalForAsync(posId, ct);
        var rates = (await settings.VatRatesAsync(ct)).ToDictionary(v => v.Letter, v => v.Percent);
        var rounding = Enum.TryParse<VatRounding>(await settings.GetAsync("vat_rounding", "RoundTaxHalfUp", ct), out var r) ? r : VatRounding.RoundTaxHalfUp;
        var request = new ReceiptRequest
        {
            BusinessId = (ulong)business.Nui,
            BranchId = (ulong)business.BranchId,
            PosId = (ulong)terminal.PosId,
            ApplicationId = (ulong)applicationId,
            CouponId = (ulong)await archive.NextCouponIdAsync(business.BranchId, ct),
            VerificationNo = VerificationNumber.New(),
            Location = business.Location,
            OperatorId = operatorName,
            IssuedAt = new DateTimeOffset(DateTime.SpecifyKind(issuedUtc, DateTimeKind.Utc)),
            Type = CouponType.Sale,
            Lines = lines.Select(l => new ReceiptLine
            {
                Name = l.Name.Trim(), Unit = string.IsNullOrWhiteSpace(l.Unit) ? "cope" : l.Unit.Trim(), Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, TaxRate = l.TaxRate, Category = string.IsNullOrWhiteSpace(l.Category) ? "TT" : l.Category.Trim(),
            }).ToList(),
            Payments = [],
        };
        var table = new TaxRateTable(rates);
        var builder = new CouponBuilder(table, rounding);
        var total = builder.TotalCents(request with { Payments = [] });
        if (total <= 0) throw new FiscalValidationException("The receipt total must be more than zero.");
        var built = builder.Build(request with { Payments = [new ReceiptPayment(payment, total / 100m)] });
        var findings = CouponVerifier.Verify(built, table).ToList();
        if (findings.Count > 0) throw new FiscalValidationException(findings);

        var result = await SignArchiveSendAsync(built, terminal, $"Blloku {serial}", new ReceiptExtras(PaperSerial: serial), ct);
        await audit.WriteAsync(actor, AuditLog.Actions.PaperReceiptEntered, "receipt", result.ReceiptId.ToString(), new
        {
            serial, couponId = result.CouponId, issued = clock.Format(issuedUtc), total = built.Total, late = now - issuedUtc > PaperDeadline,
            atk = result.Outcome.ToString().ToLowerInvariant(),
        }, terminal.Id, ct);
        return result;
    }

    private async Task<(TerminalEdit Terminal, long ApplicationId)> TerminalForAsync(long posId, CancellationToken ct)
    {
        var terminal = (await settings.TerminalsAsync(ct)).FirstOrDefault(t => t.PosId == posId)
                       ?? throw new FiscalValidationException($"Workstation POS {posId} is not set up.");
        var defaultMode = await settings.SigningModeDefaultAsync(ct);
        if (terminal.EffectiveMode(defaultMode) != SigningModes.Server)
            throw new FiscalValidationException($"Workstation POS {posId} is set to workstation-client signing, which is not available yet.");
        if (terminal.SigningState(defaultMode) is var state && state != TerminalSigningState.Ready)
            throw new FiscalValidationException($"Workstation POS {posId} cannot sign ({state}). Register it with ATK under Settings › Workstations.");
        var applicationId = await settings.GetAsync<long>("atk_application_id", 0, ct);
        if (applicationId <= 0) throw new FiscalValidationException("ATK Application ID is not set (Settings › Business & unit).");
        return (terminal, applicationId);
    }

    private async Task<ManualReceiptResult> SignArchiveSendAsync(PosCoupon coupon, TerminalEdit terminal, string? document, ReceiptExtras extras, CancellationToken ct)
    {
        var key = await enrollment.OpenKeyAsync(terminal.Id, ct);
        using var keyLifetime = key as IDisposable;
        var signer = new CouponSigner(key);
        var signed = signer.Sign(coupon);
        var qr = signer.Sign(CouponBuilder.ToCitizenCoupon(coupon)).ToQrString();
        var receiptId = await archive.AppendReceiptAsync(coupon, signed, qr, terminal.Id, false, null, document, ct, extras);

        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        var timeout = TimeSpan.FromSeconds(await settings.GetAsync("atk_timeout_seconds", 10, ct));
        var result = await atkFactory.Create(environment, timeout).SendPosCouponAsync(signed, ct);
        await archive.AppendTransmissionAsync(receiptId, "server", result, ct: ct);
        return new ManualReceiptResult(receiptId, (long)coupon.CouponId, result.Outcome, result.Message);
    }
}
