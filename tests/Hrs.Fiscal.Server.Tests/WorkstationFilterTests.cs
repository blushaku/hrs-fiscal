using System.Net;
using System.Text;

namespace Hrs.Fiscal.Server.Tests;

public class WorkstationFilterTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [PgFact]
    public async Task Receipts_audit_and_summary_can_be_filtered_and_exported_by_workstation()
    {
        var client = await app.SignedInAsync("supervisor", "demo-password-1");
        var t12 = await app.ScalarAsync<long>("SELECT id FROM fiscal.terminal WHERE pos_id = 12");
        var from = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        // Receipts CSV for one workstation: only its receipts, file name and audit entry say which.
        var csv = await client.GetAsync($"/export/receipts?format=csv&from={from}&to={to}&terminal={t12}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains("pos12", csv.Content.Headers.ContentDisposition!.FileName);
        var lines = (await csv.Content.ReadAsStringAsync()).Trim().Split('\n').Skip(1).ToList();
        var expected = await app.ScalarAsync<long>($"SELECT count(*) FROM fiscal.receipt WHERE terminal_id = {t12}");
        Assert.Equal(expected, lines.Count);
        Assert.All(lines, l => Assert.Contains(",12,", l));
        Assert.Equal(12L, await app.ScalarAsync<long>("SELECT (details->>'workstation')::bigint FROM fiscal.audit_log WHERE action = 'EXPORT_CSV' ORDER BY id DESC LIMIT 1"));

        // Audit log page and export filtered to the workstation: only entries linked to it or its receipts.
        var page = await client.GetStringAsync($"/Audit?Terminal={t12}");
        Assert.Contains("Export this view (this workstation only)", page);
        var audit = await (await client.GetAsync($"/export/audit?format=csv&terminal={t12}")).Content.ReadAsStringAsync();
        var auditRows = audit.Trim().Split('\n').Skip(1).ToList();
        Assert.NotEmpty(auditRows);
        Assert.DoesNotContain(auditRows, r => r.Contains("LOGIN"));

        // Summary by workstation: one row per workstation plus a total; the PDF works too.
        var summary = await (await client.GetAsync($"/export/summary?format=csv&from={from}&to={to}")).Content.ReadAsStringAsync();
        var sumRows = summary.Trim().Split('\n');
        Assert.StartsWith("﻿POS ID", sumRows[0]);
        Assert.Contains(sumRows, r => r.StartsWith("12,"));
        Assert.Contains(sumRows, r => r.Contains(",Total,"));
        var pdf = await client.GetAsync($"/export/summary?format=pdf&from={from}&to={to}&terminal={t12}");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
    }
}
