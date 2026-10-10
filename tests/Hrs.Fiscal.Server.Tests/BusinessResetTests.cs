using System.Net;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>"Start over with a new business" deletes all business data and settings (test data only).</summary>
public class BusinessResetTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [PgFact]
    public async Task Start_over_deletes_everything_but_users_and_requires_the_nui()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var nui = await app.ScalarAsync<long>("SELECT business_nui FROM fiscal.branch WHERE active");
        Assert.Contains("Start over with a new business", await client.GetStringAsync("/Settings/Business"));

        // Wrong NUI: nothing happens.
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business?Reset=true");
        await client.PostAsync("/Settings/Business?handler=Reset", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirmNui"] = "1", ["__RequestVerificationToken"] = token }));
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.receipt") > 0);

        token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business?Reset=true");
        var reset = await client.PostAsync("/Settings/Business?handler=Reset", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirmNui"] = nui.ToString(), ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        foreach (var table in new[] { "receipt", "transmission", "terminal", "business", "branch", "flip_message", "opera_trx_mapping" })
            Assert.Equal(0L, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.{table}"));
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.app_user") >= 1);
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.vat_rate") >= 4);
        Assert.Equal("\"capture\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'flip_mode'"));
        // The new audit chain starts with the reset itself, and the archive is still protected.
        Assert.Equal("BUSINESS_RESET", await app.ScalarAsync<string>("SELECT action FROM fiscal.audit_log ORDER BY id LIMIT 1"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ScalarAsync<int>("DELETE FROM fiscal.audit_log RETURNING 1"));

        // The dashboard now sends the administrator to the business setup first.
        var noRedirect = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var dash = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dash.StatusCode);
        Assert.Contains("/Settings/Business", dash.Headers.Location!.ToString());

        // After the server was switched to Production, starting over is refused.
        token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings?Edit=atk");
        await client.PostAsync("/Settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["section"] = "atk", ["Input.AtkEnvironment"] = "Production", ["Input.AtkTimeoutSeconds"] = "10", ["Input.AtkRetryMinutes"] = "2", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal("\"Production\"", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_environment'"));
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(app.Services);
        var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Hrs.Fiscal.Server.Data.BusinessReset>(scope.ServiceProvider);
        Assert.True((await service.StatusAsync()).ProductionUsed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetAsync("test"));
    }
}
