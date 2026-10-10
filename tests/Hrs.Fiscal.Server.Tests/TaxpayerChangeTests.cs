using System.Net;
using Npgsql;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>NUI and unit can be edited; the old taxpayer stays in the archive and workstations register again.</summary>
public class TaxpayerChangeTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private static Dictionary<string, string> Form(string token, long nui, long unit) => new()
    {
        ["Input.Nui"] = nui.ToString(), ["Input.Name"] = "New Hotel SH.P.K.", ["Input.VatNo"] = "330099999",
        ["Input.FiscalizationNo"] = "EDI-NEW-1", ["Input.ApplicationId"] = "987654321", ["Input.BranchId"] = unit.ToString(),
        ["Input.BranchName"] = "New Hotel Prizren", ["Input.Location"] = "Prizren", ["Input.Address"] = "Rr. Shadervan 1",
        ["Input.OperaHotelCode"] = "NEWPZ", ["__RequestVerificationToken"] = token,
    };

    [PgFact]
    public async Task Taxpayer_change_waits_for_the_queue_then_moves_workstations_and_keeps_old_receipts()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var oldNui = await app.ScalarAsync<long>("SELECT business_nui FROM fiscal.branch WHERE active");
        var oldReceipts = await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.receipt WHERE business_nui = {oldNui}");
        Assert.True(oldReceipts > 0);

        // The demo data has receipts waiting for ATK: the change is refused until they are sent.
        Assert.True(await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.offline_queue") > 0);
        var token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business?Edit=true");
        var refused = await client.PostAsync("/Settings/Business", new FormUrlEncodedContent(Form(token, 811111111, 7)));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("still wait to be sent to ATK", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        Assert.Equal(oldNui, await app.ScalarAsync<long>("SELECT business_nui FROM fiscal.branch WHERE active"));

        // Queue sent (simulated), then the change goes through.
        await using (var conn = new NpgsqlConnection(app.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand("INSERT INTO fiscal.transmission (receipt_id, sent_by, outcome, http_status, atk_transaction_id) SELECT id, 'server', 'accepted', 200, 1 FROM fiscal.offline_queue", conn).ExecuteNonQueryAsync();
        }
        token = await ServerFixture.AntiforgeryTokenAsync(client, "/Settings/Business?Edit=true");
        var changed = await client.PostAsync("/Settings/Business", new FormUrlEncodedContent(Form(token, 811111111, 7)));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);

        Assert.Equal(811111111L, await app.ScalarAsync<long>("SELECT business_nui FROM fiscal.branch WHERE active"));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.branch WHERE active"));
        Assert.Null(await app.ScalarAsync<string>($"SELECT opera_hotel_code FROM fiscal.branch WHERE business_nui = {oldNui}"));
        Assert.Equal("NEWPZ", await app.ScalarAsync<string>("SELECT opera_hotel_code FROM fiscal.branch WHERE active"));
        // Old receipts untouched; workstations moved and must register again.
        Assert.Equal(oldReceipts, await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.receipt WHERE business_nui = {oldNui}"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.terminal WHERE business_nui <> 811111111"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.terminal WHERE certificate_pem IS NOT NULL OR status = 'active'"));
        Assert.Equal("987654321", await app.ScalarAsync<string>("SELECT value::text FROM fiscal.setting WHERE key = 'atk_application_id'"));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'TAXPAYER_CHANGED'"));

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Business"));
        Assert.Contains("New Hotel SH.P.K.", page);
        Assert.Contains("987654321", page);
        Assert.Contains("Register the", page); // message after the change
        // An old receipt still opens with the previous taxpayer's name.
        var oldId = await app.ScalarAsync<long>($"SELECT max(id) FROM fiscal.receipt WHERE business_nui = {oldNui}");
        Assert.Contains("Hotel Demo SH.P.K.", WebUtility.HtmlDecode(await client.GetStringAsync($"/Receipts/Detail/{oldId}")));
    }
}
