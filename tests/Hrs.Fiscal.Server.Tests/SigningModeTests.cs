using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>Fake ATK: answers /ca/verify and signs CSRs with a throwaway CA, like ATK's TEST CA does.</summary>
public sealed class FakeAtk : HttpMessageHandler, IAtkClientFactory
{
    private readonly ECDsa _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _ca;
    public int CsrCount;

    public FakeAtk()
    {
        var req = new CertificateRequest("CN=Fake ATK Test CA", _caKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public AtkClient Create(AtkEnvironment environment, TimeSpan timeout) =>
        new(new HttpClient(this, false) { BaseAddress = AtkEndpoints.BaseUri(environment), Timeout = timeout });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        if (path.StartsWith("ca/verify/"))
            return Json(new { business_name = "Hotel Demo Prishtina", verification_code = "424242" });
        if (path == "ca/signcsr")
        {
            Interlocked.Increment(ref CsrCount);
            var csr = CertificateRequest.LoadSigningRequestPem(body.RootElement.GetProperty("csr").GetString()!, HashAlgorithmName.SHA256);
            var serial = RandomNumberGenerator.GetBytes(12);
            using var cert = csr.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2), serial);
            return Json(new { signed_certificate = cert.ExportCertificatePem() });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}

public class SigningModeTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private async Task<long> TerminalIdAsync(long pos) => await app.ScalarAsync<long>($"SELECT id FROM fiscal.terminal WHERE pos_id = {pos}");

    private async Task SetAsync(Dictionary<string, object?> values)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsStore>().SetManyAsync(values, "test");
    }

    [PgFact]
    public async Task Default_is_workstation_client_and_registering_on_the_server_is_refused()
    {
        Assert.Equal("\"client\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'signing_mode_default'"));
        var id = await TerminalIdAsync(11);
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var page = await client.GetStringAsync($"/Settings/Workstations?Edit={id}");
        Assert.Contains("signs with the HRS Fiscal Client", page);

        var token = await ServerFixture.AntiforgeryTokenAsync(client, $"/Settings/Workstations?Edit={id}");
        await client.PostAsync($"/Settings/Workstations?handler=Register&id={id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Contains("Register it from the client", await client.GetStringAsync($"/Settings/Workstations?Edit={id}"));
        Assert.Null(await app.ScalarAsync<string>($"SELECT key_reference FROM fiscal.terminal WHERE id = {id}"));
    }

    [PgFact]
    public async Task Central_mode_registers_the_workstation_with_atk_and_signs_with_the_server_key()
    {
        await SetAsync(new() { ["atk_application_id"] = 123456789L });
        var id = await TerminalIdAsync(12);
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);

        // Per-workstation override to central signing, through the UI.
        var token = await ServerFixture.AntiforgeryTokenAsync(client, $"/Settings/Workstations?Edit={id}");
        var save = await client.PostAsync("/Settings/Workstations", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Id"] = id.ToString(), ["Input.PosId"] = "12", ["Input.OperaTerminalId"] = "FO2", ["Input.Hostname"] = "FRONTDESK-02",
            ["Input.Status"] = "active", ["Input.SigningMode"] = "server", ["__RequestVerificationToken"] = token,
        }));
        Assert.True(save.StatusCode == HttpStatusCode.Redirect, System.Text.RegularExpressions.Regex.Match(await save.Content.ReadAsStringAsync(), "validation[^>]*>(.{0,400})", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value);
        Assert.Contains("register again", await client.GetStringAsync("/Settings/Workstations")); // was registered on the client

        token = await ServerFixture.AntiforgeryTokenAsync(client, $"/Settings/Workstations?Edit={id}");
        var register = await client.PostAsync($"/Settings/Workstations?handler=Register&id={id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        Assert.Contains("Registered with ATK (Test)", await client.GetStringAsync($"/Settings/Workstations?Edit={id}"));

        Assert.Equal("server", await app.ScalarAsync<string>($"SELECT enrolled_mode FROM fiscal.terminal WHERE id = {id}"));
        Assert.StartsWith("file:HRS-Fiscal-", await app.ScalarAsync<string>($"SELECT key_reference FROM fiscal.terminal WHERE id = {id}"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log WHERE action = 'TERMINAL_ENROLLED' AND entity_id = '{id}'"));
        // The private key never reaches the database or the audit log.
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE details::text LIKE '%PRIVATE KEY%'"));
        Assert.Empty(Directory.GetFiles(app.KeyFolder, "*.key").Select(File.ReadAllText).Where(t => t.Contains("PRIVATE KEY")));

        // The server can sign for this workstation, and the signature verifies against the ATK certificate.
        using var scope = app.Services.CreateScope();
        var key = await scope.ServiceProvider.GetRequiredService<TerminalEnrollment>().OpenKeyAsync(id);
        var data = "coupon"u8.ToArray();
        var pem = await app.ScalarAsync<string>($"SELECT certificate_pem FROM fiscal.terminal WHERE id = {id}");
        using var certKey = X509Certificate2.CreateFromPem(pem!).GetECDsaPublicKey()!;
        Assert.True(certKey.VerifyData(data, key.SignData(data), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Contains("OU=12", X509Certificate2.CreateFromPem(pem!).Subject);
    }

    [PgFact]
    public async Task Changing_the_property_mode_flags_workstations_registered_elsewhere()
    {
        await SetAsync(new() { ["signing_mode_default"] = "server" });
        try
        {
            using var scope = app.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
            var t13 = (await settings.TerminalsAsync()).Single(t => t.PosId == 13);
            Assert.Equal(TerminalSigningState.RegisterAgain, t13.SigningState(await settings.SigningModeDefaultAsync()));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<TerminalEnrollment>().OpenKeyAsync(t13.Id));
        }
        finally
        {
            await SetAsync(new() { ["signing_mode_default"] = "client" });
        }
    }
}
