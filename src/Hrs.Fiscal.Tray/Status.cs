using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Hrs.Fiscal.Tray;

/// <summary>What the tray shows. Worst first.</summary>
internal enum TrayState { ServiceDown, ServerUnreachable, AtkOffline, NotReady, Checking, Pending, Online }

/// <summary>The local Fiscal Client's /status, reduced to what the tray needs.</summary>
internal sealed class ClientStatus
{
    public bool Reached { get; set; }
    public bool Paired { get; set; }
    public bool Registered { get; set; }
    public long PosId { get; set; }
    public string? Notice { get; set; }
    public string? AtkState { get; set; }
    public int Pending { get; set; }
    public DateTime? OfflineSinceUtc { get; set; }
    public DateTime? DeadlineUtc { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public string? Environment { get; set; }
    public string? Language { get; set; }
    public string? LastError { get; set; }

    public TrayState State =>
        !Reached ? TrayState.ServiceDown
        : Notice == "server-unreachable" ? TrayState.ServerUnreachable
        : Notice == "atk-offline" ? TrayState.AtkOffline
        : !Paired || !Registered ? TrayState.NotReady
        : AtkState == "pending" ? TrayState.Pending
        : AtkState == "online" ? TrayState.Online
        : TrayState.Checking;

    /// <summary>States that open the window: fiscalization at this workstation is affected.</summary>
    public static bool NeedsNotice(TrayState s) => s is TrayState.ServiceDown or TrayState.ServerUnreachable or TrayState.AtkOffline;
}

internal sealed class StatusReader(int port)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    public int Port { get; } = port;

    public async Task<ClientStatus> ReadAsync()
    {
        try
        {
            var json = await Http.GetStringAsync($"http://localhost:{Port}/status").ConfigureAwait(true);
            return Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException)
        {
            return new ClientStatus { Reached = false };
        }
    }

    internal static ClientStatus Parse(string json)
    {
        var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var atk = Get(root, "atk") as Dictionary<string, object?>;
        return new ClientStatus
        {
            Reached = true,
            Paired = Get(root, "paired") is true,
            Registered = Get(root, "registered") is true,
            PosId = Convert.ToInt64(Get(root, "posId") ?? 0L, CultureInfo.InvariantCulture),
            Notice = Get(root, "notice") as string,
            LastHeartbeatUtc = Date(Get(root, "lastHeartbeatUtc")),
            Environment = Get(root, "environment") as string,
            LastError = Get(root, "lastError") as string,
            AtkState = atk is null ? null : Get(atk, "state") as string,
            Pending = atk is null ? 0 : Convert.ToInt32(Get(atk, "pending") ?? 0, CultureInfo.InvariantCulture),
            OfflineSinceUtc = atk is null ? null : Date(Get(atk, "offlineSinceUtc")),
            DeadlineUtc = atk is null ? null : Date(Get(atk, "deadlineUtc")),
            Language = atk is null ? null : Get(atk, "language") as string,
        };
    }

    private static object? Get(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    private static DateTime? Date(object? v) =>
        v is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
}
