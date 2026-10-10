using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Flip;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.FlipMessages;

public sealed class IndexModel(FlipCapture capture, PropertyClock clock, IConfiguration config) : PageModel
{
    /// <summary>JSON shown indented; anything else as it is.</summary>
    public string Pretty(string text)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(text)?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? text; }
        catch (System.Text.Json.JsonException) { return text; }
    }

    public IReadOnlyList<FlipMessageRow> Messages { get; private set; } = [];
    public string Mode { get; private set; } = "";
    public FlipStub Stub { get; private set; } = new(200, "text/plain", "");
    public PropertyClock Clock => clock;
    public int TcpPort => config.GetValue("Flip:TcpPort", 0);

    /// <summary>
    /// Addresses FLIP should use: this server's IPv4 addresses with the FLIP port (Kestrel:Endpoints:Flip, default the
    /// current port). IP, not the computer name: a name can resolve to IPv6, which FLIP's firewall rule does not allow.
    /// </summary>
    public IReadOnlyList<string> FlipUrls(int currentPort)
    {
        var port = Uri.TryCreate((config["Kestrel:Endpoints:Flip:Url"] ?? "").Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost"),
            UriKind.Absolute, out var u) ? u.Port : currentPort;
        var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)
                        && !a.ToString().StartsWith("169.254."))
            .Select(a => a.ToString()).Distinct().ToList();
        if (ips.Count == 0) ips.Add("<this server's IP>");
        return ips.Select(ip => $"http://{ip}:{port}/flip").ToList();
    }
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    public async Task OnGetAsync()
    {
        Messages = await capture.RecentAsync(100);
        Mode = await capture.ModeAsync();
        Stub = await capture.StubAsync();
        // A message linked directly (?Id=) opens expanded; older than the list → still shown on its own.
        if (Id is { } id && Messages.All(m => m.Id != id) && await capture.GetAsync(id) is { } single) Messages = [single, .. Messages];
    }
}
