using System.Text.Json;
using Dapper;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

public sealed class BusinessInfo
{
    public long Nui { get; set; }
    public string Name { get; set; } = "";
    public string FiscalizationNo { get; set; } = "";
    public string? VatNo { get; set; }
    public long BranchId { get; set; }
    public string BranchName { get; set; } = "";
    public string Location { get; set; } = "";
    public string? Address { get; set; }
    public string? OperaHotelCode { get; set; }
}

public sealed class TerminalEdit
{
    public long Id { get; set; }
    public long PosId { get; set; }
    public string? OperaTerminalId { get; set; }
    public string Hostname { get; set; } = "";
    public string? ClientEndpoint { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime? CertificateExpires { get; set; }
    /// <summary>Per-workstation override: "server", "client" or null (= property default).</summary>
    public string? SigningMode { get; set; }
    /// <summary>Where the current key and certificate live ("server" / "client"); null = not registered.</summary>
    public string? EnrolledMode { get; set; }
    public string? KeyReference { get; set; }

    public string EffectiveMode(string propertyDefault) => SigningMode ?? propertyDefault;

    public TerminalSigningState SigningState(string propertyDefault, DateTime? nowUtc = null) =>
        Status == "disabled" ? TerminalSigningState.Disabled
        : EnrolledMode is null || CertificateExpires is null ? TerminalSigningState.NotRegistered
        : EnrolledMode != EffectiveMode(propertyDefault) ? TerminalSigningState.RegisterAgain
        : CertificateExpires <= (nowUtc ?? DateTime.UtcNow) ? TerminalSigningState.Expired
        : TerminalSigningState.Ready;
}

public enum TerminalSigningState { Ready, NotRegistered, RegisterAgain, Expired, Disabled }

public sealed class TrxMapping
{
    public string TrxCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string Unit { get; set; } = "cope";
    public string Category { get; set; } = "";
    public string VatLetter { get; set; } = "E";
    public bool Active { get; set; } = true;
}

public sealed class PaymentMapping
{
    public string PaymentCode { get; set; } = "";
    public string Description { get; set; } = "";
    public short AtkPaymentType { get; set; } = 1;
}

public sealed class VatRateRow
{
    public string Letter { get; set; } = "";
    public decimal Percent { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>Configuration stored in the database. Every write records old and new values in the audit log.</summary>
public sealed class SettingsStore(NpgsqlDataSource db, AuditLog audit)
{
    // ---- key/value settings -------------------------------------------------------------

    public async Task<Dictionary<string, JsonElement>> AllAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Key, string Value)>("SELECT key, value::text FROM fiscal.setting");
        return rows.ToDictionary(r => r.Key, r => JsonDocument.Parse(r.Value).RootElement.Clone());
    }

    public async Task<T?> GetAsync<T>(string key, T? fallback = default, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var json = await conn.ExecuteScalarAsync<string?>("SELECT value::text FROM fiscal.setting WHERE key = @key", new { key });
        return json is null ? fallback : JsonSerializer.Deserialize<T>(json);
    }

    public async Task<int> SetManyAsync(IReadOnlyDictionary<string, object?> values, string actor, CancellationToken ct = default)
    {
        var current = await AllAsync(ct);
        var changed = 0;
        await using var conn = await db.OpenConnectionAsync(ct);
        foreach (var (key, value) in values)
        {
            var newJson = JsonSerializer.Serialize(value);
            var oldJson = current.TryGetValue(key, out var old) ? old.GetRawText() : null;
            if (oldJson == newJson) continue;
            await conn.ExecuteAsync("""
                INSERT INTO fiscal.setting (key, value, updated_by) VALUES (@key, @value::jsonb, @actor)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = now(), updated_by = excluded.updated_by
                """, new { key, value = newJson, actor });
            await audit.WriteAsync(actor, AuditLog.Actions.SettingChanged, "setting", key, new { old = oldJson, @new = newJson }, ct: ct);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Writes settings whose values must not appear in the audit log (e.g. the FLIP token hash). The caller writes its own
    /// audit entry; here only the keys are logged.
    /// </summary>
    public async Task SetSecretAsync(IReadOnlyDictionary<string, object?> values, string actor, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (key, value) in values)
            await conn.ExecuteAsync("""
                INSERT INTO fiscal.setting (key, value, updated_by) VALUES (@key, @value::jsonb, @actor)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = now(), updated_by = excluded.updated_by
                """, new { key, value = JsonSerializer.Serialize(value), actor }, tx);
        await tx.CommitAsync(ct);
    }

    /// <summary>Whether the optional per-code OPERA overrides are applied (setting opera_overrides_enabled).</summary>
    public async Task<bool> OperaOverridesEnabledAsync(CancellationToken ct = default) =>
        await GetAsync("opera_overrides_enabled", false, ct);

    /// <summary>Property-wide signing mode (setting signing_mode_default): "client" (default) or "server".</summary>
    public async Task<string> SigningModeDefaultAsync(CancellationToken ct = default) =>
        await GetAsync("signing_mode_default", "client", ct) is "server" ? "server" : "client";

    // ---- business / branch --------------------------------------------------------------

    public async Task<BusinessInfo?> BusinessAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<BusinessInfo>("""
            SELECT b.nui, b.name, b.fiscalization_no, b.vat_no, br.branch_id, br.name AS branch_name, br.location,
                   br.address, br.opera_hotel_code
            FROM fiscal.business b JOIN fiscal.branch br ON br.business_nui = b.nui
            ORDER BY br.branch_id LIMIT 1
            """);
    }

