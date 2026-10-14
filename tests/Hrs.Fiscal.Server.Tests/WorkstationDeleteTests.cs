using System.Net;
using Hrs.Fiscal.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>A workstation without receipts can be deleted; one with receipts is kept (disable it instead).</summary>
public class WorkstationDeleteTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private static async Task<string> PostAsync(HttpClient client, long id)
    {
        var token = await ServerFixture.AntiforgeryTokenAsync(client, $"/Settings/Workstations?Edit={id}");
        var response = await client.PostAsync($"/Settings/Workstations?handler=Delete&id={id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return WebUtility.HtmlDecode(await client.GetStringAsync(response.Headers.Location!.ToString()));
    }

    [PgFact]
    public async Task Unused_workstation_is_deleted_and_used_one_is_kept()
    {
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettingsStore>().SaveTerminalAsync(
                new TerminalEdit { PosId = 77, OperaTerminalId = "WRONG-ONE", Hostname = "TYPO", Status = "pending", SigningMode = "server" }, "test");
        var id = await app.ScalarAsync<long>("SELECT id FROM fiscal.terminal WHERE pos_id = 77");
        var admin = await app.SignedInAsync("admin", ServerFixture.AdminPassword);

        Assert.Contains("Delete workstation", WebUtility.HtmlDecode(await admin.GetStringAsync($"/Settings/Workstations?Edit={id}")));
        var html = await PostAsync(admin, id);
        Assert.Contains("Workstation POS 77 deleted", html);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.terminal WHERE pos_id = 77"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log WHERE action = 'TERMINAL_DELETED' AND entity_id = '{id}' AND details->>'PosId' = '77'"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.verify_chain('audit_log')"));   // the log stays intact

        // The same POS ID can be added again.
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettingsStore>().SaveTerminalAsync(
                new TerminalEdit { PosId = 77, OperaTerminalId = "RIGHT-ONE", Hostname = "FRONTDESK", Status = "pending", SigningMode = "server" }, "test");
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.terminal WHERE pos_id = 77"));

        // A workstation with receipts cannot be deleted.
        var usedId = await app.ScalarAsync<long>("SELECT coalesce(min(terminal_id), 0) FROM fiscal.receipt");
        Assert.True(usedId > 0, "the demo data has receipts");
        {
            Assert.Contains("must be kept with it", WebUtility.HtmlDecode(await admin.GetStringAsync($"/Settings/Workstations?Edit={usedId}")));
            html = await PostAsync(admin, usedId);
            Assert.Contains("has issued receipts", html);
            Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.terminal WHERE id = {usedId}"));
        }
    }
}
