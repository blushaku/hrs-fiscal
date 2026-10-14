using Dapper;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Hrs.Fiscal.Server.Pages;

public sealed class IndexModel(DashboardQueries dashboard, ReceiptQueries receipts, SettingsStore settings, PropertyClock clock, NpgsqlDataSource db, IAuthorizationService auth, Hrs.Fiscal.Server.Services.ClockStatus clockStatus) : PageModel
{
    public DashboardStats Stats { get; private set; } = new();
    public IReadOnlyList<TerminalRow> Terminals { get; private set; } = [];
    public BusinessInfo? Business { get; private set; }
    public int VatRates { get; private set; }
    public PropertyClock Clock => clock;
    public Hrs.Fiscal.Server.Services.ClockStatus ClockCheck => clockStatus;
    public DateTime UtcNow { get; private set; }

    // Pipeline OPERA → FLIP → HRS → ATK
    public string FlipMode { get; private set; } = "capture";
    public string AtkEnvironment { get; private set; } = "Test";
    public DateTime? LastFlipAt { get; private set; }
    public string? LastFlipMode { get; private set; }
    public DateTime? LastAcceptedAt { get; private set; }
    public long FlipToday { get; private set; }
    /// <summary>Receipts of the last 24 hours whose printing failed and that have not been printed since (thermal printing).</summary>
    public IReadOnlyList<(long ReceiptId, long CouponId, string Error)> Unprinted { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Business = await settings.BusinessAsync();
        VatRates = (await settings.VatRatesAsync()).Count;
        // Setting up the business is the first step: administrators go straight there until it exists.
        if (Business is null && (await auth.AuthorizeAsync(User, "perm:" + Hrs.Fiscal.Server.Security.Permissions.SettingsManage)).Succeeded)
            return RedirectToPage("/Settings/Business", new { Edit = true });
        Stats = await dashboard.TodayAsync();
        Terminals = await receipts.TerminalsAsync();
        UtcNow = clock.Time.GetUtcNow().UtcDateTime;
        FlipMode = await settings.GetAsync("flip_mode", "capture") ?? "capture";
        AtkEnvironment = await settings.GetAsync("atk_environment", "Test") ?? "Test";
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(clock.LocalNow.Date, DateTimeKind.Unspecified), clock.TimeZone);
        await using var conn = await db.OpenConnectionAsync();
        (LastFlipAt, LastFlipMode) = await conn.QueryFirstOrDefaultAsync<(DateTime?, string?)>(
            "SELECT received_at, mode FROM fiscal.flip_message ORDER BY id DESC LIMIT 1");
        FlipToday = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM fiscal.flip_message WHERE received_at >= @dayStart", new { dayStart });
        LastAcceptedAt = await conn.ExecuteScalarAsync<DateTime?>("SELECT max(attempted_at) FROM fiscal.transmission WHERE outcome = 'accepted'");
        Unprinted = (await conn.QueryAsync<(long, long, string)>("""
            SELECT DISTINCT ON (f.entity_id) f.entity_id::bigint, (f.details->>'couponId')::bigint, f.details->>'error'
            FROM fiscal.audit_log f
            WHERE f.action = 'PRINT_FAILED' AND f.at > now() - interval '24 hours'
              AND NOT EXISTS (SELECT 1 FROM fiscal.audit_log p WHERE p.action = 'RECEIPT_PRINTED' AND p.entity_id = f.entity_id AND p.id > f.id)
            ORDER BY f.entity_id, f.id DESC
            LIMIT 20
            """)).ToList();
        return Page();
    }

    public string Ago(DateTime? utc)
    {
        if (utc is not { } t) return "never";
        var span = UtcNow - DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
            : clock.FormatShort(DateTime.SpecifyKind(t, DateTimeKind.Utc));
    }
}