    /// <summary>
    /// Creates the business and branch on first setup. Afterwards NUI and branch id are fixed (receipts reference them);
    /// names, VAT number, address and OPERA code stay editable.
    /// </summary>
    public async Task SaveBusinessAsync(BusinessInfo b, string actor, CancellationToken ct = default)
    {
        var before = await BusinessAsync(ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (before is null)
        {
            await conn.ExecuteAsync("INSERT INTO fiscal.business (nui, name, fiscalization_no, vat_no) VALUES (@Nui, @Name, @FiscalizationNo, @VatNo)", b, tx);
            await conn.ExecuteAsync("""
                INSERT INTO fiscal.branch (business_nui, branch_id, name, location, address, opera_hotel_code)
                VALUES (@Nui, @BranchId, @BranchName, @Location, @Address, @OperaHotelCode)
                """, b, tx);
        }
        else
        {
            if (before.Nui != b.Nui || before.BranchId != b.BranchId)
                throw new InvalidOperationException("NUI and unit number cannot be changed once receipts can reference them.");
            await conn.ExecuteAsync("UPDATE fiscal.business SET name = @Name, fiscalization_no = @FiscalizationNo, vat_no = @VatNo WHERE nui = @Nui", b, tx);
            await conn.ExecuteAsync("""
                UPDATE fiscal.branch SET name = @BranchName, location = @Location, address = @Address, opera_hotel_code = @OperaHotelCode
                WHERE business_nui = @Nui AND branch_id = @BranchId
                """, b, tx);
        }
        await tx.CommitAsync(ct);
        await audit.WriteAsync(actor, AuditLog.Actions.SettingChanged, "business", b.Nui.ToString(), new { old = before, @new = b }, ct: ct);
    }

    // ---- terminals ------------------------------------------------------------------------

    public async Task<IReadOnlyList<TerminalEdit>> TerminalsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TerminalEdit>("""
            SELECT id, pos_id, opera_terminal_id, hostname, client_endpoint, description, status, certificate_expires,
                   signing_mode, enrolled_mode, key_reference
            FROM fiscal.terminal ORDER BY pos_id
            """)).ToList();
    }

