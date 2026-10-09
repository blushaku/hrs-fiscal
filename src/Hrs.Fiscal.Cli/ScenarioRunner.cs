using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;
using Hrs.Fiscal.Core.Signing;
using QRCoder;

namespace Hrs.Fiscal.Cli;

public sealed record SentReceipt(DateTime SentAt, PosCoupon Coupon, SignedPayload Payload, string Qr, string Outcome, int? HttpStatus, ulong? TransactionId, string? Message);

public sealed record ScenarioResult(string Name, string Expected, AtkOutcome Outcome, int? HttpStatus, ulong? TransactionId, string? Message, ulong CouponId)
{
    public bool AsExpected => Expected switch
    {
        "accepted" => Outcome == AtkOutcome.Accepted,
        "rejected" => Outcome == AtkOutcome.Rejected,
        _ => true, // "observe": we record what ATK does (e.g. duplicates)
    };
}

/// <summary>
/// Test receipts modelled on hotel folios. Every scenario stores its signed payload and QR (PNG + text)
/// under runs/ so they can be attached to the certification file.
/// </summary>
public sealed class ScenarioRunner(Profile profile, PemSigningKey key, AtkClient atk, string dir, long citizenId = 38344000000L)
{
    private readonly CouponSigner _signer = new(key);
    private string _operator = "HRS test";
    private readonly string _runDir = Path.Combine(dir, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    private ulong _seq = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 900_000_000);

    public static List<ReceiptLine> HotelStay() =>
    [
        new() { Name = "Room night", Unit = "nate", Quantity = 2, UnitPrice = 85.00m, TaxRate = "D", Category = "HT" },
        new() { Name = "Breakfast", Unit = "cope", Quantity = 4, UnitPrice = 9.50m, TaxRate = "E", Category = "UR" },
        new() { Name = "Minibar", Unit = "cope", Quantity = 3, UnitPrice = 2.35m, TaxRate = "E", Category = "UR" },
    ];

    public async Task<List<ScenarioResult>> RunAllAsync()
    {
        var results = new List<ScenarioResult>();

        // 1. Plain hotel folio, card.
        var (first, firstPayload, firstQr) = await SendBuiltAsync("Sale · room + F&B · card", "accepted", HotelStay(), [new(PaymentType.CreditCard, 0)]);
        results.Add(first);

        // 2. Several VAT rates incl. exempt (A) and 0% (C), split payment cash + card.
        results.Add((await SendBuiltAsync("Sale · VAT A/C/D/E · cash + card split", "accepted",
        [
            new() { Name = "Room night", Unit = "nate", Quantity = 1, UnitPrice = 110m, TaxRate = "D", Category = "HT" },
            new() { Name = "City tax", Unit = "nate", Quantity = 2, UnitPrice = 1m, TaxRate = "A", Category = "HT" },
            new() { Name = "Airport transfer", Unit = "cope", Quantity = 1, UnitPrice = 25m, TaxRate = "C", Category = "SUA" },
            new() { Name = "Dinner", Unit = "cope", Quantity = 2, UnitPrice = 18.4m, TaxRate = "E", Category = "UR" },
        ], [new(PaymentType.Cash, 50m), new(PaymentType.CreditCard, 0)])).Result);

        // 3. Line discount.
        results.Add((await SendBuiltAsync("Sale · line discount", "accepted",
            [new() { Name = "Spa treatment", Unit = "cope", Quantity = 1, UnitPrice = 60m, Discount = 12m, TaxRate = "E", Category = "SUA" }],
            [new(PaymentType.CreditCard, 0)])).Result);

        // 4. 4-decimal unit price, fractional quantity.
        results.Add((await SendBuiltAsync("Sale · 4-decimal price, quantity 1.5", "accepted",
            [new() { Name = "Wine by glass", Unit = "cope", Quantity = 1.5m, UnitPrice = 4.3333m, TaxRate = "E", Category = "UR" }],
            [new(PaymentType.Cash, 0)])).Result);

        // 5. Return referring to scenario 1 (minibar correction).
        results.Add((await SendBuiltAsync("Return · references sale #1", "accepted",
            [new() { Name = "Minibar", Unit = "cope", Quantity = 1, UnitPrice = 2.35m, TaxRate = "E", Category = "UR" }],
            [new(PaymentType.CreditCard, 0)], CouponType.Return, first.CouponId)).Result);

        // 6. Exact same payload again (retry after timeout): does ATK return the same transaction or an error?
        results.Add(await PostAsync("Duplicate · identical payload resent", "observe", firstPayload, first.CouponId));

        // 7. Same CouponId, different content: must never be accepted twice.
        var dup = Build(HotelStay(), [new(PaymentType.Cash, 0)], couponId: first.CouponId);
        results.Add(await PostAsync("Duplicate · same CouponId, new content", "observe", _signer.Sign(dup), first.CouponId));

        // 8. Tampered payload: signature no longer matches.
        var tampered = firstPayload with { Details = Convert.ToBase64String(Tamper(firstPayload.Details)) };
        results.Add(await PostAsync("Bad signature · payload changed after signing", "rejected", tampered, first.CouponId));

        // 9. Return without reference (bypassing our own validation) — ATK should reject.
        var noRef = Build([new() { Name = "Minibar", Unit = "cope", Quantity = 1, UnitPrice = 2.35m, TaxRate = "E", Category = "UR" }],
            [new(PaymentType.Cash, 0)]);
        noRef.Type = CouponType.Return;
        results.Add(await PostAsync("Return without ReferenceNo", "rejected", _signer.Sign(noRef), noRef.CouponId));

        // 10. Same lines with the other VAT rounding: does ATK accept/flag a 1-cent difference?
        results.Add((await SendBuiltAsync("Sale · VAT rounding 'truncate net' variant", "observe",
            [new() { Name = "Sandwich", Unit = "cope", Quantity = 1, UnitPrice = 10.50m, TaxRate = "E", Category = "UR" }],
            [new(PaymentType.Cash, 0)], rounding: VatRounding.TruncateNet)).Result);

        // 11. Citizen verification of scenario 1's QR (what the ATK citizen app does).
        results.Add(await VerifyQrAsync("QR · citizen verification of sale #1", firstQr, first.CouponId));

        return results;
    }

