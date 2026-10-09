using System.Text.Json;
using Dapper;
using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;
using Hrs.Fiscal.Core.Signing;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Npgsql;

namespace Hrs.Fiscal.Server.Flip;

/// <summary>Answer to FLIP for one folio. Provisional JSON shape until Oracle's partner response specification is known.</summary>
public sealed record FlipResult
{
    public required string Status { get; init; }            // OK | ERROR | NOT_FISCAL
    public string? Message { get; init; }
    public string? FiscalFolioId { get; init; }
    public string? FiscalBillNo { get; init; }              // receipt number (ATK CouponId)
    public string? VerificationNo { get; init; }            // NUIKF
    public string? SefId { get; init; }                     // unit-NUI-POS
    public string? AtkStatus { get; init; }                 // accepted | pending (sent later, max 48 h) | rejected
    public string? AtkTransactionId { get; init; }
    public string? QrCode { get; init; }                    // content of the QR printed on the folio
    public string? IssuedAt { get; init; }
    public long? TotalCents { get; init; }
    public bool Duplicate { get; init; }
    public bool TestEnvironment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public long? ReceiptId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public int HttpStatus { get; init; } = 200;

    public static FlipResult Error(string message, string? folioId = null, int http = 422) =>
        new() { Status = "ERROR", Message = message, FiscalFolioId = folioId, HttpStatus = http };
}

