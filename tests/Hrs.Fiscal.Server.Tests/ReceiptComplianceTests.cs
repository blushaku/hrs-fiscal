using System.Net;
using System.Text;
using System.Text.Json;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>
/// ATK receipt rules added for certification: daily receipt number, OFFLINE mark, cancellation receipt with reason,
/// paper-block entry (Doc. 01-06-1967 Arts 25.11, 25.16, 25.18, 26.13; UA Arts 7, 45).
/// </summary>
public class ReceiptComplianceTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private static string Folio(string folioId) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "opera-checkout-sample.json"))
            .Replace("\"FiscalFolioId\": \"67838\"", $"\"FiscalFolioId\": \"{folioId}\"");

    private async Task SetUpAsync()
    {
        using var scope = app.Services.CreateScope();
        var s = scope.ServiceProvider;
        var settings = s.GetRequiredService<SettingsStore>();
        if ((await settings.TerminalsAsync()).Any(t => t.OperaTerminalId == "OPERA9TERMINAL")) return;
        await settings.SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = 123456789L, ["flip_mode"] = "live" }, "test");
        var b = (await settings.BusinessAsync())!;
        b.OperaHotelCode = "DEMO1";
        b.VatNo = "X00000000X";
        await settings.SaveBusinessAsync(b, "test");
        await settings.SaveVatRateAsync(new VatRateRow { Letter = "D", Percent = 6, Description = "test" }, "test");
        await settings.SaveTerminalAsync(new TerminalEdit { PosId = 21, OperaTerminalId = "OPERA9TERMINAL", Hostname = "OPERA9", Status = "pending", SigningMode = "server" }, "test");
        var id = (await settings.TerminalsAsync()).Single(t => t.OperaTerminalId == "OPERA9TERMINAL").Id;
        await s.GetRequiredService<TerminalEnrollment>().EnrollAsync(id, "test");
    }

    private async Task<JsonElement> FiscalizeAsync(string folioId)
    {
        var response = await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(Folio(folioId), Encoding.UTF8, "application/json"));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static List<string> Lines(JsonElement body) => body.GetProperty("FiscalText").EnumerateArray().Select(e => e.GetString()!).ToList();

    [PgFact]
    public async Task Daily_number_counts_per_workstation_and_offline_receipts_are_marked()
    {
        await SetUpAsync();
        var first = await FiscalizeAsync("81001");
        var second = await FiscalizeAsync("81002");
        var d1 = first.GetProperty("DailyNo").GetInt32();
        Assert.Equal(d1 + 1, second.GetProperty("DailyNo").GetInt32());
        Assert.False(first.GetProperty("Offline").GetBoolean());
        Assert.Equal("KUPON FISKAL", first.GetProperty("ReceiptType").GetString());
        Assert.Contains($"KUPON FISKAL DITOR NR. {d1 + 1:0000}", Lines(second));
        Assert.Contains("e-kupon", Lines(second));
        Assert.DoesNotContain("OFFLINE", Lines(second));

        // ATK unreachable: the receipt is issued, marked OFFLINE and keeps the daily sequence.
        app.FakeAtk.Offline = true;
        try
        {
            var offline = await FiscalizeAsync("81003");
            Assert.Equal("pending", offline.GetProperty("AtkStatus").GetString());
            Assert.True(offline.GetProperty("Offline").GetBoolean());
            Assert.Contains("OFFLINE", Lines(offline));
            Assert.Equal(d1 + 2, offline.GetProperty("DailyNo").GetInt32());
            Assert.True(await app.ScalarAsync<bool>($"SELECT offline FROM fiscal.receipt_status WHERE coupon_id = {offline.GetProperty("FiscalBillNo").GetString()}"));
        }
        finally { app.FakeAtk.Offline = false; }

        // The archive still verifies after the new columns (old rows hash as before).
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.verify_chain('receipt')"));
        Assert.Equal(0L, await app.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT terminal_id, issued_day, daily_no FROM fiscal.receipt WHERE daily_no IS NOT NULL GROUP BY 1, 2, 3 HAVING count(*) > 1) d"));
    }

    [PgFact]
    public async Task Sale_is_cancelled_once_with_a_reason()
    {
        await SetUpAsync();
        var sale = await FiscalizeAsync("82001");
        var couponId = sale.GetProperty("FiscalBillNo").GetString();
        var id = await app.ScalarAsync<long>($"SELECT id FROM fiscal.receipt WHERE coupon_id = {couponId}");
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        Assert.Contains("Cancel receipt", await client.GetStringAsync($"/Receipts/Detail/{id}"));

        async Task<HttpResponseMessage> CancelAsync(string reason)
        {
            var token = await ServerFixture.AntiforgeryTokenAsync(client, $"/Receipts/Detail/{id}?Cancel=true");
            return await client.PostAsync($"/Receipts/Detail/{id}?handler=Cancel",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["Reason"] = reason, ["__RequestVerificationToken"] = token }));
        }

        // No reason: refused, nothing issued.
        var refused = await CancelAsync("");
        Assert.Contains("Cancel=true", refused.Headers.Location!.ToString());
        Assert.Equal(0L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.receipt WHERE reference_coupon_id = {couponId}"));

        var done = await CancelAsync("Gabim gjatë shtypjes");
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        var cancelId = await app.ScalarAsync<long>($"SELECT id FROM fiscal.receipt WHERE reference_coupon_id = {couponId} AND coupon_type = 2");
        Assert.Equal("Gabim gjatë shtypjes", await app.ScalarAsync<string>($"SELECT cancel_reason FROM fiscal.receipt WHERE id = {cancelId}"));
        Assert.Equal(await app.ScalarAsync<long>($"SELECT total_cents FROM fiscal.receipt WHERE id = {id}"),
                     await app.ScalarAsync<long>($"SELECT total_cents FROM fiscal.receipt WHERE id = {cancelId}"));
        Assert.Equal("accepted", await app.ScalarAsync<string>($"SELECT status FROM fiscal.receipt_status WHERE id = {cancelId}"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log WHERE action = 'RECEIPT_CANCELLED' AND entity_id = '{cancelId}'"));

        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/Receipts/Detail/{id}"));
        Assert.DoesNotContain("Cancel receipt…", page);
        Assert.Contains("Already cancelled", page);
        var copy = await client.GetAsync($"/receipts/{cancelId}/copy.pdf");
        Assert.Equal("application/pdf", copy.Content.Headers.ContentType!.MediaType);
    }

    [PgFact]
    public async Task Paper_block_receipt_is_entered_once_with_its_serial_and_time()
    {
        await SetUpAsync();
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var written = DateTime.Now.AddDays(-1).Date.AddHours(9).AddMinutes(30);

        async Task<HttpResponseMessage> EnterAsync(string serial)
        {
            var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Receipts/Paper");
            return await client.PostAsync("/Receipts/Paper", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["PosId"] = "21", ["Serial"] = serial, ["IssuedAt"] = written.ToString("yyyy-MM-ddTHH:mm"), ["Operator"] = "Arta K.",
                ["Payment"] = "Cash",
                ["Lines[0].Name"] = "Kafe", ["Lines[0].Quantity"] = "2", ["Lines[0].UnitPrice"] = "1.50", ["Lines[0].TaxRate"] = "E", ["Lines[0].Unit"] = "cope",
                ["Lines[1].Name"] = "Ujë", ["Lines[1].Quantity"] = "1", ["Lines[1].UnitPrice"] = "1.00", ["Lines[1].TaxRate"] = "E", ["Lines[1].Unit"] = "cope",
                ["__RequestVerificationToken"] = token,
            }));
        }

        var entered = await EnterAsync("B-000123");
        Assert.Equal(HttpStatusCode.Redirect, entered.StatusCode);
        var id = await app.ScalarAsync<long>("SELECT id FROM fiscal.receipt WHERE paper_serial = 'B-000123'");
        Assert.Equal(400L, await app.ScalarAsync<long>($"SELECT total_cents FROM fiscal.receipt WHERE id = {id}"));
        Assert.Equal("Blloku B-000123", await app.ScalarAsync<string>($"SELECT source_document FROM fiscal.receipt WHERE id = {id}"));
        Assert.False(await app.ScalarAsync<bool>($"SELECT offline FROM fiscal.receipt_status WHERE id = {id}"));
        Assert.Equal("accepted", await app.ScalarAsync<string>($"SELECT status FROM fiscal.receipt_status WHERE id = {id}"));
        // The receipt keeps the time written on the paper.
        var issued = await app.ScalarAsync<DateTime>($"SELECT issued_at FROM fiscal.receipt WHERE id = {id}");
        Assert.True(Math.Abs((DateTime.UtcNow - issued).TotalHours - (DateTime.Now - written).TotalHours) < 3);

        var again = await EnterAsync("B-000123");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("already entered", WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.receipt WHERE paper_serial = 'B-000123'"));
    }
}
