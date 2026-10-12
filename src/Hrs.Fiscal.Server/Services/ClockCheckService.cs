using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;

namespace Hrs.Fiscal.Server.Services;

/// <summary>Latest comparison of the server clock with ATK's (Doc. 01-06-1967 Art 26.5: time synchronised with ATK).</summary>
public sealed class ClockStatus
{
    public DateTime? CheckedAtUtc { get; private set; }
    /// <summary>ATK time minus server time, seconds (positive = server is behind).</summary>
    public double? DriftSeconds { get; private set; }
    public string? Error { get; private set; }
    public int MaxDriftSeconds { get; private set; } = 30;
    public bool OutOfSync => DriftSeconds is { } d && Math.Abs(d) > MaxDriftSeconds;

    public void Record(DateTime atUtc, double? drift, string? error, int max)
    {
        CheckedAtUtc = atUtc; DriftSeconds = drift; Error = error; MaxDriftSeconds = max;
    }
}

/// <summary>
/// Every 15 minutes reads the time from ATK's server (HTTP Date header of the selected environment, corrected for half
/// the round trip) and compares it with the server clock. A drift above <c>clock_max_drift_seconds</c> is shown on the
/// dashboard and written to the audit log once (CLOCK_DRIFT), and again when the clock is back in sync (CLOCK_OK).
/// </summary>
public sealed class ClockCheckService(IServiceScopeFactory scopes, ClockStatus status, ILogger<ClockCheckService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private bool? _lastOutOfSync;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { });
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Clock check failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        var max = Math.Clamp(await settings.GetAsync("clock_max_drift_seconds", 30, ct), 1, 3600);
        var (drift, error) = await MeasureAsync(AtkEndpoints.BaseUri(environment), ct);
        status.Record(DateTime.UtcNow, drift, error, max);
        if (drift is null) return;

        var outOfSync = status.OutOfSync;
        if (_lastOutOfSync != outOfSync && (outOfSync || _lastOutOfSync == true))
            await audit.WriteAsync("system", outOfSync ? AuditLog.Actions.ClockDrift : AuditLog.Actions.ClockOk, "clock", environment.ToString(),
                new { drift_seconds = Math.Round(drift.Value, 1), max_seconds = max }, ct: ct);
        _lastOutOfSync = outOfSync;
    }

    /// <summary>ATK's clock minus ours, from the Date header (second resolution, so ±1 s is normal).</summary>
    public static async Task<(double? Drift, string? Error)> MeasureAsync(Uri baseUri, CancellationToken ct)
    {
        try
        {
            var sent = DateTimeOffset.UtcNow;
            using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUri), ct);
            var received = DateTimeOffset.UtcNow;
            if (response.Headers.Date is not { } atk) return (null, "ATK's server did not send its time.");
            var local = sent + (received - sent) / 2;
            return ((atk - local).TotalSeconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, ex is TaskCanceledException ? "ATK did not answer in time." : ex.Message);
        }
    }
}
