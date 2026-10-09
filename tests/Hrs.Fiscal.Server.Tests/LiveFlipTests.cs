using System.Net;
using System.Text;
using System.Text.Json;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Flip;
using Hrs.Fiscal.Server.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

public class OperaPayloadTests
{
    private static string Sample() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "opera-checkout-sample.json"));

    [Fact]
    public void Parses_the_captured_OPERA_checkout_payload()
    {
        var f = OperaPayload.Parse(Sample());
        Assert.Equal("67838", f.FiscalFolioId);
        Assert.Equal("OPERA9TERMINAL", f.TerminalId);
        Assert.Equal("CHECKOUT", f.Command);
        Assert.Equal("ALL", f.LocalCurrency);
        var line = Assert.Single(f.Lines);                     // the generated VAT posting is not a line
        Assert.Equal(("100000", "Accommodation", 1m, 19000m, 6m), (line.TrxCode, line.Description, line.Quantity, line.Gross, line.VatPercent));
        var pay = Assert.Single(f.Payments);
        Assert.Equal(("900000", "Cash", 19000m), (pay.TrxCode, pay.Description, pay.Amount));
        Assert.Equal(f.Gross, f.Paid);
        Assert.Equal("TEST.USER", f.Operator);
    }

    [Fact]
    public void Vat_letter_and_payment_type_rules()
    {
        var rates = new List<VatRateRow> { new() { Letter = "A", Percent = 0 }, new() { Letter = "C", Percent = 0 }, new() { Letter = "D", Percent = 8 }, new() { Letter = "E", Percent = 18 } };
        Assert.Equal("C", FlipFiscalizer.LetterFor(0, rates));
        Assert.Equal("D", FlipFiscalizer.LetterFor(8, rates));
        Assert.Equal("E", FlipFiscalizer.LetterFor(18, rates));
        Assert.Null(FlipFiscalizer.LetterFor(6, rates));
        var none = new Dictionary<string, PaymentMapping>();
        Assert.Equal(PaymentType.Cash, FlipFiscalizer.PaymentTypeFor(new OperaPayment("9000", "Cash", 1), none));
        Assert.Equal(PaymentType.CreditCard, FlipFiscalizer.PaymentTypeFor(new OperaPayment("9004", "Visa Card", 1), none));
        Assert.Equal(PaymentType.Other, FlipFiscalizer.PaymentTypeFor(new OperaPayment("9100", "City Ledger", 1), none));
        var map = new Dictionary<string, PaymentMapping> { ["9100"] = new() { PaymentCode = "9100", AtkPaymentType = 2 } };
        Assert.Equal(PaymentType.CreditCard, FlipFiscalizer.PaymentTypeFor(new OperaPayment("9100", "City Ledger", 1), map));
    }
}

