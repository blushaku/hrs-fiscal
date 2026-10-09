using System.Text;
using Dapper;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

public sealed class ReceiptQueries(NpgsqlDataSource db, PropertyClock clock)
{
    private const string ListColumns = """
        r.id, r.coupon_id, r.verification_no, r.coupon_type, r.reference_coupon_id, r.source_document,
        r.operator_id, coalesce(t.opera_terminal_id, t.hostname) AS terminal_label, r.pos_id, r.issued_at,
        r.issued_offline, r.total_cents, s.status, s.atk_transaction_id
        """;

    private const string FromClause = """
        FROM fiscal.receipt r
        JOIN fiscal.receipt_status s ON s.id = r.id
        JOIN fiscal.terminal t ON t.id = r.terminal_id
        """;

    public async Task<Paged<ReceiptListRow>> SearchAsync(ReceiptFilter f, CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(f);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 500);
        args.Add("limit", size);
        args.Add("offset", (page - 1) * size);

        await using var conn = await db.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>($"SELECT count(*) {FromClause} {where}", args);
        var rows = await conn.QueryAsync<ReceiptListRow>(
            $"SELECT {ListColumns} {FromClause} {where} ORDER BY r.issued_at DESC, r.id DESC LIMIT @limit OFFSET @offset", args);
        return new Paged<ReceiptListRow>(rows.ToList(), total, page, size);
    }

    /// <summary>Streams all matching rows (for exports) without paging.</summary>
    public async IAsyncEnumerable<ReceiptListRow> StreamAsync(ReceiptFilter f, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(f);
        await using var conn = await db.OpenConnectionAsync(ct);
        var reader = conn.QueryUnbufferedAsync<ReceiptListRow>(
            $"SELECT {ListColumns} {FromClause} {where} ORDER BY r.issued_at, r.id", args);
        await foreach (var row in reader.WithCancellation(ct)) yield return row!;
    }

    public async Task<ReceiptDetail?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ReceiptDetail>("""
            SELECT r.*, coalesce(t.opera_terminal_id, t.hostname) AS terminal_label, s.status, s.atk_transaction_id,
                   b.name AS business_name, b.vat_no, br.name AS branch_name, br.location, br.address
            FROM fiscal.receipt r
            JOIN fiscal.receipt_status s ON s.id = r.id
            JOIN fiscal.terminal t ON t.id = r.terminal_id
            JOIN fiscal.business b ON b.nui = r.business_nui
            JOIN fiscal.branch br ON br.business_nui = r.business_nui AND br.branch_id = r.branch_id
            WHERE r.id = @id
            """, new { id });
    }

    public async Task<IReadOnlyList<TransmissionRow>> TransmissionsAsync(long receiptId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TransmissionRow>(
            "SELECT attempted_at, sent_by, outcome, http_status, atk_transaction_id, message FROM fiscal.transmission WHERE receipt_id = @receiptId ORDER BY attempted_at, id",
            new { receiptId })).ToList();
    }

    public async Task<SourcePayloadRow?> SourcePayloadAsync(long receiptId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SourcePayloadRow>("""
            SELECT p.source, p.source_event_id, p.content_type, p.body, p.received_at
            FROM fiscal.source_payload p JOIN fiscal.receipt r ON r.source_payload_id = p.id
            WHERE r.id = @receiptId
            """, new { receiptId });
    }

    /// <summary>Receipts linked to this one: the original it refers to, and returns that refer to it.</summary>
    public async Task<IReadOnlyList<ReceiptListRow>> RelatedAsync(ReceiptDetail r, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ReceiptListRow>(
            $"SELECT {ListColumns} {FromClause} WHERE r.coupon_id = @refId OR r.reference_coupon_id = @couponId ORDER BY r.issued_at",
            new { refId = r.ReferenceCouponId ?? -1, couponId = r.CouponId })).ToList();
    }

    public async Task<IReadOnlyList<TerminalRow>> TerminalsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TerminalRow>("""
            SELECT t.id, t.pos_id, t.opera_terminal_id, t.hostname, t.status, t.certificate_expires,
                   (SELECT max(issued_at) FROM fiscal.receipt r WHERE r.terminal_id = t.id) AS last_receipt_at,
                   (SELECT count(*) FROM fiscal.offline_queue q WHERE q.terminal_id = t.id) AS pending_count
            FROM fiscal.terminal t ORDER BY t.branch_id, t.pos_id
            """)).ToList();
    }

    /// <summary>Totals per workstation for a period (sales minus returns), with ATK status counts.</summary>
    public async Task<IReadOnlyList<WorkstationSummary>> SummaryAsync(ReceiptFilter f, CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(f with { Status = null });
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<WorkstationSummary>($"""
            SELECT r.pos_id, coalesce(t.opera_terminal_id, t.hostname) AS terminal_label, t.hostname,
                   count(*) FILTER (WHERE r.coupon_type = 1) AS sales,
                   count(*) FILTER (WHERE r.coupon_type = 3) AS returns,
                   coalesce(sum(r.total_cents) FILTER (WHERE r.coupon_type = 1), 0) AS sales_cents,
                   coalesce(sum(r.total_cents) FILTER (WHERE r.coupon_type = 3), 0) AS returns_cents,
                   coalesce(sum(CASE WHEN r.coupon_type = 3 THEN -r.total_tax_cents ELSE r.total_tax_cents END), 0) AS net_tax_cents,
                   count(*) FILTER (WHERE s.status = 'accepted') AS accepted,
                   count(*) FILTER (WHERE s.status = 'pending') AS pending,
                   count(*) FILTER (WHERE s.status = 'rejected') AS rejected,
                   min(r.issued_at) AS first_at, max(r.issued_at) AS last_at
            {FromClause} {where}
            GROUP BY r.pos_id, t.opera_terminal_id, t.hostname ORDER BY r.pos_id
            """, args)).ToList();
    }

    private (string Where, DynamicParameters Args) BuildWhere(ReceiptFilter f)
    {
        var sb = new StringBuilder("WHERE true");
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(f.Query))
        {
            sb.Append(" AND (r.coupon_id::text = @q OR r.verification_no = upper(@q) OR r.source_document ILIKE @like OR r.operator_id ILIKE @like)");
            args.Add("q", f.Query.Trim());
            args.Add("like", "%" + f.Query.Trim().Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        var (from, to) = DbSetup.ToUtcRange(f.From, f.To, clock.TimeZone);
        if (from is not null) { sb.Append(" AND r.issued_at >= @from"); args.Add("from", from); }
        if (to is not null) { sb.Append(" AND r.issued_at < @to"); args.Add("to", to); }
        if (f.TerminalId is not null) { sb.Append(" AND r.terminal_id = @terminal"); args.Add("terminal", f.TerminalId); }
        if (f.Status is "accepted" or "pending" or "rejected") { sb.Append(" AND s.status = @status"); args.Add("status", f.Status); }
        if (f.Type is not null) { sb.Append(" AND r.coupon_type = @type"); args.Add("type", f.Type); }
        return (sb.ToString(), args);
    }
}

