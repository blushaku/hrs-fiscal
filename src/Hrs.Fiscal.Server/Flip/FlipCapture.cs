using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Hrs.Fiscal.Server.Data;
using Npgsql;

namespace Hrs.Fiscal.Server.Flip;

public sealed class FlipMessageRow
{
    public long Id { get; init; }
    public DateTime ReceivedAt { get; init; }
    public string Transport { get; init; } = "";
    public string? RemoteAddress { get; init; }
    public string? Method { get; init; }
    public string? Path { get; init; }
    public string Headers { get; init; } = "{}";
    public string? ContentType { get; init; }
    public byte[] Body { get; init; } = [];
    public string Mode { get; init; } = "";
    public int? ResponseStatus { get; init; }
    public string? ResponseBody { get; init; }
    public int Size => Body.Length;

    /// <summary>Body as text, pretty-printed when it is XML or JSON.</summary>
    public string Pretty()
    {
        var text = Encoding.UTF8.GetString(Body);
        var t = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        try
        {
            if (t.StartsWith('<')) return XDocument.Parse(t).ToString();
            if (t.StartsWith('{') || t.StartsWith('['))
                return JsonSerializer.Serialize(JsonDocument.Parse(t).RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or JsonException) { }
        return text;
    }

    public string Kind
    {
        get
        {
            var t = Encoding.UTF8.GetString(Body, 0, Math.Min(Body.Length, 64)).TrimStart('﻿', ' ', '\r', '\n', '\t');
            return t.StartsWith('<') ? "XML" : t.StartsWith('{') || t.StartsWith('[') ? "JSON" : Body.Length == 0 ? "empty" : "text/binary";
        }
    }
}

public sealed record FlipStub(int Status, string ContentType, string Body);

/// <summary>Stores FLIP traffic and decides the answer. Capture mode answers with the configured stub.</summary>
public sealed class FlipCapture(NpgsqlDataSource db, SettingsStore settings, AuditLog audit, ILogger<FlipCapture> log)
{
    public const int MaxBodyBytes = 5 * 1024 * 1024;

    public async Task<FlipStub> StubAsync(CancellationToken ct = default) => new(
        await settings.GetAsync("flip_stub_status", 200, ct),
        await settings.GetAsync("flip_stub_content_type", "text/plain", ct) ?? "text/plain",
        await settings.GetAsync("flip_stub_body", "", ct) ?? "");

    public Task<string> ModeAsync(CancellationToken ct = default) => settings.GetAsync("flip_mode", "capture", ct)!;