    public async Task SaveTerminalAsync(TerminalEdit t, string actor, CancellationToken ct = default)
    {
        var business = await BusinessAsync(ct) ?? throw new InvalidOperationException("Set up the business first.");
        await using var conn = await db.OpenConnectionAsync(ct);
        TerminalEdit? before = null;
        if (t.Id == 0)
        {
            t.Id = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO fiscal.terminal (business_nui, branch_id, pos_id, opera_terminal_id, hostname, client_endpoint, description, status, signing_mode)
                VALUES (@nui, @branch, @PosId, @OperaTerminalId, @Hostname, @ClientEndpoint, @Description, @Status, @SigningMode)
                RETURNING id
                """, new { nui = business.Nui, branch = business.BranchId, t.PosId, t.OperaTerminalId, t.Hostname, t.ClientEndpoint, t.Description, t.Status, t.SigningMode });
        }
        else
        {
            before = (await TerminalsAsync(ct)).SingleOrDefault(x => x.Id == t.Id) ?? throw new InvalidOperationException("Unknown terminal.");
            // Only ATK registration (TerminalEnrollment / the client) may activate a workstation.
            if (t.Status == "active" && before.Status != "active" && before.EnrolledMode is null)
                throw new InvalidOperationException("Register the workstation with ATK before activating it.");
            // PosId is part of the ATK identity (certificate OU) and of issued receipts: not editable.
            await conn.ExecuteAsync("""
                UPDATE fiscal.terminal SET opera_terminal_id = @OperaTerminalId, hostname = @Hostname, client_endpoint = @ClientEndpoint,
                       description = @Description, status = @Status, signing_mode = @SigningMode
                WHERE id = @Id
                """, t);
        }
        await audit.WriteAsync(actor, AuditLog.Actions.TerminalChanged, "terminal", t.Id.ToString(), new { old = before, @new = t }, t.Id, ct);
    }

    // ---- OPERA overrides (optional) ---------------------------------------------------------
    // All receipt fields come from OPERA via FLIP. These per-code entries, when present, override what OPERA sends.

    public async Task<IReadOnlyList<TrxMapping>> TrxMappingsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TrxMapping>(
            "SELECT trx_code, item_name, unit, category, vat_letter, active FROM fiscal.opera_trx_mapping ORDER BY trx_code")).ToList();
    }

    public async Task SaveTrxMappingAsync(TrxMapping m, string actor, CancellationToken ct = default)
    {
        var business = await BusinessAsync(ct) ?? throw new InvalidOperationException("Set up the business first.");
        var before = (await TrxMappingsAsync(ct)).SingleOrDefault(x => x.TrxCode == m.TrxCode);
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO fiscal.opera_trx_mapping (branch_id, trx_code, item_name, unit, category, vat_letter, active, updated_by)
            VALUES (@branch, @TrxCode, @ItemName, @Unit, @Category, @VatLetter, @Active, @actor)
            ON CONFLICT (branch_id, trx_code) DO UPDATE SET item_name = excluded.item_name, unit = excluded.unit,
                category = excluded.category, vat_letter = excluded.vat_letter, active = excluded.active,
                updated_at = now(), updated_by = excluded.updated_by
            """, new { branch = business.BranchId, m.TrxCode, m.ItemName, m.Unit, m.Category, m.VatLetter, m.Active, actor });
        await audit.WriteAsync(actor, AuditLog.Actions.MappingChanged, "opera_trx_mapping", m.TrxCode, new { old = before, @new = m }, ct: ct);
    }

    public async Task<IReadOnlyList<PaymentMapping>> PaymentMappingsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<PaymentMapping>(
            "SELECT payment_code, description, atk_payment_type FROM fiscal.opera_payment_mapping ORDER BY payment_code")).ToList();
    }

    public async Task SavePaymentMappingAsync(PaymentMapping m, string actor, CancellationToken ct = default)
    {
        var business = await BusinessAsync(ct) ?? throw new InvalidOperationException("Set up the business first.");
        var before = (await PaymentMappingsAsync(ct)).SingleOrDefault(x => x.PaymentCode == m.PaymentCode);
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO fiscal.opera_payment_mapping (branch_id, payment_code, description, atk_payment_type, updated_by)
            VALUES (@branch, @PaymentCode, @Description, @AtkPaymentType, @actor)
            ON CONFLICT (branch_id, payment_code) DO UPDATE SET description = excluded.description,
                atk_payment_type = excluded.atk_payment_type, updated_at = now(), updated_by = excluded.updated_by
            """, new { branch = business.BranchId, m.PaymentCode, m.Description, m.AtkPaymentType, actor });
        await audit.WriteAsync(actor, AuditLog.Actions.MappingChanged, "opera_payment_mapping", m.PaymentCode, new { old = before, @new = m }, ct: ct);
    }

    // ---- VAT ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<VatRateRow>> VatRatesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<VatRateRow>("SELECT letter, percent, description FROM fiscal.vat_rate ORDER BY letter")).ToList();
    }

    public async Task SaveVatRateAsync(VatRateRow v, string actor, CancellationToken ct = default)
    {
        var before = (await VatRatesAsync(ct)).SingleOrDefault(x => x.Letter == v.Letter) ?? throw new InvalidOperationException("Unknown VAT letter.");
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE fiscal.vat_rate SET percent = @Percent, description = @Description WHERE letter = @Letter", v);
        await audit.WriteAsync(actor, AuditLog.Actions.SettingChanged, "vat_rate", v.Letter, new { old = before, @new = v }, ct: ct);
    }
}
