using System.Net;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>VAT rates start empty and are defined by the user: add, change which rate matches OPERA, delete.</summary>
public class VatRateTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private static async Task<string> PostAsync(HttpClient client, string handler, string? key, Dictionary<string, string> form)
    {
        var path = key is null ? "/Settings/Mapping?Form=vat" : $"/Settings/Mapping?Form=vat&Key={key}";
        form["__RequestVerificationToken"] = await ServerFixture.AntiforgeryTokenAsync(client, path);
        var url = $"/Settings/Mapping?handler={handler}&Form=vat" + (key is null ? "" : $"&Key={key}");
        var response = await client.PostAsync(url, new FormUrlEncodedContent(form));
        var html = response.StatusCode == HttpStatusCode.Redirect
            ? await client.GetStringAsync("/Settings/Mapping")
            : await response.Content.ReadAsStringAsync();
        return WebUtility.HtmlDecode(html);
    }

    private static Dictionary<string, string> Rate(string letter, string percent, string description, bool match) => new()
    {
        ["VatInput.Letter"] = letter, ["VatInput.Percent"] = percent, ["VatInput.Description"] = description,
        ["VatInput.MatchOpera"] = match ? "true" : "false",
    };

    [PgFact]
    public async Task Rates_start_empty_and_are_added_matched_and_deleted_by_the_user()
    {
        // The migrations leave no rates: the only ones present were added by the demo data (audited as demo-seed).
        Assert.Equal(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.vat_rate"),
            await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE entity = 'vat_rate' AND actor = 'demo-seed'"));

        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);

        var html = await PostAsync(client, "Vat", null, Rate("F", "10", "Tourism rate", true));
        Assert.Contains("VAT rate F added", html);
        Assert.Contains("yes, lines with 10", html);

        // A second rate with 10% cannot also match OPERA lines…
        html = await PostAsync(client, "Vat", null, Rate("G", "10", "Tourism exempt", true));
        Assert.Contains("VAT rate F (Tourism rate) already matches OPERA lines with 10%", html);
        // …but can exist for overrides only.
        html = await PostAsync(client, "Vat", null, Rate("G", "10", "Tourism exempt", false));
        Assert.Contains("VAT rate G added", html);
        Assert.Contains("no, overrides only", html);

        // A rate used by an OPERA override cannot be deleted.
        var used = await app.ScalarAsync<string>("SELECT vat_letter FROM fiscal.opera_trx_mapping LIMIT 1");
        html = await PostAsync(client, "DeleteVat", used, []);
        Assert.Contains($"VAT rate {used} is used by OPERA override(s)", html);

        html = await PostAsync(client, "DeleteVat", "G", []);
        Assert.Contains("VAT rate G deleted", html);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.vat_rate WHERE letter = 'G'"));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.vat_rate WHERE letter = 'F' AND match_opera"));
    }
}
