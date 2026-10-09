using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Flip;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.FlipMessages;

public sealed class IndexModel(FlipCapture capture, PropertyClock clock, IConfiguration config) : PageModel
{
    public IReadOnlyList<FlipMessageRow> Messages { get; private set; } = [];
    public FlipMessageRow? Selected { get; private set; }
    public string Mode { get; private set; } = "";
    public FlipStub Stub { get; private set; } = new(200, "text/plain", "");
    public PropertyClock Clock => clock;
    public int TcpPort => config.GetValue("Flip:TcpPort", 0);
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    public async Task OnGetAsync()
    {
        Messages = await capture.RecentAsync();
        Mode = await capture.ModeAsync();
        Stub = await capture.StubAsync();
        if (Id is { } id) Selected = await capture.GetAsync(id);
        else if (Messages.Count > 0) Selected = Messages[0];
    }
}
