using Dapper;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

/// <summary>
/// "Start over with a new business": deletes all business data and settings, keeping users, roles and VAT rates.
/// Only for servers that never talked to ATK Production (the audit log, which cannot be altered, shows every switch to
/// Production). Production receipts are legal records: a new business then gets a new database.
/// Needs the database owner (the service's normal connection) because the archive tables are protected by triggers.
/// </summary>
public sealed class BusinessReset(NpgsqlDataSource db, AuditLog audit)
{
    public sealed record Deleted(long Receipts, long Workstations);

    /// <summary>Settings as the migrations create them.</summary>
    private static readonly (string Key, string Json)[] Defaults =
    [
        ("retention_years", "10"), ("backup_target", "\"\""), ("atk_environment", "\"Test\""), ("atk_application_id", "0"),
        ("atk_timeout_seconds", "10"), ("atk_retry_minutes", "2"), ("alert_emails", "\"\""), ("vat_rounding", "\"RoundTaxHalfUp\""),
        ("opera_overrides_enabled", "false"), ("flip_mode", "\"capture\""), ("flip_stub_status", "200"),
        ("flip_stub_content_type", "\"text/plain\""), ("flip_stub_body", "\"\""), ("signing_mode_default", "\"client\""),
        ("flip_auth_required", "false"), ("flip_auth_header", "\"Authorization\""), ("default_item_category", "\"TT\""),
        ("default_item_unit", "\"cope\""), ("validation_check_tax_number", "true"), ("validation_tolerance", "0.01"),
    ];

    public async Task<(long Receipts, bool ProductionUsed)> StatusAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var receipts = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM fiscal.receipt");
        var production = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM fiscal.setting WHERE key = 'atk_environment' AND value::text = '"Production"')
                OR EXISTS (SELECT 1 FROM fiscal.audit_log WHERE entity = 'setting' AND entity_id = 'atk_environment' AND details::text LIKE '%Production%')
            """);
        return (receipts, production);
    }

    public async Task<Deleted> ResetAsync(string actor, CancellationToken ct = default)
    {
        var (_, production) = await StatusAsync(ct);
        if (production)
            throw new InvalidOperationException("This server has been connected to ATK Production: its receipts are legal records and cannot be deleted. Install the application with a new database for the new business.");
        await using var conn = await db.OpenConnectionAsync(ct);
        var receipts = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM fiscal.receipt");
        var workstations = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM fiscal.terminal");
        string[] protectedTables = ["receipt", "transmission", "audit_log", "source_payload", "flip_message", "terminal"];
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            try
            {
                foreach (var t in protectedTables) await conn.ExecuteAsync($"ALTER TABLE fiscal.{t} DISABLE TRIGGER USER", transaction: tx);
                await conn.ExecuteAsync("""
                    TRUNCATE fiscal.transmission, fiscal.receipt, fiscal.source_payload, fiscal.flip_message, fiscal.audit_log,
                             fiscal.terminal, fiscal.branch, fiscal.business, fiscal.opera_trx_mapping, fiscal.opera_payment_mapping,
                             fiscal.setting RESTART IDENTITY CASCADE
                    """, transaction: tx);
                foreach (var t in protectedTables) await conn.ExecuteAsync($"ALTER TABLE fiscal.{t} ENABLE TRIGGER USER", transaction: tx);
                await conn.ExecuteAsync("ALTER SEQUENCE fiscal.coupon_seq RESTART", transaction: tx);
                foreach (var (key, json) in Defaults)
                    await conn.ExecuteAsync("INSERT INTO fiscal.setting (key, value, updated_by) VALUES (@key, @json::jsonb, 'reset')", new { key, json }, tx);
                await tx.CommitAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new InvalidOperationException("The database user of the service is not the owner of the database, so the archive cannot be cleared. Nothing was deleted.");
            }
        }
        // First entry of the new audit chain.
        await audit.WriteAsync(actor, AuditLog.Actions.BusinessReset, "business", null, new { deletedReceipts = receipts, deletedWorkstations = workstations }, ct: ct);
        return new Deleted(receipts, workstations);
    }
}
