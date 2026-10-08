using System.Net;
using System.Text;

namespace Hrs.Fiscal.Server.Tests;

public class AdminUiTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [PgFact]
    public async Task Pages_require_login()
    {
        var response = await app.Anonymous().GetAsync("/Receipts");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location!.PathAndQuery);
    }

    [PgFact]
    public async Task Wrong_password_is_rejected_and_logged()
    {
        var client = app.Anonymous();
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Login");
        var response = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "auditor", ["Password"] = "not-the-password", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("incorrect", await response.Content.ReadAsStringAsync());
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'LOGIN_FAILED' AND entity_id = 'auditor'") >= 1);
    }

    [PgFact]
    public async Task Admin_sees_all_pages()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        foreach (var path in new[] { "/", "/Receipts", "/Receipts?Status=pending", "/Audit", "/Export", "/Settings", "/Settings/Business",
                                     "/Settings/Workstations", "/Settings/Mapping", "/Settings/Users" })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {response.StatusCode}");
        }
        var receiptId = await app.ScalarAsync<long>("SELECT min(id) FROM fiscal.receipt");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Receipts/Detail/{receiptId}")).StatusCode);
        var qr = await client.GetStringAsync($"/receipts/{receiptId}/qr.svg");
        Assert.Contains("<svg", qr);
    }

    [PgFact]
    public async Task Cashier_cannot_open_settings_or_export()
    {
        var client = await app.SignedInAsync("cashier", "demo-password-2");
        var settings = await client.GetAsync("/Settings");
        Assert.Equal(HttpStatusCode.Redirect, settings.StatusCode);
        Assert.StartsWith("/Denied", settings.Headers.Location!.PathAndQuery);
        var export = await client.GetAsync("/export/receipts?format=csv");
        Assert.StartsWith("/Denied", export.Headers.Location!.PathAndQuery);
    }

    [PgFact]
    public async Task Csv_export_has_all_receipts_and_is_logged_with_hash()
    {
        var client = await app.SignedInAsync("supervisor", "demo-password-1");
        var response = await client.GetAsync("/export/receipts?format=csv&from=2000-01-01&requestedBy=ATK%20test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.receipt") + 1, lines.Length);

        var details = await app.ScalarAsync<string>("SELECT details::text FROM fiscal.audit_log WHERE action = 'EXPORT_CSV' ORDER BY id DESC LIMIT 1");
        Assert.Contains("sha256", details);
        Assert.Contains("ATK test", details);
    }

    [PgFact]
    public async Task Pdf_copy_is_generated_and_logged_as_reprint()
    {
        var client = await app.SignedInAsync("cashier", "demo-password-2");
        var id = await app.ScalarAsync<long>("SELECT max(id) FROM fiscal.receipt");
        var response = await client.GetAsync($"/receipts/{id}/copy.pdf");
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await response.Content.ReadAsByteArrayAsync())[..4]));
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'REPRINT'") >= 1);
    }

    [PgFact]
    public async Task Settings_change_is_audited_with_old_and_new_value()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings");
        var form = new Dictionary<string, string>
        {
            ["Input.RetentionYears"] = "11", ["Input.BackupTarget"] = "", ["Input.AtkEnvironment"] = "Test", ["Input.AtkApplicationId"] = "0",
            ["Input.AtkTimeoutSeconds"] = "10", ["Input.AtkRetryMinutes"] = "2", ["Input.AlertEmails"] = "", ["Input.VatRounding"] = "RoundTaxHalfUp",
            ["__RequestVerificationToken"] = token,
        };
        var response = await client.PostAsync("/Settings", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("11", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'retention_years'"));
        var details = await app.ScalarAsync<string>("SELECT details::text FROM fiscal.audit_log WHERE action = 'SETTING_CHANGED' AND entity_id = 'retention_years' ORDER BY id DESC LIMIT 1");
        Assert.Contains("\"old\": \"10\"", details);
        Assert.Contains("\"new\": \"11\"", details);
    }

    [PgFact]
    public async Task Integrity_check_passes_on_demo_data_and_is_logged()
    {
        var client = await app.SignedInAsync("auditor", "demo-password-3");
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Audit");
        var response = await client.PostAsync("/Audit?handler=Check", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Contains("Integrity verified", await response.Content.ReadAsStringAsync());
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'INTEGRITY_CHECK'") >= 1);
    }

    [PgFact]
    public async Task Archived_coupon_bytes_verify_against_stored_signature_format()
    {
        // Every archived receipt keeps the exact signed bytes and a DER signature.
        var sig = await app.ScalarAsync<string>("SELECT signature FROM fiscal.receipt ORDER BY id LIMIT 1");
        Assert.Equal(0x30, Convert.FromBase64String(sig!)[0]);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.verify_chain('receipt')"));
    }
}