/// <summary>
/// Live mode: OPERA folio (via FLIP) → ATK receipt. Idempotent per OPERA FiscalFolioId: a resent folio returns the
/// receipt issued the first time. The receipt is archived before it is sent; if ATK is unreachable it stays pending
/// and <see cref="AtkResendService"/> sends it later (ATK allows 48 hours).
/// </summary>
public sealed class FlipFiscalizer(
    NpgsqlDataSource db, SettingsStore settings, ReceiptArchive archive, AuditLog audit, TerminalEnrollment enrollment,
    IAtkClientFactory atkFactory, PropertyClock clock, ILogger<FlipFiscalizer> log)
{
    public const string Source = "OFIS";

    public async Task<FlipResult> ProcessAsync(byte[] body, CancellationToken ct = default)
    {
        OperaFolio folio;
        var json = System.Text.Encoding.UTF8.GetString(body);
        try { folio = OperaPayload.Parse(json); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return FlipResult.Error("Not an OPERA fiscal payload: " + ex.Message, http: 400);
        }

        var eventId = $"{folio.HotelCode}:{folio.FiscalFolioId}";
        if (await ExistingAsync(eventId, ct) is { } existing) return existing;

        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        try
        {
            if (folio.Lines.Count == 0 && folio.Payments.Count == 0)
                return new FlipResult { Status = "NOT_FISCAL", Message = "Folio has no charges: nothing to fiscalize.", FiscalFolioId = folio.FiscalFolioId };
            if (!string.Equals(folio.LocalCurrency, "EUR", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(folio.LocalCurrency)
                && environment == AtkEnvironment.Production)
                return FlipResult.Error($"Folio currency is {folio.LocalCurrency}; Kosovo fiscal receipts must be in EUR.", folio.FiscalFolioId);

            var business = await settings.BusinessAsync(ct) ?? throw new FiscalValidationException("Business is not set up (Settings › Business).");
            // 1. Is this folio for this property, and do OPERA's own numbers add up?
            var validation = new ValidationOptions(
                await settings.GetAsync("validation_check_tax_number", true, ct),
                Math.Clamp(await settings.GetAsync("validation_tolerance", 0.01m, ct), 0m, 1m));
            var findings = FolioValidator.Check(folio, business, validation);
            if (findings.Count > 0) throw new FiscalValidationException(findings);
            var terminal = (await settings.TerminalsAsync(ct)).FirstOrDefault(t => string.Equals(t.OperaTerminalId, folio.TerminalId, StringComparison.OrdinalIgnoreCase))
                           ?? throw new FiscalValidationException($"OPERA terminal '{folio.TerminalId}' is not set up as a workstation in HRS (Settings › Workstations › OPERA Fiscal Terminal ID).");
            var defaultMode = await settings.SigningModeDefaultAsync(ct);
            if (terminal.EffectiveMode(defaultMode) != SigningModes.Server)
                throw new FiscalValidationException($"Workstation POS {terminal.PosId} is set to workstation-client signing, which is not available yet. Set it to central signing (Settings › Workstations).");
            var state = terminal.SigningState(defaultMode);
            if (state != TerminalSigningState.Ready)
                throw new FiscalValidationException($"Workstation POS {terminal.PosId} cannot sign ({state}). Register it with ATK under Settings › Workstations.");
            var applicationId = await settings.GetAsync<long>("atk_application_id", 0, ct);
            if (applicationId <= 0) throw new FiscalValidationException("ATK Application ID is not set (Settings › General).");

            var request = await BuildRequestAsync(folio, business, terminal, (ulong)applicationId, ct);
            var rates = (await settings.VatRatesAsync(ct)).ToDictionary(v => v.Letter, v => v.Percent);
            var rounding = Enum.TryParse<VatRounding>(await settings.GetAsync("vat_rounding", "RoundTaxHalfUp", ct), out var r) ? r : VatRounding.RoundTaxHalfUp;
            var coupon = new CouponBuilder(new TaxRateTable(rates), rounding).Build(request);
            // 2. The receipt itself must add up, and its VAT must match what OPERA calculated (unless overrides change VAT).
            var receiptFindings = CouponVerifier.Verify(coupon, new TaxRateTable(rates)).ToList();
            if (!await settings.OperaOverridesEnabledAsync(ct))
                receiptFindings.AddRange(FolioValidator.CompareWithReceipt(folio, coupon, await settings.VatRatesAsync(ct), validation.Tolerance));
            if (receiptFindings.Count > 0) throw new FiscalValidationException(receiptFindings);

            var key = await enrollment.OpenKeyAsync(terminal.Id, ct);
            using var keyLifetime = key as IDisposable;
            var signer = new CouponSigner(key);
            var signed = signer.Sign(coupon);
            var qr = signer.Sign(CouponBuilder.ToCitizenCoupon(coupon)).ToQrString();

            // Archive first: the payload exactly as received, then the signed receipt.
            long payloadId;
            try { payloadId = await archive.AppendSourcePayloadAsync(Source, eventId, "application/json", json, ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return await ExistingAsync(eventId, ct) ?? FlipResult.Error("Folio is being processed already; retry.", folio.FiscalFolioId, 409);
            }
            var receiptId = await archive.AppendReceiptAsync(coupon, signed, qr, terminal.Id, false, payloadId, folio.FolioReference, ct);
            await audit.WriteAsync("flip", AuditLog.Actions.ReceiptIssued, "receipt", receiptId.ToString(), new
            {
                couponId = coupon.CouponId, folio = folio.FiscalFolioId, opera_terminal = folio.TerminalId, posId = terminal.PosId,
                type = coupon.Type.ToString(), total = coupon.Total, command = folio.Command, currency = folio.LocalCurrency,
            }, terminal.Id, ct);

            var timeout = TimeSpan.FromSeconds(await settings.GetAsync("atk_timeout_seconds", 10, ct));
            var result = await atkFactory.Create(environment, timeout).SendPosCouponAsync(signed, ct);
            await archive.AppendTransmissionAsync(receiptId, "server", result, ct: ct);
            await audit.WriteAsync("flip", result.Outcome switch
            {
                AtkOutcome.Accepted => AuditLog.Actions.ReceiptAccepted,
                AtkOutcome.Rejected => AuditLog.Actions.ReceiptRejected,
                _ => AuditLog.Actions.QueuedOffline,
            }, "receipt", receiptId.ToString(), new { result.HttpStatus, result.Message, transaction = result.TransactionId?.ToString() }, terminal.Id, ct);

            var view = await ViewAsync(receiptId, folio.FiscalFolioId, environment, ct);
            if (result.Outcome == AtkOutcome.Rejected)
                return view with { Status = "ERROR", Message = "ATK rejected the receipt: " + result.Message, HttpStatus = 422 };
            return view with
            {
                Message = result.Outcome == AtkOutcome.Accepted ? "Fiscalized." : "Fiscalized offline; HRS sends it to ATK automatically within 48 hours.",
            };
        }
        catch (FiscalValidationException ex)
        {
            var message = string.Join(" · ", ex.Errors);
            log.LogWarning("Folio {Folio} not fiscalized: {Message}", folio.FiscalFolioId, message);
            await audit.WriteAsync("flip", AuditLog.Actions.FolioRefused, "folio", folio.FiscalFolioId,
                new { opera_terminal = folio.TerminalId, folio.Command, reason = message });
            return FlipResult.Error(message, folio.FiscalFolioId);
        }
    }

    private async Task<ReceiptRequest> BuildRequestAsync(OperaFolio folio, BusinessInfo business, TerminalEdit terminal, ulong applicationId, CancellationToken ct)
    {
        var overrides = await settings.OperaOverridesEnabledAsync(ct);
        var trxMap = overrides ? (await settings.TrxMappingsAsync(ct)).Where(m => m.Active).ToDictionary(m => m.TrxCode) : [];
        var payMap = overrides ? (await settings.PaymentMappingsAsync(ct)).ToDictionary(m => m.PaymentCode) : [];
        var vat = await settings.VatRatesAsync(ct);
        var defaultCategory = await settings.GetAsync("default_item_category", "TT", ct) ?? "TT";
        var defaultUnit = await settings.GetAsync("default_item_unit", "cope", ct) ?? "cope";

        // Credit folio (negative total) = return of an earlier receipt.
        var isReturn = folio.Gross < 0;
        var sign = isReturn ? -1m : 1m;
        ulong reference = 0;
        if (isReturn)
        {
            if (!ulong.TryParse(folio.AssociatedFiscalBillNo, out reference) || reference == 0)
                throw new FiscalValidationException("Credit folio without the original receipt number (FLIP_ASSOCIATED_FISCAL_BILL_NO).");
        }

        var errors = new List<string>();
        var lines = new List<ReceiptLine>();
        foreach (var l in folio.Lines)
        {
            trxMap.TryGetValue(l.TrxCode, out var m);
            var letter = m?.VatLetter ?? LetterFor(l.VatPercent, vat);
            if (letter is null) { errors.Add($"{l.Description} ({l.TrxCode}): VAT {l.VatPercent:0.##}% does not match any VAT rate ({string.Join(", ", vat.Select(v => $"{v.Letter} {v.Percent:0.##}%"))})."); continue; }
            var gross = l.Gross * sign;
            lines.Add(new ReceiptLine
            {
                Name = m?.ItemName is { Length: > 0 } n ? n : l.Description,
                Unit = m?.Unit is { Length: > 0 } u ? u : defaultUnit,
                Quantity = gross < 0 ? 1 : l.Quantity,
                UnitPrice = gross < 0 ? gross : decimal.Round(gross / l.Quantity, 4, MidpointRounding.AwayFromZero),
                TaxRate = letter,
                Category = m?.Category is { Length: > 0 } c ? c : defaultCategory,
            });
        }
        if (errors.Count > 0) throw new FiscalValidationException(errors);

        // Corrections inside a folio (negative lines) become discounts on lines with the same VAT letter.
        var positive = lines.Where(l => l.UnitPrice >= 0).ToList();
        foreach (var neg in lines.Where(l => l.UnitPrice < 0))
        {
            var remaining = -neg.UnitPrice;
            foreach (var target in positive.Where(p => p.TaxRate == neg.TaxRate).OrderByDescending(p => p.UnitPrice * p.Quantity).ToList())
            {
                var room = target.UnitPrice * target.Quantity - target.Discount;
                var take = Math.Min(room, remaining);
                if (take <= 0) continue;
                positive[positive.IndexOf(target)] = target with { Discount = target.Discount + take };
                remaining -= take;
                if (remaining == 0) break;
            }
            if (remaining > 0) throw new FiscalValidationException($"Correction '{neg.Name}' ({-neg.UnitPrice:0.00}) is larger than the charges with VAT {neg.TaxRate} on this folio.");
        }

        var payments = folio.Payments
            .GroupBy(p => PaymentTypeFor(p, payMap))
            .Select(g => new ReceiptPayment(g.Key, g.Sum(p => p.Amount) * sign))
            .Where(p => p.Amount != 0)
            .ToList();

        return new ReceiptRequest
        {
            BusinessId = (ulong)business.Nui,
            BranchId = (ulong)business.BranchId,
            PosId = (ulong)terminal.PosId,
            ApplicationId = applicationId,
            CouponId = (ulong)await archive.NextCouponIdAsync(business.BranchId, ct),
            VerificationNo = VerificationNumber.New(),
            Location = business.Location,
            OperatorId = string.IsNullOrWhiteSpace(folio.Operator) ? "OPERA" : folio.Operator!,
            IssuedAt = clock.Time.GetUtcNow(),
            Type = isReturn ? CouponType.Return : CouponType.Sale,
            ReferenceNo = reference,
            Lines = positive,
            Payments = payments,
        };
    }

    /// <summary>VAT letter for a percentage. 0 % → C (zero rate); exempt (A) only through an override.</summary>
    public static string? LetterFor(decimal percent, IReadOnlyList<VatRateRow> rates)
    {
        if (percent == 0 && rates.Any(r => r.Letter == "C" && r.Percent == 0)) return "C";
        return rates.Where(r => r.Percent == percent && r.Letter != "A").Select(r => r.Letter).FirstOrDefault()
               ?? rates.Where(r => r.Percent == percent).Select(r => r.Letter).FirstOrDefault();
    }

    /// <summary>Override table (when switched on) → payment name → Other.</summary>
    public static PaymentType PaymentTypeFor(OperaPayment p, IReadOnlyDictionary<string, PaymentMapping> map)
    {
        if (map.TryGetValue(p.TrxCode, out var m) && Enum.IsDefined(typeof(PaymentType), (int)m.AtkPaymentType)) return (PaymentType)m.AtkPaymentType;
        var d = p.Description.ToUpperInvariant();
        if (new[] { "CASH", "KESH", "PARA", "GOTOV", "BAR" }.Any(d.Contains)) return PaymentType.Cash;
        if (new[] { "CARD", "KART", "VISA", "MASTER", "MAESTRO", "AMEX", "AMERICAN EXPRESS", "DINERS", "DISCOVER", "JCB", "UNION" }.Any(d.Contains)) return PaymentType.CreditCard;
        if (new[] { "VOUCHER", "KUPON" }.Any(d.Contains)) return PaymentType.Voucher;
        if (new[] { "CHEQUE", "CHECK", "CEK" }.Any(d.Contains)) return PaymentType.Cheque;
        return PaymentType.Other;
    }

    private async Task<FlipResult?> ExistingAsync(string eventId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var receiptId = await conn.ExecuteScalarAsync<long?>("""
            SELECT r.id FROM fiscal.receipt r JOIN fiscal.source_payload s ON s.id = r.source_payload_id
            WHERE s.source = @Source AND s.source_event_id = @eventId ORDER BY r.id LIMIT 1
            """, new { Source, eventId });
        if (receiptId is null) return null;
        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        var view = await ViewAsync(receiptId.Value, eventId.Split(':').Last(), environment, ct);
        return view with { Duplicate = true, Message = "Already fiscalized; returning the original receipt." };
    }

    private async Task<FlipResult> ViewAsync(long receiptId, string folioId, AtkEnvironment environment, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<(long CouponId, string VerificationNo, long BranchId, long BusinessNui, long PosId, string QrString,
            DateTime IssuedAt, long TotalCents, string Status, decimal? AtkTransactionId)>("""
            SELECT r.coupon_id, r.verification_no, r.branch_id, r.business_nui, r.pos_id, r.qr_string, r.issued_at, r.total_cents,
                   s.status, s.atk_transaction_id
            FROM fiscal.receipt r JOIN fiscal.receipt_status s ON s.id = r.id WHERE r.id = @receiptId
            """, new { receiptId });
        return new FlipResult
        {
            Status = row.Status == "rejected" ? "ERROR" : "OK",
            FiscalFolioId = folioId,
            FiscalBillNo = row.CouponId.ToString(),
            VerificationNo = row.VerificationNo,
            SefId = $"{row.BranchId}-{row.BusinessNui}-{row.PosId}",
            AtkStatus = row.Status,
            AtkTransactionId = row.AtkTransactionId?.ToString("0"),
            QrCode = row.QrString,
            IssuedAt = clock.ToLocal(DateTime.SpecifyKind(row.IssuedAt, DateTimeKind.Utc)).ToString("yyyy-MM-ddTHH:mm:ss"),
            TotalCents = row.TotalCents,
            TestEnvironment = environment == AtkEnvironment.Test,
            ReceiptId = receiptId,
            HttpStatus = row.Status == "rejected" ? 422 : 200,
        };
    }
}
