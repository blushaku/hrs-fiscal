using System.Net;
using System.Text.Json;

namespace Hrs.Fiscal.Server.Tests;

public class PermissionTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [PgFact]
    public async Task Dashboard_and_menu_follow_the_role()
    {
        var cashier = await app.SignedInAsync("cashier", "demo-password-2");
        var dash = await cashier.GetStringAsync("/");
        Assert.Contains("Find a receipt", dash);
        Assert.DoesNotContain("How folios reach ATK", dash);
        Assert.DoesNotContain("href=\"/Audit\"", dash);
        Assert.StartsWith("/Denied", (await cashier.GetAsync("/Audit")).Headers.Location!.PathAndQuery);

        var supervisor = await app.SignedInAsync("supervisor", "demo-password-1");
        var sdash = await supervisor.GetStringAsync("/");
        Assert.Contains("How folios reach ATK", sdash);
        Assert.Contains("href=\"/Audit\"", sdash);
        Assert.DoesNotContain(">Settings<", sdash);

        // Auditor: may read and export, not download receipt copies.
        var auditor = await app.SignedInAsync("auditor", "demo-password-3");
        var id = await app.ScalarAsync<long>("SELECT min(id) FROM fiscal.receipt");
        Assert.DoesNotContain("copy.pdf", await auditor.GetStringAsync($"/Receipts/Detail/{id}"));
        Assert.StartsWith("/Denied", (await auditor.GetAsync($"/receipts/{id}/copy.pdf")).Headers.Location!.PathAndQuery);
    }

    [PgFact]
    public async Task Admin_can_grant_a_permission_and_it_applies_without_new_sign_in()
    {
        var cashier = await app.SignedInAsync("cashier", "demo-password-2");
        Assert.StartsWith("/Denied", (await cashier.GetAsync("/export/receipts?format=csv")).Headers.Location!.PathAndQuery);

        var admin = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var token = await ServerFixture.AntiforgeryTokenAsync(admin, "/Settings/Roles");
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach (var p in new[] { "receipts.view", "receipts.copy", "export" }) form.Add(new("grant", "Cashier|" + p));
        foreach (var p in new[] { "dashboard.system", "receipts.view", "receipts.copy", "export", "audit.view", "audit.integrity", "flip.view" }) form.Add(new("grant", "Supervisor|" + p));
        foreach (var p in new[] { "receipts.view", "export", "audit.view", "audit.integrity" }) form.Add(new("grant", "Auditor|" + p));
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/Settings/Roles", new FormUrlEncodedContent(form))).StatusCode);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'PERMISSIONS_CHANGED' AND entity_id = 'Cashier'"));

        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/export/receipts?format=csv")).StatusCode);
        Assert.Contains("href=\"/Export\"", await cashier.GetStringAsync("/"));
    }

    [PgFact]
    public async Task Version_is_reported_and_start_is_logged()
    {
        var health = JsonDocument.Parse(await app.Anonymous().GetStringAsync("/health")).RootElement;
        Assert.Matches(@"^\d+\.\d+\.\d+$", health.GetProperty("version").GetString());
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'SERVICE_STARTED'") >= 1);
        Assert.Equal("Opera Cloud Fiscal Solution - Kosovo", health.GetProperty("product").GetString());
        Assert.Contains("Opera Cloud Fiscal Solution - Kosovo " + health.GetProperty("version").GetString(), await (await app.SignedInAsync("admin", ServerFixture.AdminPassword)).GetStringAsync("/"));
    }
}
