using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Audit;

public sealed class IndexModel(AuditLog audit, PropertyClock clock, ReceiptQueries receipts, Hrs.Fiscal.Server.Security.RolePermissions perms) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public long? Terminal { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<AuditRow> Result { get; private set; } = new([], 0, 1, 100);
    public IReadOnlyList<string> ActionsInUse { get; private set; } = [];
    public IReadOnlyList<TerminalRow> Terminals { get; private set; } = [];
    public IntegrityResult? Integrity { get; private set; }
    public PropertyClock Clock => clock;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCheckAsync()
    {
        if (!(await perms.ForUserAsync(User)).Contains(Hrs.Fiscal.Server.Security.Permissions.AuditIntegrity)) return Forbid();
        Integrity = await audit.VerifyAsync();
        await audit.WriteAsync(User.Identity!.Name!, AuditLog.Actions.IntegrityCheck, details: new
        {
            intact = Integrity.Intact,
            receipts = Integrity.ReceiptCount,
            auditEntries = Integrity.AuditCount,
            receiptBreak = Integrity.Receipt?.BrokenId,
            auditBreak = Integrity.Audit?.BrokenId,
        });
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        ActionsInUse = await audit.ActionsInUseAsync();
        Terminals = await receipts.TerminalsAsync();
        Result = await audit.SearchAsync(new AuditFilter { Query = Q, Action = Action, TerminalId = Terminal, From = From, To = To, Page = P });
    }

    public string PageLink(int page) => "/Audit" + Microsoft.AspNetCore.Http.QueryString.Create(new Dictionary<string, string?>
    {
        ["Q"] = Q, ["Action"] = Action, ["Terminal"] = Terminal?.ToString(), ["From"] = From?.ToString("yyyy-MM-dd"), ["To"] = To?.ToString("yyyy-MM-dd"), ["P"] = page.ToString(),
    }.Where(kv => !string.IsNullOrEmpty(kv.Value)));

    /// <summary>Export of exactly what the filters show (search text excluded: exports are by period, workstation and event).</summary>
    public string ExportLink(string format) => "/export/audit" + Microsoft.AspNetCore.Http.QueryString.Create(new Dictionary<string, string?>
    {
        ["format"] = format, ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"), ["terminal"] = Terminal?.ToString(), ["action"] = Action,
    }.Where(kv => !string.IsNullOrEmpty(kv.Value)));
}
