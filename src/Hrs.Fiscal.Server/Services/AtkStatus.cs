using Dapper;
using Hrs.Fiscal.Core.Client;
using Hrs.Fiscal.Server.Data;
using Npgsql;

namespace Hrs.Fiscal.Server.Services;

/// <summary>
/// Whether ATK is reachable, from the two contacts that really reach ATK: every sending of a receipt (at issue, by the
/// offline queue, manual receipts) and the clock check's request to ATK's server. The most recent contact wins.
/// Shown to the front desk by the Fiscal Client (offline notice, Doc. 01-06-1967 Art 26.13) and on the dashboard.
/// </summary>
public sealed class AtkStatus
{
    private readonly object _gate = new();
    public DateTime? LastContactUtc { get; private set; }
    public bool? LastOk { get; private set; }
    /// <summary>"send", "queue" or "clock".</summary>
    public string? LastSource { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>First failed contact of the current offline period.</summary>
    public DateTime? OfflineSinceUtc { get; private set; }
    public bool Offline => LastOk == false;

    public void Record(bool ok, string source, string? error = null, DateTime? atUtc = null)
    {
        var at = atUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (LastContactUtc is { } previous && at < previous) return;   // an older answer arriving late
            LastContactUtc = at; LastOk = ok; LastSource = source; LastError = ok ? null : error;
            OfflineSinceUtc = ok ? null : OfflineSinceUtc ?? at;
        }
    }
}

/// <summary>ATK status plus the offline queue, as sent to the Fiscal Clients.</summary>
public sealed class AtkHealthQuery(AtkStatus status, NpgsqlDataSource db, SettingsStore settings, IConfiguration config)
{
    public async Task<AtkHealth> GetAsync(CancellationToken ct = default)
    {
        (long Pending, DateTime? Oldest) queue;
        await using (var conn = await db.OpenConnectionAsync(ct))
            queue = await conn.QuerySingleAsync<(long, DateTime?)>("SELECT count(*), min(issued_at) FROM fiscal.offline_queue");
        var oldest = queue.Oldest is { } o ? DateTime.SpecifyKind(o, DateTimeKind.Utc) : (DateTime?)null;
        var state = status.Offline ? AtkStates.Offline
            : queue.Pending > 0 ? AtkStates.Pending
            : status.LastOk == true ? AtkStates.Online
            : AtkStates.Unknown;
        return new AtkHealth(state, await settings.GetAsync("atk_environment", "Test", ct) ?? "Test", (int)queue.Pending, oldest,
            oldest?.AddHours(48), status.OfflineSinceUtc, status.LastContactUtc, status.LastError, config["I18n:DefaultLanguage"] ?? "sq");
    }

    public static async Task<long> PendingAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM fiscal.offline_queue");
    }
}
