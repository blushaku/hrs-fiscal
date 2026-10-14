using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Printing;
using Hrs.Fiscal.Server.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

public class ThermalReceiptTests
{
    private static readonly Encoding Pc850 = CodePagesEncoding();
    private static Encoding CodePagesEncoding() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(850); }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
    private static bool ContainsText(byte[] job, string text) => Contains(job, Pc850.GetBytes(text));

    [Fact]
    public void Escpos_wraps_long_text_and_aligns_right_values()
    {
        Assert.Equal(["ARSYEJA E ANULIMIT: GABIM NË", "SHUMË"], EscPos.Wrap("ARSYEJA E ANULIMIT: GABIM NË SHUMË", 30).ToArray());
        var job = new EscPos(58).LeftRight("TOTALI", "12.50").ToArray();
        Assert.True(ContainsText(job, "TOTALI" + new string(' ', 32 - 6 - 5) + "12.50"));
    }

    [Fact]
    public void Test_page_has_albanian_letters_qr_and_cut()
    {
        var clock = new PropertyClock(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var job = ThermalReceipt.TestPage("POS 1", "network printer 10.0.0.5", 80, clock);
        Assert.Equal(new byte[] { 0x1B, 0x40 }, job[..2]);                         // initialise
        Assert.True(Contains(job, [0x1B, 0x74, 0x02]));                            // code page PC850
        Assert.True(Contains(job, [0x89, 0x20, 0xD3, 0x20, 0x87, 0x20, 0x80]));     // ë Ë ç Ç
        Assert.True(Contains(job, [0x1D, 0x28, 0x6B, 0x07, 0x00, 0x31, 0x50, 0x30, (byte)'T', (byte)'E', (byte)'S', (byte)'T']));
        Assert.True(Contains(job, [0x1D, 0x56, 0x42]));                            // cut
    }
}

/// <summary>Receipts printed by the fiscal server on a thermal printer (Settings › Printing), here a fake network printer.</summary>
public sealed class PrintingTests(ServerFixture app) : IClassFixture<ServerFixture>, IDisposable
{
    private readonly FakePrinter _printer = new();
    public void Dispose() => _printer.Dispose();

