using System.Net;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>The business name ATK returns at registration is compared with Settings › Business and can be taken over.</summary>
public class AtkNameTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [Fact]
    public void Names_match_ignoring_case_and_spacing()
    {
        Assert.True(TerminalEnrollment.SameName("Hotel  Demo sh.p.k. ", "HOTEL DEMO SH.P.K."));
        Assert.False(TerminalEnrollment.SameName("Hotel Demo SH.P.K.", "Hotel Demo Prishtina"));
    }

    [PgFact]
    public async Task Registration_records_atks_name_and_the_business_can_take_it_over()
    {
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettingsStore>().SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = 123456789L }, "test");
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);

        var business = await client.GetStringAsync("/Settings/Business");
        Assert.Contains("known after the first ATK registration", business);

        // Register a new central-mode workstation; the fake ATK knows the business as "Hotel Demo Prishtina".
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Workstations?Add=true");
        var add = await client.PostAsync("/Settings/Workstations", new MultipartFormDataContent
        {
            { new StringContent("0"), "Input.Id" }, { new StringContent("51"), "Input.PosId" }, { new StringContent("FO51"), "Input.OperaTerminalId" },
            { new StringContent("FRONTDESK-51"), "Input.Hostname" }, { new StringContent("server"), "Input.SigningMode" },
            { new StringContent("atk"), "Input.Register" }, { new StringContent(token), "__RequestVerificationToken" },
        });
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        var tiles = WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Workstations"));
        Assert.Contains("ATK has the business as “Hotel Demo Prishtina”, but Settings › Business has “Hotel Demo SH.P.K.”", tiles);
        Assert.Equal("\"Hotel Demo Prishtina\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_business_name'"));

        business = WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Business"));
        Assert.Contains("differs", business);
        Assert.Contains("Use ATK's name", business);

        token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business");
        var use = await client.PostAsync("/Settings/Business?handler=UseAtkName",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, use.StatusCode);
        Assert.Equal("Hotel Demo Prishtina", await app.ScalarAsync<string>("SELECT name FROM fiscal.business"));

        business = WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Business"));
        Assert.Contains("matches", business);
        Assert.DoesNotContain("Use ATK's name", business);
        // The rename is in the audit log like any other business change.
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE details::text LIKE '%Hotel Demo Prishtina%' AND entity = 'business'") >= 1);
    }
}
