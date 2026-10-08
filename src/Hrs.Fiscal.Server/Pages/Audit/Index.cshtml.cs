using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Audit;

public sealed class IndexModel(AuditLog audit, PropertyClock clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<AuditRow> Result { get; private set; } = new([], 0, 1, 100);
    public IReadOnlyList<string> ActionsInUse { get; private set; } = [];
    public IntegrityResult? Integrity { get; private set; }
    public PropertyClock Clock => clock;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostCheckAsync()
    {
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
    }

    private async Task LoadAsync()
    {
        ActionsInUse = await audit.ActionsInUseAsync();
        Result = await audit.SearchAsync(new AuditFilter { Query = Q, Action = Action, From = From, To = To, Page = P });
    }

    public string PageLink(int page) => "/Audit" + Microsoft.AspNetCore.Http.QueryString.Create(new Dictionary<string, string?>
    {
        ["Q"] = Q, ["Action"] = Action, ["From"] = From?.ToString("yyyy-MM-dd"), ["To"] = To?.ToString("yyyy-MM-dd"), ["P"] = page.ToString(),
    }.Where(kv => !string.IsNullOrEmpty(kv.Value)));
}