    private static string Sample() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "opera-checkout-sample.json"));
    private static string WithFolio(string folioId) => Sample().Replace("\"FiscalFolioId\": \"67838\"", $"\"FiscalFolioId\": \"{folioId}\"");

    private async Task<long> SetUpAsync()
    {
        using var scope = app.Services.CreateScope();
        var s = scope.ServiceProvider;
        var settings = s.GetRequiredService<SettingsStore>();
        if ((await settings.TerminalsAsync()).SingleOrDefault(t => t.OperaTerminalId == "OPERA9TERMINAL") is { } existing) return existing.Id;
        await settings.SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = 123456789L, ["flip_mode"] = "live" }, "test");
        var b = (await settings.BusinessAsync())!;
        b.OperaHotelCode = "DEMO1";
        b.VatNo = "X00000000X";
        await settings.SaveBusinessAsync(b, "test");
        await settings.SaveVatRateAsync(new VatRateRow { Letter = "D", Percent = 6, Description = "test" }, "test");
        await settings.SaveTerminalAsync(new TerminalEdit { PosId = 31, OperaTerminalId = "OPERA9TERMINAL", Hostname = "OPERA9", Status = "pending", SigningMode = "server" }, "test");
        var id = (await settings.TerminalsAsync()).Single(t => t.OperaTerminalId == "OPERA9TERMINAL").Id;
        await s.GetRequiredService<TerminalEnrollment>().EnrollAsync(id, "test");
        return id;
    }

    private static async Task<string> PostFormAsync(HttpClient client, string getPath, string postUrl, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = await ServerFixture.AntiforgeryTokenAsync(client, getPath);
        var response = await client.PostAsync(postUrl, new FormUrlEncodedContent(form));
        var html = response.StatusCode == HttpStatusCode.Redirect
            ? await client.GetStringAsync(response.Headers.Location!.ToString())
            : await response.Content.ReadAsStringAsync();
        return WebUtility.HtmlDecode(html);
    }

    [PgFact]
    public async Task Receipt_prints_automatically_once_and_reprints_as_a_copy()
    {
        var terminalId = await SetUpAsync();
        var admin = await app.SignedInAsync("admin", ServerFixture.AdminPassword);

        // 1. Settings › Printing: receipts on thermal printers, and this workstation's network printer.
        var html = await PostFormAsync(admin, "/Settings/Printing?Change=true", "/Settings/Printing?handler=Mode", new() { ["NewMode"] = "printer" });
        Assert.Contains("printed on the workstations' thermal printers", html);
        html = await PostFormAsync(admin, $"/Settings/Printing?Edit={terminalId}", $"/Settings/Printing?handler=Printer&Edit={terminalId}", new()
        {
            ["Printer.Kind"] = "network", ["Printer.Address"] = $"127.0.0.1:{_printer.Port}", ["Printer.PaperMm"] = "80", ["Printer.Auto"] = "true",
        });
        Assert.Contains("Printer for workstation POS 31 saved", html);
        Assert.Contains($"127.0.0.1:{_printer.Port}", html);

        // 2. Test print reaches the printer.
        html = await PostFormAsync(admin, "/Settings/Printing", $"/Settings/Printing?handler=Test&id={terminalId}", []);
        Assert.Contains("Test page sent", html);
        Assert.Contains("PROVË", Decode(await _printer.NextAsync()));

        // 3. A folio from OPERA is fiscalized and printed by itself: the original, with the receipt's NUIKF.
        var response = await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(WithFolio("81001"), Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("OK", body.GetProperty("Status").GetString());
        var nuikf = body.GetProperty("VerificationNo").GetString()!;
        var original = Decode(await _printer.NextAsync());
        Assert.Contains($"NUIKF: {nuikf}", original);
        Assert.Contains("TOTALI PËR PAGESË", original);
        Assert.Contains("e-kupon", original);
        Assert.DoesNotContain("KOPJE E KUPONIT", original);

        // 4. OPERA reprints the same folio: same receipt, nothing printed again.
        await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(WithFolio("81001"), Encoding.UTF8, "application/json"));
        Assert.Null(await _printer.NextAsync(TimeSpan.FromSeconds(1)));

        // 5. Printed again from the receipt page: a copy.
        var receiptId = await app.ScalarAsync<long>($"SELECT id FROM fiscal.receipt WHERE verification_no = '{nuikf}'");
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync($"/Receipts/Detail/{receiptId}"));
        Assert.Contains("Print copy on thermal printer", page);
        html = await PostFormAsync(admin, $"/Receipts/Detail/{receiptId}", $"/Receipts/Detail/{receiptId}?handler=Print", []);
        Assert.Contains("Copy of receipt", html);
        Assert.Contains("KOPJE E KUPONIT", Decode(await _printer.NextAsync()));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log WHERE action = 'RECEIPT_PRINTED' AND entity_id = '{receiptId}' AND details->>'copy' = 'false'"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log WHERE action = 'RECEIPT_PRINTED' AND entity_id = '{receiptId}' AND details->>'copy' = 'true'"));

        // 6. Printer switched off: the receipt is still fiscalized; the failure is logged and shown on the dashboard.
        _printer.Dispose();
        response = await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(WithFolio("81002"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        long failed = 0;
        for (var i = 0; i < 50 && failed == 0; i++)
        {
            await Task.Delay(200);
            failed = await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'PRINT_FAILED'");
        }
        Assert.Equal(1L, failed);
        Assert.Contains("was not printed", WebUtility.HtmlDecode(await admin.GetStringAsync("/")));

        // 7. Back to the folio: nothing is printed by itself any more.
        await PostFormAsync(admin, "/Settings/Printing?Change=true", "/Settings/Printing?handler=Mode", new() { ["NewMode"] = "folio" });
        Assert.Equal("folio", await app.ScalarAsync<string>("SELECT value #>> '{}' FROM fiscal.setting WHERE key = 'receipt_print_mode'"));
    }

    private static string Decode(byte[]? job)
    {
        Assert.NotNull(job);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850).GetString(job!);
    }

    /// <summary>A network receipt printer on a free local port: every connection is one print job.</summary>
    private sealed class FakePrinter : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly BlockingCollection<byte[]> _jobs = new();
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }

        public FakePrinter()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var ms = new MemoryStream();
                        await client.GetStream().CopyToAsync(ms, _stop.Token);
                        _jobs.Add(ms.ToArray());
                    }
                    catch { if (_stop.IsCancellationRequested) return; }
                }
            });
        }

        public Task<byte[]?> NextAsync(TimeSpan? wait = null) =>
            Task.Run(() => _jobs.TryTake(out var job, wait ?? TimeSpan.FromSeconds(10)) ? job : null);

        public void Dispose()
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
