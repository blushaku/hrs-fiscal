using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages;

public sealed class IndexModel(DashboardQueries dashboard, ReceiptQueries receipts, SettingsStore settings, PropertyClock clock) : PageModel
{
    public DashboardStats Stats { get; private set; } = new();
    public IReadOnlyList<TerminalRow> Terminals { get; private set; } = [];
    public BusinessInfo? Business { get; private set; }
    public PropertyClock Clock => clock;
    public DateTime UtcNow { get; private set; }

    public async Task OnGetAsync()
    {
        Stats = await dashboard.TodayAsync();
        Terminals = await receipts.TerminalsAsync();
        Business = await settings.BusinessAsync();
        UtcNow = clock.Time.GetUtcNow().UtcDateTime;
    }
}