    /// <summary>One receipt from the GUI editor. Saves the signed payload, QR and ATK's answer under runs/.</summary>
    public async Task<SentReceipt> SendCustomAsync(List<ReceiptLine> lines, List<ReceiptPayment> payments, CouponType type, ulong reference, string operatorId)
    {
        _operator = operatorId;
        var coupon = Build(lines, payments, type: type, reference: reference);
        var payload = _signer.Sign(coupon);
        var qr = _signer.Sign(CouponBuilder.ToCitizenCoupon(coupon)).ToQrString();
        var r = await atk.SendPosCouponAsync(payload);
        var sent = new SentReceipt(DateTime.Now, coupon, payload, qr, r.Outcome.ToString(), r.HttpStatus, r.TransactionId, r.Message);
        Save(type == CouponType.Return ? "GUI return" : "GUI sale", coupon, payload, qr, sent);
        return sent;
    }

    /// <summary>Asks ATK's citizen endpoint to verify a QR string (what the ATK app does when a guest scans it).</summary>
    public static async Task<(int Status, string Body)> VerifyQrRawAsync(AtkEnvironment env, string qr, long citizenId)
    {
        using var http = new HttpClient { BaseAddress = AtkEndpoints.BaseUri(env), Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.PostAsJsonAsync("citizen/coupon", new { citizen_id = citizenId, qr_code = qr });
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<ScenarioResult> SaleAsync(string name, List<ReceiptLine> lines) =>
        (await SendBuiltAsync(name, "accepted", lines, [new(PaymentType.CreditCard, 0)])).Result;

    private async Task<(ScenarioResult Result, SignedPayload Payload, string Qr)> SendBuiltAsync(string name, string expected,
        List<ReceiptLine> lines, List<ReceiptPayment> payments, CouponType type = CouponType.Sale, ulong reference = 0,
        VatRounding rounding = VatRounding.RoundTaxHalfUp)
    {
        var coupon = Build(lines, payments, type: type, reference: reference, rounding: rounding);
        var payload = _signer.Sign(coupon);
        var qr = _signer.Sign(CouponBuilder.ToCitizenCoupon(coupon)).ToQrString();
        Save(name, coupon, payload, qr);
        return (await PostAsync(name, expected, payload, coupon.CouponId), payload, qr);
    }

    /// <summary>Builds a coupon. A payment amount of 0 means "the rest of the total".</summary>
    private PosCoupon Build(List<ReceiptLine> lines, List<ReceiptPayment> payments, ulong? couponId = null,
        CouponType type = CouponType.Sale, ulong reference = 0, VatRounding rounding = VatRounding.RoundTaxHalfUp)
    {
        var builder = new CouponBuilder(rounding: rounding);
        var probe = builder.Build(Request(lines, [new(PaymentType.Cash, 1m)], 1, CouponType.Sale, 0, skipPaymentCheck: true));
        var total = probe.Total / 100m;
        var fixedSum = payments.Where(p => p.Amount > 0).Sum(p => p.Amount);
        var resolved = payments.Select(p => p.Amount > 0 ? p : p with { Amount = total - fixedSum }).ToList();
        return builder.Build(Request(lines, resolved, couponId ?? NextCouponId(), type, reference));
    }

    private ReceiptRequest Request(List<ReceiptLine> lines, List<ReceiptPayment> payments, ulong couponId, CouponType type, ulong reference,
        bool skipPaymentCheck = false) => new()
    {
        BusinessId = profile.Nui, BranchId = profile.BranchId, PosId = profile.PosId, ApplicationId = profile.ApplicationId,
        CouponId = couponId, VerificationNo = VerificationNumber.New(), Location = profile.Location, OperatorId = _operator,
        IssuedAt = DateTimeOffset.UtcNow, Type = type, ReferenceNo = reference, Lines = lines,
        Payments = skipPaymentCheck ? [new ReceiptPayment(PaymentType.Cash, lines.Sum(l => l.UnitPrice * l.Quantity - l.Discount))] : payments,
    };

    private ulong NextCouponId() => profile.BranchId * 10_000_000_000UL + 9_000_000_000UL + ++_seq;

    private async Task<ScenarioResult> PostAsync(string name, string expected, SignedPayload payload, ulong couponId)
    {
        var r = await atk.SendPosCouponAsync(payload);
        Console.WriteLine($"[{r.Outcome,-9}] {name} · HTTP {r.HttpStatus} · tx {r.TransactionId} · {r.Message}");
        return new ScenarioResult(name, expected, r.Outcome, r.HttpStatus, r.TransactionId, r.Message, couponId);
    }

    private async Task<ScenarioResult> VerifyQrAsync(string name, string qr, ulong couponId)
    {
        using var http = new HttpClient { BaseAddress = AtkEndpoints.BaseUri(profile.Environment), Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.PostAsJsonAsync("citizen/coupon", new { citizen_id = citizenId, qr_code = qr }); // citizen_id must be a number: ATK rejects a string although Swagger says string
        var body = await response.Content.ReadAsStringAsync();
        var ok = response.IsSuccessStatusCode;
        Console.WriteLine($"[{(ok ? "Accepted" : "Rejected"),-9}] {name} · HTTP {(int)response.StatusCode} · {Trim(body)}");
        return new ScenarioResult(name, "accepted", ok ? AtkOutcome.Accepted : AtkOutcome.Rejected, (int)response.StatusCode, null, Trim(body), couponId);
    }

    private void Save(string name, PosCoupon coupon, SignedPayload payload, string qr, SentReceipt? result = null)
    {
        Directory.CreateDirectory(_runDir);
        var stem = Path.Combine(_runDir, $"{coupon.CouponId}");
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
        {
            scenario = name,
            coupon = JsonFormatter.Default.Format(coupon),
            details = payload.Details,
            signature = payload.Signature,
            qr,
            atk = result is null ? null : new { result.Outcome, result.HttpStatus, transactionId = result.TransactionId?.ToString(), result.Message, sentAt = result.SentAt },
        }, new JsonSerializerOptions { WriteIndented = true }));
        using var data = QRCodeGenerator.GenerateQrCode(qr, QRCodeGenerator.ECCLevel.M);
        File.WriteAllBytes(stem + "-qr.png", new PngByteQRCode(data).GetGraphic(6));
    }

    private static byte[] Tamper(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        bytes[^1] ^= 0x01;
        return bytes;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