    public async Task<long> StoreAsync(string transport, string? remote, string? method, string? path, IDictionary<string, string> headers,
        string? contentType, byte[] body, string mode, int? responseStatus, string? responseBody, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO fiscal.flip_message (transport, remote_address, method, path, headers, content_type, body, mode, response_status, response_body)
            VALUES (@transport, @remote, @method, @path, @headers::jsonb, @contentType, @body, @mode, @responseStatus, @responseBody)
            RETURNING id
            """, new { transport, remote, method, path, headers = JsonSerializer.Serialize(headers), contentType, body, mode, responseStatus, responseBody });
        log.LogInformation("FLIP {Transport} message {Id} captured from {Remote}: {Bytes} bytes", transport, id, remote, body.Length);
        await audit.WriteAsync("flip", "FLIP_MESSAGE", "flip_message", id.ToString(), new { transport, remote, method, path, bytes = body.Length, mode }, ct: ct);
        return id;
    }

    public async Task<IReadOnlyList<FlipMessageRow>> RecentAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<FlipMessageRow>(
            "SELECT id, received_at, transport, remote_address, method, path, headers::text AS headers, content_type, body, mode, response_status, response_body FROM fiscal.flip_message ORDER BY id DESC LIMIT @limit",
            new { limit })).ToList();
    }

    public async Task<FlipMessageRow?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FlipMessageRow>(
            "SELECT id, received_at, transport, remote_address, method, path, headers::text AS headers, content_type, body, mode, response_status, response_body FROM fiscal.flip_message WHERE id = @id",
            new { id });
    }

    /// <summary>HTTP entry point for every method and path under /flip.</summary>
    public static async Task<IResult> HandleHttpAsync(HttpContext http, FlipCapture capture, FlipAuth auth)
    {
        var ct = http.RequestAborted;
        var authState = await auth.StateAsync(ct);
        var authResult = await auth.CheckAsync(http.Request.Headers, ct);
        var remote = http.Connection.RemoteIpAddress?.ToString();
        var path = http.Request.Path + http.Request.QueryString;

        // Secrets are never stored: the token header (and cookies) are replaced by the verification result.
        var headers = http.Request.Headers
            .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                        && !h.Key.Equals(authState.Header, StringComparison.OrdinalIgnoreCase)
                        && !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString());
        foreach (var name in new[] { "Authorization", authState.Header }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (http.Request.Headers.ContainsKey(name)) headers[name] = "[present, not stored]";
        headers["X-HRS-Token-Check"] = authResult switch
        {
            FlipAuthResult.Valid => "valid",
            FlipAuthResult.Missing => "missing",
            FlipAuthResult.Invalid => "invalid",
            _ => "not required",
        };

        if (authResult is FlipAuthResult.Missing or FlipAuthResult.Invalid)
        {
            // Rejected requests are logged without their body, so nothing unauthenticated enters the archive.
            await capture.StoreAsync("http", remote, http.Request.Method, path, headers, http.Request.ContentType, [], "rejected",
                StatusCodes.Status401Unauthorized, "", ct);
            await capture.AuditAuthFailureAsync(remote, path, authResult, ct);
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            ms.Write(buffer, 0, read);
        }

        var mode = await capture.ModeAsync(ct);
        FlipStub answer = mode == "capture"
            ? await capture.StubAsync(ct)
            : new FlipStub(StatusCodes.Status501NotImplemented, "text/plain", "HRS Fiscal live FLIP processing is not available yet.");

        await capture.StoreAsync("http", remote, http.Request.Method, path, headers, http.Request.ContentType, ms.ToArray(), mode, answer.Status, answer.Body, ct);
        return Results.Text(answer.Body, answer.ContentType, Encoding.UTF8, answer.Status);
    }

    public Task AuditAuthFailureAsync(string? remote, string? path, FlipAuthResult result, CancellationToken ct = default) =>
        audit.WriteAsync("flip", AuditLog.Actions.FlipAuthFailed, "flip_message", null,
            new { remote, path, reason = result == FlipAuthResult.Missing ? "no token" : "wrong token" }, ct: ct);
}

/// <summary>
/// Optional raw TCP listener (Flip:TcpPort), for the case that FLIP talks plain sockets rather than HTTP.
/// Reads one message per connection (until the peer pauses 2 s or closes), stores it and answers with the stub body.
/// </summary>
public sealed class FlipTcpListener(IServiceScopeFactory scopes, IConfiguration config, ILogger<FlipTcpListener> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = config.GetValue("Flip:TcpPort", 0);
        if (port <= 0) return;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        log.LogInformation("FLIP raw TCP capture listening on port {Port}", port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var ms = new MemoryStream();
            var buffer = new byte[65536];
            while (ms.Length < FlipCapture.MaxBodyBytes)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(2));
                int read;
                try { read = await stream.ReadAsync(buffer, idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (read == 0) break;
                ms.Write(buffer, 0, read);
            }

            using var scope = scopes.CreateScope();
            var capture = scope.ServiceProvider.GetRequiredService<FlipCapture>();
            if ((await scope.ServiceProvider.GetRequiredService<FlipAuth>().StateAsync(ct)).Required)
            {
                // Raw TCP has no place for a token: refused while the token is required.
                var remote = client.Client.RemoteEndPoint?.ToString();
                await capture.StoreAsync("tcp", remote, null, null, new Dictionary<string, string> { ["X-HRS-Token-Check"] = "not possible over TCP" },
                    null, [], "rejected", null, "", ct);
                await capture.AuditAuthFailureAsync(remote, null, FlipAuthResult.Missing, ct);
                return;
            }
            var stub = await capture.StubAsync(ct);
            await capture.StoreAsync("tcp", client.Client.RemoteEndPoint?.ToString(), null, null, new Dictionary<string, string>(),
                null, ms.ToArray(), await capture.ModeAsync(ct), null, stub.Body, ct);
            if (stub.Body.Length > 0 && client.Connected)
                await stream.WriteAsync(Encoding.UTF8.GetBytes(stub.Body), ct);
        }
    }
}
