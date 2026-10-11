using System.Net;
using System.Text.RegularExpressions;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>
/// Business setup runs in two steps: NUI, fiscalization no., unit and Application ID are validated with ATK first; only then
/// the rest opens, with the registered name from ATK's answer. Saving is separate and never calls ATK.
/// </summary>
public class BusinessValidationTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private sealed record Saved(long Nui, long Unit, string Name, string Unit_Name, string Location);

    private async Task<Saved> SavedAsync() => new(
        await app.ScalarAsync<long>("SELECT business_nui FROM fiscal.branch WHERE active"),
        await app.ScalarAsync<long>("SELECT branch_id FROM fiscal.branch WHERE active"),
        (await app.ScalarAsync<string>("SELECT b.name FROM fiscal.business b JOIN fiscal.branch br ON br.business_nui = b.nui WHERE br.active"))!,
        (await app.ScalarAsync<string>("SELECT name FROM fiscal.branch WHERE active"))!,
        (await app.ScalarAsync<string>("SELECT location FROM fiscal.branch WHERE active"))!);

    private static async Task<(HttpResponseMessage Response, string Html)> PostAsync(HttpClient client, string handler, Saved s,
        string fiscNo, long appId, string? name = null, string? validation = null)
    {
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business?Edit=true");
        var form = new Dictionary<string, string>
        {
            ["Input.Nui"] = s.Nui.ToString(), ["Input.FiscalizationNo"] = fiscNo, ["Input.BranchId"] = s.Unit.ToString(),
            ["Input.ApplicationId"] = appId.ToString(), ["PosId"] = "99",
            ["Input.Name"] = name ?? s.Name, ["Input.BranchName"] = s.Unit_Name, ["Input.Location"] = s.Location,
            ["Validation"] = validation ?? "", ["__RequestVerificationToken"] = token,
        };
        var url = handler == "" ? "/Settings/Business" : $"/Settings/Business?handler={handler}";
        var response = await client.PostAsync(url, new FormUrlEncodedContent(form));
        return (response, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    private static string ValidationToken(string html)
    {
        var m = Regex.Match(html, "<input[^>]*name=\"Validation\"[^>]*>");
        Assert.True(m.Success);
        return Regex.Match(m.Value, "value=\"([^\"]*)\"").Groups[1].Value;
    }

    [PgFact]
    public async Task Atk_values_are_validated_before_the_rest_opens_and_saves()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var s = await SavedAsync();

        // No Application ID: validation does not even ask ATK.
        var (_, html) = await PostAsync(client, "Validate", s, "EDI-2", 0);
        Assert.Contains("Enter the Application ID", html);
        Assert.Contains("id=\"step2\" hidden", html);

        // Validate: ATK confirms, the registered name is filled from its answer, step 2 opens.
        (_, html) = await PostAsync(client, "Validate", s, "EDI-2", 123456789);
        Assert.Contains("ATK (Test) confirms these values: registered to “Hotel Demo Prishtina”", html);
        Assert.Contains("value=\"Hotel Demo Prishtina\"", html);
        Assert.Contains("Registered name (from ATK)", html);
        // Save posts to the page itself, never back to the Validate handler (that would ask ATK again instead of saving).
        Assert.Matches("<form method=\"post\" class=\"modal-body\" id=\"business-form\"[^>]*action=\"/Settings/Business\"|<form method=\"post\" action=\"/Settings/Business\" class=\"modal-body\" id=\"business-form\"", html);
        var validation = ValidationToken(html);
        Assert.False(string.IsNullOrEmpty(validation));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.terminal WHERE pos_id = 99")); // nothing registered

        var (saved, _) = await PostAsync(client, "", s, "EDI-2", 123456789, "Hotel Demo Prishtina", validation);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("EDI-2", await app.ScalarAsync<string>($"SELECT fiscalization_no FROM fiscal.business WHERE nui = {s.Nui}"));
        Assert.Equal("Hotel Demo Prishtina", await app.ScalarAsync<string>($"SELECT name FROM fiscal.business WHERE nui = {s.Nui}"));
        Assert.Equal("123456789", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_application_id'"));
        Assert.Equal("\"Hotel Demo Prishtina\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_business_name'"));

        // ATK values unchanged: the other details are edited without a new check.
        s = await SavedAsync();
        var (renamed, _) = await PostAsync(client, "", s with { Location = "Prishtinë" }, "EDI-2", 123456789);
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);
        Assert.Equal("Prishtinë", await app.ScalarAsync<string>("SELECT location FROM fiscal.branch WHERE active"));

        // Saving does not depend on a validation and never calls ATK: changed values without one are saved,
        // but ATK's name is not touched (it only comes from a real ATK answer).
        var checks = await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'ATK_CHECK'");
        var (unvalidated, _) = await PostAsync(client, "", s, "EDI-3", 123456789, "Hotel Without Check");
        Assert.Equal(HttpStatusCode.Redirect, unvalidated.StatusCode);
        Assert.Equal("EDI-3", await app.ScalarAsync<string>($"SELECT fiscalization_no FROM fiscal.business WHERE nui = {s.Nui}"));
        Assert.Equal("\"Hotel Demo Prishtina\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_business_name'"));
        Assert.Equal(checks, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'ATK_CHECK'"));

        // A wrong fiscalization number: ATK refuses, the dialog says why, step 2 stays closed with a way to continue.
        (_, html) = await PostAsync(client, "Validate", s, "WRONG", 123456789);
        Assert.Contains("ATK did not accept this combination", html);
        Assert.Contains("Fill in the details anyway", html);
        Assert.Contains("id=\"step2\" hidden", html);
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'ATK_CHECK'"));
    }
}
