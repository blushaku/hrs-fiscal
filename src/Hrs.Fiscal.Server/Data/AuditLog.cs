using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

/// <summary>Writes and reads the append-only, hash-chained audit trail.</summary>
public sealed class AuditLog(NpgsqlDataSource db, PropertyClock clock)
{
    public static class Actions
    {
        public const string Login = "LOGIN", LoginFailed = "LOGIN_FAILED", Logout = "LOGOUT";
        public const string ReceiptIssued = "RECEIPT_ISSUED", ReceiptAccepted = "RECEIPT_ACCEPTED", ReceiptRejected = "RECEIPT_REJECTED";
        public const string QueuedOffline = "QUEUED_OFFLINE", Reprint = "REPRINT", ReceiptViewed = "RECEIPT_VIEWED";
        public const string ExportCsv = "EXPORT_CSV", ExportPdf = "EXPORT_PDF";
        public const string IntegrityCheck = "INTEGRITY_CHECK", SettingChanged = "SETTING_CHANGED";
        public const string UserCreated = "USER_CREATED", UserChanged = "USER_CHANGED";
        public const string TerminalChanged = "TERMINAL_CHANGED", TerminalEnrolled = "TERMINAL_ENROLLED",
            FlipTokenCreated = "FLIP_TOKEN_CREATED", FlipAuthFailed = "FLIP_AUTH_FAILED", FolioRefused = "FOLIO_REFUSED", MappingChanged = "MAPPING_CHANGED";
    }

    public async Task WriteAsync(string actor, string action, string? entity = null, string? entityId = null,
        object? details = null, long? terminalId = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO fiscal.audit_log (actor, terminal_id, action, entity, entity_id, details)
            VALUES (@actor, @terminalId, @action, @entity, @entityId, @details::jsonb)
            """,
            new { actor, terminalId, action, entity, entityId, details = JsonSerializer.Serialize(details ?? new { }) });
    }

    public async Task<Paged<AuditRow>> SearchAsync(AuditFilter f, CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(f);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 1000);
        args.Add("limit", size);
        args.Add("offset", (page - 1) * size);

        await using var conn = await db.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM fiscal.audit_log a {where}", args);
        var rows = await conn.QueryAsync<AuditRow>($"""
            SELECT a.id, a.at, a.actor, coalesce(t.opera_terminal_id, t.hostname) AS terminal_label,
                   a.action, a.entity, a.entity_id, a.details::text AS details
            FROM fiscal.audit_log a LEFT JOIN fiscal.terminal t ON t.id = a.terminal_id
            {where} ORDER BY a.id DESC LIMIT @limit OFFSET @offset
            """, args);
        return new Paged<AuditRow>(rows.ToList(), total, page, size);
    }

    public async IAsyncEnumerable<AuditRow> StreamAsync(AuditFilter f, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (where, args) = BuildWhere(f);
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = conn.QueryUnbufferedAsync<AuditRow>($"""
            SELECT a.id, a.at, a.actor, coalesce(t.opera_terminal_id, t.hostname) AS terminal_label,
                   a.action, a.entity, a.entity_id, a.details::text AS details
            FROM fiscal.audit_log a LEFT JOIN fiscal.terminal t ON t.id = a.terminal_id
            {where} ORDER BY a.id
            """, args);
        await foreach (var row in rows.WithCancellation(ct)) yield return row!;
    }

    public async Task<long> CountRecentAsync(string action, string entityId, TimeSpan window, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM fiscal.audit_log WHERE action = @action AND entity_id = @entityId AND at > now() - @window",
            new { action, entityId, window });
    }

    public async Task<IReadOnlyList<string>> ActionsInUseAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<string>("SELECT DISTINCT action FROM fiscal.audit_log ORDER BY action")).ToList();
    }

    /// <summary>Runs fiscal.verify_chain() for both chains. Null = intact.</summary>
    public async Task<IntegrityResult> VerifyAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var receipt = await conn.QueryFirstOrDefaultAsync<(long BrokenId, string Reason)?>("SELECT broken_id, reason FROM fiscal.verify_chain('receipt')");
        var audit = await conn.QueryFirstOrDefaultAsync<(long BrokenId, string Reason)?>("SELECT broken_id, reason FROM fiscal.verify_chain('audit_log')");
        var counts = await conn.QuerySingleAsync<(long Receipts, long Audit)>("SELECT (SELECT count(*) FROM fiscal.receipt), (SELECT count(*) FROM fiscal.audit_log)");
        return new IntegrityResult(receipt, audit, counts.Receipts, counts.Audit);
    }

    private (string Where, DynamicParameters Args) BuildWhere(AuditFilter f)
    {
        var sb = new StringBuilder("WHERE true");
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(f.Action)) { sb.Append(" AND a.action = @action"); args.Add("action", f.Action); }
        if (f.TerminalId is not null)
        {
            sb.Append(" AND (a.terminal_id = @terminal OR (a.entity = 'receipt' AND a.entity_id IN (SELECT id::text FROM fiscal.receipt WHERE terminal_id = @terminal)))");
            args.Add("terminal", f.TerminalId);
        }
        if (!string.IsNullOrWhiteSpace(f.Query))
        {
            sb.Append(" AND (a.actor ILIKE @like OR a.entity_id = @q OR a.details::text ILIKE @like)");
            args.Add("q", f.Query.Trim());
            args.Add("like", "%" + f.Query.Trim().Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        var (from, to) = DbSetup.ToUtcRange(f.From, f.To, clock.TimeZone);
        if (from is not null) { sb.Append(" AND a.at >= @from"); args.Add("from", from); }
        if (to is not null) { sb.Append(" AND a.at < @to"); args.Add("to", to); }
        return (sb.ToString(), args);
    }
}

public sealed record IntegrityResult((long BrokenId, string Reason)? Receipt, (long BrokenId, string Reason)? Audit, long ReceiptCount, long AuditCount)
{
    public bool Intact => Receipt is null && Audit is null;
}
