using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Receipts;

public sealed class IndexModel(ReceiptQueries receipts, PropertyClock clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public long? Terminal { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public short? Type { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<ReceiptListRow> Result { get; private set; } = new([], 0, 1, 50);
    public IReadOnlyList<TerminalRow> Terminals { get; private set; } = [];
    public PropertyClock Clock => clock;

    public async Task OnGetAsync()
    {
        // Default: last 7 days, unless searching for a specific receipt.
        if (From is null && To is null && string.IsNullOrWhiteSpace(Q) && Status is null)
        {
            To = clock.Today;
            From = clock.Today.AddDays(-6);
        }
        Terminals = await receipts.TerminalsAsync();
        Result = await receipts.SearchAsync(new ReceiptFilter
        {
            Query = Q, From = From, To = To, TerminalId = Terminal, Status = Status, Type = Type, Page = P,
        });
    }

    public string Link(string? status = null, int? page = null, bool keepStatus = false)
    {
        var q = new Dictionary<string, string?>
        {
            ["Q"] = Q, ["From"] = From?.ToString("yyyy-MM-dd"), ["To"] = To?.ToString("yyyy-MM-dd"),
            ["Terminal"] = Terminal?.ToString(), ["Type"] = Type?.ToString(),
            ["Status"] = keepStatus ? Status : status, ["P"] = page?.ToString(),
        };
        return "/Receipts" + Microsoft.AspNetCore.Http.QueryString.Create(q.Where(kv => !string.IsNullOrEmpty(kv.Value)));
    }

    /// <summary>Export of the receipts the filters show (period, workstation, status).</summary>
    public string ExportLink(string format) => "/export/receipts" + Microsoft.AspNetCore.Http.QueryString.Create(new Dictionary<string, string?>
    {
        ["format"] = format, ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"), ["terminal"] = Terminal?.ToString(), ["status"] = Status,
    }.Where(kv => !string.IsNullOrEmpty(kv.Value)));
}
