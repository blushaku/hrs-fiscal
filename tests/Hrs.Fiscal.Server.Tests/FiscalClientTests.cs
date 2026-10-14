using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Hrs.Fiscal.Client;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Client;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>A receipt printer on the front-desk PC, as the Fiscal Client sees it.</summary>
public sealed class RecordingPrinter : IPrinterTransport
{
    public ConcurrentQueue<(PrinterTarget Target, byte[] Job)> Jobs { get; } = new();
    public Task SendAsync(PrinterTarget target, byte[] job, CancellationToken ct = default) { Jobs.Enqueue((target, job)); return Task.CompletedTask; }
    public IReadOnlyList<string> InstalledPrinters() => ["EPSON TM-T88VII Receipt"];
    public IReadOnlyList<string> SerialPorts() => ["COM3"];
}

/// <summary>
/// Workstation-client signing end to end: the installation package from Settings › Workstations, the Fiscal Client pairing
/// with the server, registering its own key with ATK through the server, signing a FLIP folio and printing on its printer.
/// </summary>
public sealed class FiscalClientTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private const string ServerUrl = "http://fiscal.local:5080";
    private static string Sample() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "opera-checkout-sample.json"));
    private static string WithFolio(string folioId) => Sample().Replace("\"FiscalFolioId\": \"67838\"", $"\"FiscalFolioId\": \"{folioId}\"");

    private async Task<long> SetUpAsync()
    {
        using var scope = app.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        await settings.SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = 123456789L, ["flip_mode"] = "live" }, "test");
        var b = (await settings.BusinessAsync())!;
        b.OperaHotelCode = "DEMO1";
        b.VatNo = "X00000000X";
        await settings.SaveBusinessAsync(b, "test");
        await settings.SaveVatRateAsync(new VatRateRow { Letter = "D", Percent = 6, Description = "test" }, "test");
        await settings.SaveTerminalAsync(new TerminalEdit { PosId = 51, OperaTerminalId = "OPERA9TERMINAL", Hostname = "FRONTDESK1", Status = "pending", SigningMode = "client" }, "test");
        return (await settings.TerminalsAsync()).Single(t => t.PosId == 51).Id;
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

    private WebApplication StartClient(string folder, RecordingPrinter printer) =>
        ClientHost.Build([], b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://127.0.0.1:0", ["Client:DataFolder"] = folder, ["Client:KeyStore"] = "file",
            });
        }, app.Server.CreateHandler(), printer, background: false);

    [PgFact]
    public async Task Client_pairs_registers_with_atk_signs_folios_and_prints()
    {
        var id = await SetUpAsync();
        var admin = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var csrBefore = app.FakeAtk.CsrCount;

        // 1. Settings › Workstations: the installation package for this workstation.
        var html = await PostFormAsync(admin, $"/Settings/Workstations?Edit={id}", $"/Settings/Workstations?handler=ClientPackage&id={id}", new() { ["serverUrl"] = ServerUrl + "/Settings" });
        Assert.Contains("Download installation package", html);
        Assert.Contains("not installed", html);
        var zipResponse = await admin.GetAsync($"/Settings/Workstations?handler=ClientDownload&id={id}&server={Uri.EscapeDataString(ServerUrl)}");
        Assert.Equal("application/zip", zipResponse.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await zipResponse.Content.ReadAsStreamAsync());
        Assert.Contains(zip.Entries, e => e.FullName == "Client/Hrs.Fiscal.Client.exe");
        Assert.Contains(zip.Entries, e => e.FullName == "install-client.ps1");
        Assert.Contains(zip.Entries, e => e.FullName == "README.txt");
        string pairingJson;
        using (var r = new StreamReader(zip.GetEntry("pairing.json")!.Open())) pairingJson = await r.ReadToEndAsync();
        var pairing = JsonDocument.Parse(pairingJson).RootElement;
        Assert.Equal(ServerUrl, pairing.GetProperty("serverUrl").GetString());
        Assert.Equal(51, pairing.GetProperty("posId").GetInt64());
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$", pairing.GetProperty("code").GetString());
        Assert.Contains(pairing.GetProperty("code").GetString()!, html);   // also shown for manual set-up

        // 2. The client on the workstation: install-client.ps1 puts pairing.json into its data folder.
        var folder = Path.Combine(Path.GetTempPath(), "hrs-fiscal-client-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "pairing.json"), pairingJson);
        var printer = new RecordingPrinter();
        var client = StartClient(folder, printer);
        try
        {
            await client.StartAsync();
            var clientUrl = client.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            client.Services.GetRequiredService<ClientOptions>().AdvertiseUrl = clientUrl;
            var worker = client.Services.GetRequiredService<ClientWorker>();
            var store = client.Services.GetRequiredService<ClientStore>();

            // One round: pair with the code, create the key here and register it with ATK through the server, report in.
            await worker.StepAsync(CancellationToken.None);
            Assert.True(store.State.Registered, store.State.LastError);
            Assert.False(File.Exists(Path.Combine(folder, "pairing.json")));   // the code is used up
            Assert.Equal(51, store.State.PosId);
            Assert.Equal(csrBefore + 1, app.FakeAtk.CsrCount);
            Assert.Equal("client", await app.ScalarAsync<string>($"SELECT enrolled_mode FROM fiscal.terminal WHERE id = {id}"));
            Assert.Equal("client", await app.ScalarAsync<string>($"SELECT key_reference FROM fiscal.terminal WHERE id = {id}"));
            Assert.Equal(clientUrl, await app.ScalarAsync<string>($"SELECT client_endpoint FROM fiscal.terminal WHERE id = {id}"));
            Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log WHERE action = 'CLIENT_PAIRED' AND entity_id = '{id}'"));
            Assert.DoesNotContain(Directory.EnumerateFiles(folder), f => f.EndsWith(".key") && File.ReadAllText(f).Contains("PRIVATE KEY"));   // protected at rest

            // The code works only once.
            var again = await app.CreateClient().PostAsJsonAsync("/client/pair", new PairRequest(pairing.GetProperty("code").GetString()!, "OTHER", "1", 5110, null));
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

            html = WebUtility.HtmlDecode(await admin.GetStringAsync($"/Settings/Workstations?Edit={id}"));
            Assert.Contains("online", html);
            Assert.Contains(clientUrl, html);

            // 3. A folio from OPERA for this workstation is signed by its Fiscal Client and sent to ATK.
            var response = await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(WithFolio("91001"), Encoding.UTF8, "application/json"));
            var text = await response.Content.ReadAsStringAsync();
            var body = JsonDocument.Parse(text).RootElement;
            Assert.True(body.GetProperty("Status").GetString() == "OK", text);
            Assert.Equal(1, store.State.SignedCount);
            Assert.Contains("NUIKF", await File.ReadAllTextAsync(Path.Combine(folder, "signed.log")));

            // 4. The client signs only for its own POS, and only for the server.
            var foreign = new PosCoupon { BusinessId = (ulong)store.State.Nui, BranchId = (ulong)store.State.BranchId, PosId = 99 };
            Assert.Contains("POS 99", ClientHost.Check(new SignItem("pos", Convert.ToBase64String(foreign.ToByteArray())), store.State, out _));
            using var http = new HttpClient();
            var unsigned = await http.PostAsync(clientUrl + "/sign", new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

            // 5. Printing on the USB printer connected to the workstation (reported by the client).
            html = WebUtility.HtmlDecode(await admin.GetStringAsync($"/Settings/Printing?Edit={id}"));
            Assert.Contains("USB, on this workstation (Fiscal Client)", html);
            Assert.Contains("EPSON TM-T88VII Receipt", html);
            html = await PostFormAsync(admin, $"/Settings/Printing?Edit={id}", $"/Settings/Printing?handler=Printer&Edit={id}", new()
            {
                ["Printer.Kind"] = "client-usb", ["Printer.Address"] = "EPSON TM-T88VII Receipt", ["Printer.Model"] = "epson-tm-t88-80", ["Printer.Auto"] = "true",
            });
            Assert.Contains("Printer for workstation POS 51 saved", html);
            html = await PostFormAsync(admin, "/Settings/Printing", $"/Settings/Printing?handler=Test&id={id}", []);
            Assert.Contains("Test page sent", html);
            Assert.True(printer.Jobs.TryDequeue(out var job));
            Assert.Equal(("usb", "EPSON TM-T88VII Receipt"), (job.Target.Kind, job.Target.Address));
            Assert.Equal(new byte[] { 0x1B, 0x40 }, job.Job[..2]);
        }
        finally
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }

        // 6. The workstation is off: the folio is refused with a clear message instead of being signed elsewhere.
        var offline = await app.Anonymous().PostAsync("/flip/fiscal", new StringContent(WithFolio("91002"), Encoding.UTF8, "application/json"));
        var offlineText = await offline.Content.ReadAsStringAsync();
        Assert.NotEqual("OK", JsonDocument.Parse(offlineText).RootElement.GetProperty("Status").GetString());
        Assert.Contains("does not answer", offlineText);
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    [Fact]
    public void Request_signature_covers_method_path_time_and_body()
    {
        var secret = ClientAuth.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var unix = now.ToUnixTimeSeconds().ToString();
        var body = "{\"a\":1}"u8.ToArray();
        var signature = ClientAuth.Sign(secret, "POST", "/sign", now.ToUnixTimeSeconds(), body);
        Assert.True(ClientAuth.Verify(secret, "POST", "/sign", unix, signature, body, now));
        Assert.False(ClientAuth.Verify(secret, "POST", "/print", unix, signature, body, now));
        Assert.False(ClientAuth.Verify(secret, "POST", "/sign", unix, signature, "{\"a\":2}"u8.ToArray(), now));
        Assert.False(ClientAuth.Verify(ClientAuth.NewSecret(), "POST", "/sign", unix, signature, body, now));
        Assert.False(ClientAuth.Verify(secret, "POST", "/sign", unix, signature, body, now.AddMinutes(5)));   // replay later
    }
}