public class LiveFlipTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private static string Sample() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "opera-checkout-sample.json"));

    private static string WithFolio(string json, string folioId, string terminal = "OPERA9TERMINAL") =>
        json.Replace("\"FiscalFolioId\": \"67838\"", $"\"FiscalFolioId\": \"{folioId}\"").Replace("\"OPERA9TERMINAL\"", $"\"{terminal}\"");

    private async Task<long> SetUpAsync()
    {
        using var scope = app.Services.CreateScope();
        var s = scope.ServiceProvider;
        var settings = s.GetRequiredService<SettingsStore>();
        if ((await settings.TerminalsAsync()).Any(t => t.OperaTerminalId == "OPERA9TERMINAL"))
            return (await settings.TerminalsAsync()).Single(t => t.OperaTerminalId == "OPERA9TERMINAL").Id;
        await settings.SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = 123456789L, ["flip_mode"] = "live" }, "test");
        // The demo property is in Albania (6 % VAT): map 6 % to letter D for this test.
        await settings.SaveVatRateAsync(new VatRateRow { Letter = "D", Percent = 6, Description = "test" }, "test");
        await settings.SaveTerminalAsync(new TerminalEdit { PosId = 21, OperaTerminalId = "OPERA9TERMINAL", Hostname = "OPERA9", Status = "pending", SigningMode = "server" }, "test");
        var id = (await settings.TerminalsAsync()).Single(t => t.OperaTerminalId == "OPERA9TERMINAL").Id;
        await s.GetRequiredService<TerminalEnrollment>().EnrollAsync(id, "test");
        return id;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string json)
    {
        var response = await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(json, Encoding.UTF8, "application/json"));
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [PgFact]
    public async Task Folio_is_fiscalized_archived_and_answered_once()
    {
        await SetUpAsync();
        var json = WithFolio(Sample(), "70001");
        var (status, body) = await PostAsync(json);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", body.GetProperty("Status").GetString());
        Assert.Equal("accepted", body.GetProperty("AtkStatus").GetString());
        Assert.Contains("|", body.GetProperty("QrCode").GetString());
        Assert.Equal(16, body.GetProperty("VerificationNo").GetString()!.Length);
        var couponId = body.GetProperty("FiscalBillNo").GetString();

        // Archive: original payload, receipt and transmission; the FLIP message points at the receipt.
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.source_payload WHERE source = 'OFIS' AND source_event_id = 'DEMO1:70001'"));
        Assert.Equal(1900000L, await app.ScalarAsync<long>($"SELECT total_cents FROM fiscal.receipt WHERE coupon_id = {couponId}"));
        Assert.Equal("accepted", await app.ScalarAsync<string>($"SELECT s.status FROM fiscal.receipt_status s WHERE s.coupon_id = {couponId}"));
        Assert.Equal(couponId, (await app.ScalarAsync<long>("SELECT r.coupon_id FROM fiscal.flip_message m JOIN fiscal.receipt r ON r.id = m.receipt_id ORDER BY m.id DESC LIMIT 1")).ToString());

        // Same folio again (reprint / retry): same receipt, nothing new sent to ATK.
        var sent = app.FakeAtk.CouponCount;
        var (status2, again) = await PostAsync(json);
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.True(again.GetProperty("Duplicate").GetBoolean());
        Assert.Equal(couponId, again.GetProperty("FiscalBillNo").GetString());
        Assert.Equal(sent, app.FakeAtk.CouponCount);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.receipt r JOIN fiscal.source_payload s ON s.id = r.source_payload_id WHERE s.source_event_id = 'DEMO1:70001'"));
    }

    [PgFact]
    public async Task Unknown_terminal_is_refused_with_a_reason()
    {
        await SetUpAsync();
        var (status, body) = await PostAsync(WithFolio(Sample(), "70002", "NOT-SET-UP"));
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Equal("ERROR", body.GetProperty("Status").GetString());
        Assert.Contains("NOT-SET-UP", body.GetProperty("Message").GetString());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'FOLIO_REFUSED' AND entity_id = '70002'"));
    }

    [PgFact]
    public async Task Atk_offline_receipt_is_issued_and_sent_later()
    {
        await SetUpAsync();
        app.FakeAtk.Offline = true;
        try
        {
            var (status, body) = await PostAsync(WithFolio(Sample(), "70003"));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("pending", body.GetProperty("AtkStatus").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("QrCode").GetString()));
        }
        finally { app.FakeAtk.Offline = false; }

        using var scope = app.Services.CreateScope();
        Assert.True(await AtkResendService.ResendPendingAsync(scope.ServiceProvider, CancellationToken.None) >= 1);
        Assert.Equal("accepted", await app.ScalarAsync<string>(
            "SELECT s.status FROM fiscal.receipt_status s JOIN fiscal.receipt r ON r.id = s.id JOIN fiscal.source_payload p ON p.id = r.source_payload_id WHERE p.source_event_id = 'DEMO1:70003'"));
    }

    [PgFact]
    public async Task Rejected_by_atk_is_reported_to_opera()
    {
        await SetUpAsync();
        app.FakeAtk.RejectNext = true;
        var (status, body) = await PostAsync(WithFolio(Sample(), "70004"));
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Contains("test rejection", body.GetProperty("Message").GetString());
    }
}

public class CertificateImportTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [PgFact]
    public async Task Onboarder_key_and_certificate_can_be_imported_for_central_signing()
    {
        using var scope = app.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var enrollment = scope.ServiceProvider.GetRequiredService<TerminalEnrollment>();
        var business = (await settings.BusinessAsync())!;
        await settings.SaveTerminalAsync(new TerminalEdit { PosId = 31, OperaTerminalId = "IMP1", Hostname = "IMP1", SigningMode = "server" }, "test");
        var id = (await settings.TerminalsAsync()).Single(t => t.PosId == 31).Id;

        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        System.Security.Cryptography.X509Certificates.X509Certificate2 Cert(long pos) =>
            new System.Security.Cryptography.X509Certificates.CertificateRequest(
                $"CN=Test, O={business.Nui}, OU={pos}, L={business.BranchId}", key, System.Security.Cryptography.HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // A certificate for another POS is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.ImportAsync(id, key.ExportECPrivateKeyPem(), Cert(99).ExportCertificatePem(), "test"));

        await enrollment.ImportAsync(id, key.ExportECPrivateKeyPem(), Cert(31).ExportCertificatePem(), "test");
        var signer = await enrollment.OpenKeyAsync(id);
        var data = "x"u8.ToArray();
        Assert.True(key.VerifyData(data, signer.SignData(data), System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal("active", await app.ScalarAsync<string>($"SELECT status FROM fiscal.terminal WHERE id = {id}"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE details::text LIKE '%PRIVATE KEY%'"));
    }
}
