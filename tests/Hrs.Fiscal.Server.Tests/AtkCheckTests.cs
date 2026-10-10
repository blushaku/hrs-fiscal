using System.Net;
using Hrs.Fiscal.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>"Check with ATK" confirms NUI, fiscalization no., unit and Application ID against ATK's registration check.</summary>
public class AtkCheckTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private async Task<HttpResponseMessage> CheckAsync(HttpClient client, string pos)
    {
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business?Check=true");
        return await client.PostAsync("/Settings/Business?handler=CheckAtk",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["posId"] = pos, ["__RequestVerificationToken"] = token }));
    }

    [PgFact]
    public async Task Check_confirms_matching_values_and_explains_a_refusal()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);

        // Without an Application ID the check cannot run.
        await CheckAsync(client, "99");
        Assert.Contains("Enter the Application ID", WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Business")));

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettingsStore>().SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = 123456789L }, "test");
        var page = await client.GetStringAsync("/Settings/Business?Check=true");
        Assert.Contains("Check with ATK", page);

        var ok = await CheckAsync(client, "99");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var after = WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Business"));
        Assert.Contains("ATK (Test) confirms NUI", after);
        Assert.Contains("registered to “Hotel Demo Prishtina”", after);
        Assert.Equal("\"Hotel Demo Prishtina\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_business_name'"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.terminal WHERE pos_id = 99")); // nothing registered

        // A wrong fiscalization number: ATK refuses, the page says why.
        await using (var conn = new Npgsql.NpgsqlConnection(app.ConnectionString))
        {
            await conn.OpenAsync();
            await new Npgsql.NpgsqlCommand("UPDATE fiscal.business SET fiscalization_no = 'WRONG'", conn).ExecuteNonQueryAsync();
        }
        await CheckAsync(client, "99");
        Assert.Contains("ATK did not accept this combination", WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Business")));
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'ATK_CHECK'"));
    }
}