public sealed class DashboardQueries(NpgsqlDataSource db, PropertyClock clock)
{
    public async Task<DashboardStats> TodayAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var (from, to) = DbSetup.ToUtcRange(today, today, clock.TimeZone);
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<DashboardStats>("""
            SELECT
              (SELECT count(*) FROM fiscal.receipt_status WHERE issued_at >= @from AND issued_at < @to) AS issued_today,
              (SELECT coalesce(sum(total_cents), 0) FROM fiscal.receipt_status WHERE issued_at >= @from AND issued_at < @to) AS total_today_cents,
              (SELECT count(*) FROM fiscal.receipt_status WHERE issued_at >= @from AND issued_at < @to AND status = 'accepted') AS accepted_today,
              (SELECT count(*) FROM fiscal.offline_queue) AS pending,
              (SELECT min(issued_at) FROM fiscal.offline_queue) AS oldest_pending_issued_at,
              (SELECT count(*) FROM fiscal.offline_queue WHERE overdue_48h) AS overdue48h,
              (SELECT count(*) FROM fiscal.receipt_status WHERE issued_at >= @from AND issued_at < @to AND status = 'rejected') AS rejected_today,
              (SELECT count(*) FROM fiscal.receipt_status WHERE issued_at >= now() - interval '30 days' AND status = 'rejected') AS rejected30days
            """, new { from, to });
    }
}
