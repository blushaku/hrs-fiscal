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
        // Read-only until Edit: no input fields on the overview, the tile's fields only after Edit.
        Assert.DoesNotContain("name=\"Input.RetentionYears\"", await client.GetStringAsync("/Settings"));
        Assert.Contains("name=\"Input.RetentionYears\"", await client.GetStringAsync("/Settings?Edit=retention"));

        var timeoutBefore = await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_timeout_seconds'");
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings?Edit=retention");
        var form = new Dictionary<string, string>
        {
            ["section"] = "retention", ["Input.RetentionYears"] = "11", ["Input.BackupTarget"] = "", ["__RequestVerificationToken"] = token,
        };
        var response = await client.PostAsync("/Settings", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("11", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'retention_years'"));
        // Only that tile was saved.
        Assert.Equal(timeoutBefore, await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_timeout_seconds'"));
        var details = await app.ScalarAsync<string>("SELECT details::text FROM fiscal.audit_log WHERE action = 'SETTING_CHANGED' AND entity_id = 'retention_years' ORDER BY id DESC LIMIT 1");
        Assert.Contains("\"old\": \"10\"", details);
        Assert.Contains("\"new\": \"11\"", details);
    }

    [PgFact]
    public async Task Overrides_switch_defaults_off_and_is_audited_when_turned_on()
    {
        Assert.Equal("false", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'opera_overrides_enabled'"));
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        Assert.Contains("Overrides are off", await client.GetStringAsync("/Settings/Mapping"));

        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings?Edit=overrides");
        var response = await client.PostAsync("/Settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["section"] = "overrides", ["Input.OperaOverridesEnabled"] = "true", ["Input.DefaultItemCategory"] = "TT",
            ["Input.DefaultItemUnit"] = "cope", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("true", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'opera_overrides_enabled'"));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'SETTING_CHANGED' AND entity_id = 'opera_overrides_enabled'"));
        Assert.Contains("Overrides are on", await client.GetStringAsync("/Settings/Mapping"));
    }

    [PgFact]
    public async Task Flip_capture_stores_any_message_and_answers_with_test_reply()
    {
        const string xml = "<FiscalPayload><Folio No=\"4711\"><Line Code=\"1000\" Amount=\"85.00\"/></Folio></FiscalPayload>";
        var flip = app.Anonymous();
        var response = await flip.PostAsync("/flip/some/partner/path?terminal=FO1", new StringContent(xml, Encoding.UTF8, "application/xml"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("/flip/some/partner/path?terminal=FO1", await app.ScalarAsync<string>("SELECT path FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));
        Assert.Equal(xml, await app.ScalarAsync<string>("SELECT convert_from(body, 'UTF8') FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));
        Assert.Equal("capture", await app.ScalarAsync<string>("SELECT mode FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));

        var admin = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var page = await admin.GetStringAsync("/FlipMessages");
        Assert.Contains("Folio No=&quot;4711&quot;", page);

        var id = await app.ScalarAsync<long>("SELECT max(id) FROM fiscal.flip_message");
        var raw = await admin.GetByteArrayAsync($"/flip-messages/{id}/raw");
        Assert.Equal(xml, Encoding.UTF8.GetString(raw));

        // FLIP traffic is not readable without login, and stored messages are append-only.
        Assert.Equal(HttpStatusCode.Redirect, (await flip.GetAsync($"/flip-messages/{id}/raw")).StatusCode);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ScalarAsync<int>($"DELETE FROM fiscal.flip_message WHERE id = {id} RETURNING 1"));
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

    [PgFact]
    public async Task Codes_payments_and_vat_rates_are_added_through_a_form()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var page = await client.GetStringAsync("/Settings/Mapping");
        Assert.DoesNotContain("name=\"VatInput.Percent\"", page); // lists are read-only
        Assert.Contains("Add VAT rate", page);

        async Task<HttpResponseMessage> Post(string handler, string query, Dictionary<string, string> fields)
        {
            var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Mapping?" + query);
            fields["__RequestVerificationToken"] = token;
            return await client.PostAsync($"/Settings/Mapping?handler={handler}&{query}", new FormUrlEncodedContent(fields));
        }

        var vat = await Post("Vat", "Form=vat", new() { ["VatInput.Letter"] = "f", ["VatInput.Percent"] = "5", ["VatInput.Description"] = "Test rate" });
        Assert.Equal(HttpStatusCode.Redirect, vat.StatusCode);
        Assert.Equal(5m, await app.ScalarAsync<decimal>("SELECT percent FROM fiscal.vat_rate WHERE letter = 'F'"));
        var dup = await Post("Vat", "Form=vat", new() { ["VatInput.Letter"] = "E", ["VatInput.Percent"] = "9", ["VatInput.Description"] = "x" });
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
        Assert.Equal(18m, await app.ScalarAsync<decimal>("SELECT percent FROM fiscal.vat_rate WHERE letter = 'E'"));

        var trx = await Post("Trx", "Form=trx", new()
        {
            ["TrxInput.TrxCode"] = "77001", ["TrxInput.ItemName"] = "Spa treatment", ["TrxInput.Unit"] = "cope",
            ["TrxInput.Category"] = "shz", ["TrxInput.VatLetter"] = "F", ["TrxInput.Active"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, trx.StatusCode);
        Assert.Equal("SHZ", await app.ScalarAsync<string>("SELECT category FROM fiscal.opera_trx_mapping WHERE trx_code = '77001'"));

        var pay = await Post("Payment", "Form=pay", new() { ["PaymentInput.PaymentCode"] = "9300", ["PaymentInput.Description"] = "Voucher", ["PaymentInput.AtkPaymentType"] = "3" });
        Assert.Equal(HttpStatusCode.Redirect, pay.StatusCode);
        Assert.Equal((short)3, await app.ScalarAsync<short>("SELECT atk_payment_type FROM fiscal.opera_payment_mapping WHERE payment_code = '9300'"));

        // Edit keeps the key and changes the rest.
        var edit = await Post("Vat", "Form=vat&Key=F", new() { ["VatInput.Letter"] = "F", ["VatInput.Percent"] = "6", ["VatInput.Description"] = "Test rate" });
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        Assert.Equal(6m, await app.ScalarAsync<decimal>("SELECT percent FROM fiscal.vat_rate WHERE letter = 'F'"));
    }
}
