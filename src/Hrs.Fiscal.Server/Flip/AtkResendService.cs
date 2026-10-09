using Dapper;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Signing;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Npgsql;

namespace Hrs.Fiscal.Server.Flip;

/// <summary>
/// Offline queue: resends receipts that ATK has not accepted yet (no answer, timeout, 5xx), exactly as they were signed.
/// Runs every <c>atk_retry_minutes</c> while FLIP is in live mode. Receipts ATK rejected are not resent.
/// </summary>
public sealed class AtkResendService(IServiceScopeFactory scopes, ILogger<AtkResendService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ContinueWith(_ => { });
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = 2;
            try
            {
                using var scope = scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
                minutes = Math.Clamp(await settings.GetAsync("atk_retry_minutes", 2, stoppingToken), 1, 60);
                if (await settings.GetAsync("flip_mode", "capture", stoppingToken) == "live")
                    await ResendPendingAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Offline queue run failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    public static async Task<int> ResendPendingAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<NpgsqlDataSource>();
        var settings = services.GetRequiredService<SettingsStore>();
        var archive = services.GetRequiredService<ReceiptArchive>();
        var audit = services.GetRequiredService<AuditLog>();
        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        var timeout = TimeSpan.FromSeconds(await settings.GetAsync("atk_timeout_seconds", 10, ct));
        var atk = services.GetRequiredService<IAtkClientFactory>().Create(environment, timeout);

        List<(long Id, byte[] PosCoupon, string Signature, long TerminalId)> pending;
        await using (var conn = await db.OpenConnectionAsync(ct))
            pending = (await conn.QueryAsync<(long, byte[], string, long)>("""
                SELECT r.id, r.pos_coupon, r.signature, r.terminal_id
                FROM fiscal.offline_queue q JOIN fiscal.receipt r ON r.id = q.id
                JOIN fiscal.source_payload s ON s.id = r.source_payload_id AND s.source = 'OFIS'
                ORDER BY r.id LIMIT 200
                """)).ToList();

        var sent = 0;
        foreach (var p in pending)
        {
            var result = await atk.SendPosCouponAsync(new SignedPayload(Convert.ToBase64String(p.PosCoupon), p.Signature), ct);
            await archive.AppendTransmissionAsync(p.Id, "server (queue)", result, ct: ct);
            if (result.Outcome == AtkOutcome.Transient) break; // still offline: try the rest next run
            await audit.WriteAsync("queue", result.Outcome == AtkOutcome.Accepted ? AuditLog.Actions.ReceiptAccepted : AuditLog.Actions.ReceiptRejected,
                "receipt", p.Id.ToString(), new { result.HttpStatus, result.Message, transaction = result.TransactionId?.ToString(), resent = true }, p.TerminalId, ct);
            sent++;
        }
        return sent;
    }
}
