using Dapper;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Hrs.Fiscal.Server.Pages;

public sealed class IndexModel(DashboardQueries dashboard, ReceiptQueries receipts, SettingsStore settings, PropertyClock clock, NpgsqlDataSource db, IAuthorizationService auth) : PageModel
{
    public DashboardStats Stats { get; private set; } = new();
    public IReadOnlyList<TerminalRow> Terminals { get; private set; } = [];
    public BusinessInfo? Business { get; private set; }
    public PropertyClock Clock => clock;
    public DateTime UtcNow { get; private set; }

    // Pipeline OPERA → FLIP → HRS → ATK
    public string FlipMode { get; private set; } = "capture";
    public string AtkEnvironment { get; private set; } = "Test";
    public DateTime? LastFlipAt { get; private set; }
    public string? LastFlipMode { get; private set; }
    public DateTime? LastAcceptedAt { get; private set; }
    public long FlipToday { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Business = await settings.BusinessAsync();
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
