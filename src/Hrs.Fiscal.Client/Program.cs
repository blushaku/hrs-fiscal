// Opera Cloud Fiscal Solution - Kosovo — Fiscal Client.
// Installed on a front-desk workstation that is set to "Workstation client" signing on the fiscal server. It keeps this
// workstation's ATK key (created here, non-exportable, in the TPM when there is one), registers it with ATK through the
// fiscal server, signs the receipts the server sends it, and prints on a receipt printer connected to this PC.

using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Client;
using Hrs.Fiscal.Core.Printing;
using Microsoft.AspNetCore.DataProtection;

namespace Hrs.Fiscal.Client;

public static class ClientProgram
{
    public static async Task Main(string[] args) => await ClientHost.Build(args).RunAsync();
}

public static class ClientHost
{
    public const string ServiceName = "OperaCloudFiscalKosovoClient";

    /// <param name="serverHandler">Tests: how the client reaches the fiscal server.</param>
    /// <param name="printers">Tests: replaces the printer transport.</param>
    /// <param name="background">Tests: false = no background loop; the test calls <see cref="ClientWorker.StepAsync"/>.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null,
        HttpMessageHandler? serverHandler = null, IPrinterTransport? printers = null, bool background = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        configure?.Invoke(builder);
        var options = builder.Configuration.GetSection("Client").Get<ClientOptions>() ?? new ClientOptions();
        builder.WebHost.ConfigureKestrel(k => { if (builder.Configuration["urls"] is null && builder.Configuration["ASPNETCORE_URLS"] is null) k.ListenAnyIP(options.Port); });

        var dp = builder.Services.AddDataProtection().SetApplicationName("Opera Cloud Fiscal Client")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.Folder(), "protection")));
        if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ClientStore>();
        builder.Services.AddSingleton(printers ?? new PrinterTransport());
        builder.Services.AddSingleton(sp => new ServerApi(sp.GetRequiredService<ClientStore>(), options, serverHandler));
        builder.Services.AddSingleton<ClientWorker>();
        if (background) builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientWorker>());

        var app = builder.Build();
        var store = app.Services.GetRequiredService<ClientStore>();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        app.MapGet("/health", () => Results.Ok(new { status = "ok", component = "fiscal-client", version = ClientStore.Version, paired = store.State.Paired, registered = store.State.Registered }));

        // Signs the receipt the fiscal server built for this workstation, after checking it is really for this POS.
        app.MapPost("/sign", async (HttpContext http, ILogger<ClientWorker> log) =>
        {
            var body = await ReadBodyAsync(http);
            if (!Authorised(http, body, store)) return Results.Json(new ClientError("Not authorised."), statusCode: 401);
            var state = store.State;
            if (!state.Registered) return Results.Json(new ClientError("This workstation is not registered with ATK yet."), statusCode: 409);
            var request = JsonSerializer.Deserialize<SignRequest>(body, json);
            if (request is null || request.Items.Count is 0 or > 4) return Results.BadRequest(new ClientError("Nothing to sign."));
            var lines = new List<string>();
            foreach (var item in request.Items)
            {
                var check = Check(item, state, out var summary);
                if (check is not null) { log.LogWarning("Refused to sign: {Reason}", check); return Results.BadRequest(new ClientError(check)); }
                lines.Add(summary);
            }
            var signingKey = store.OpenKey(state.KeyReference!);
            List<string> signatures;
            try { signatures = request.Items.Select(i => Convert.ToBase64String(signingKey.SignData(Encoding.UTF8.GetBytes(i.Details)))).ToList(); }
            finally { (signingKey as IDisposable)?.Dispose(); }
            // Local trace of everything this workstation signed.
            await File.AppendAllLinesAsync(Path.Combine(store.Folder, "signed.log"), lines.Select(l => $"{DateTime.UtcNow:O} {l}"));
            store.Update(s => { s.LastSignedUtc = DateTime.UtcNow; s.SignedCount++; });
            return Results.Ok(new SignResponse(signatures));
        });

        // Prints a finished ESC/POS job on the receipt printer connected to this PC (USB or COM port).
        app.MapPost("/print", async (HttpContext http, IPrinterTransport transport) =>
        {
            var body = await ReadBodyAsync(http);
            if (!Authorised(http, body, store)) return Results.Json(new ClientError("Not authorised."), statusCode: 401);
            var request = JsonSerializer.Deserialize<PrintRequest>(body, json);
            if (request is null || request.Kind is not (PrinterKinds.Usb or PrinterKinds.Serial)) return Results.BadRequest(new ClientError("Unknown printer."));
            try { await transport.SendAsync(new PrinterTarget(request.Kind, request.Address, request.Baud, request.Flow), Convert.FromBase64String(request.JobBase64)); }
            catch (PrinterException ex) { return Results.Json(new ClientError(ex.Message), statusCode: 502); }
            return Results.Ok(new ClientError("printed"));
        });

        // Status page, only on this computer: http://localhost:5110
        app.MapGet("/", (HttpContext http) =>
        {
            if (http.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)) return Results.NotFound();
            return Results.Content(StatusPage(store), "text/html; charset=utf-8");
        });
        return app;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static bool Authorised(HttpContext http, byte[] body, ClientStore store)
    {
        var state = store.State;
        if (!state.Paired || http.Request.Headers[ClientAuth.TerminalHeader] != state.TerminalId.ToString()) return false;
        return ClientAuth.Verify(store.Secret!, http.Request.Method, http.Request.Path, http.Request.Headers[ClientAuth.TimeHeader],
            http.Request.Headers[ClientAuth.SignatureHeader], body, DateTimeOffset.UtcNow);
    }

    /// <summary>Only receipts of this business, unit and POS are signed. Null = fine.</summary>
    public static string? Check(SignItem item, ClientState state, out string summary)
    {
        summary = "";
        byte[] bytes;
        try { bytes = Convert.FromBase64String(item.Details); } catch (FormatException) { return "The data is not Base64."; }
        ulong business, branch, pos;
        try
        {
            switch (item.Kind)
            {
                case "pos":
                    var c = PosCoupon.Parser.ParseFrom(bytes);
                    (business, branch, pos) = (c.BusinessId, c.BranchId, c.PosId);
                    summary = $"coupon {c.CouponId} NUIKF {c.VerificationNo} type {c.Type} total {c.Total}";
                    break;
                case "citizen":
                    var q = CitizenCoupon.Parser.ParseFrom(bytes);
                    (business, branch, pos) = (q.BusinessId, q.BranchId, q.PosId);
                    summary = $"qr {q.CouponId} NUIKF {q.VerificationNo}";
                    break;
                default: return $"Unknown item kind '{item.Kind}'.";
            }
        }
        catch (InvalidProtocolBufferException) { return "The data is not an ATK coupon."; }
        if (business != (ulong)state.Nui || branch != (ulong)state.BranchId || pos != (ulong)state.PosId)
            return $"The coupon is for NUI {business}, unit {branch}, POS {pos}; this workstation is NUI {state.Nui}, unit {state.BranchId}, POS {state.PosId}.";
        return null;
    }

    private static string StatusPage(ClientStore store)
    {
        var s = store.State;
        string E(object? v) => WebUtility.HtmlEncode(v?.ToString() ?? "");
        var status = !s.Paired ? "Not paired" : !s.Registered ? "Paired, not registered with ATK yet" : "Ready";
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Fiscal Client</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#12142B}dt{color:#4F5D69}dd{margin:0 0 8px}</style></head>
            <body><h1>Opera Cloud Fiscal Solution - Kosovo — Fiscal Client</h1><dl>
            <dt>Status</dt><dd><strong>{{E(status)}}</strong></dd>
            <dt>Fiscal server</dt><dd>{{E(s.ServerUrl)}}</dd>
            <dt>Workstation</dt><dd>POS {{E(s.PosId)}} · NUI {{E(s.Nui)}} · unit {{E(s.BranchId)}} · {{E(s.BusinessName)}} · ATK {{E(s.Environment)}}</dd>
            <dt>Certificate valid until</dt><dd>{{E(s.CertificateExpiresUtc?.ToString("dd.MM.yyyy"))}}</dd>
            <dt>Key</dt><dd>{{E(store.KeyKind == "cng" ? "Windows key store, non-exportable (TPM when available)" : "file (test only)")}}</dd>
            <dt>Last contact with the server</dt><dd>{{E(s.LastHeartbeatUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))}}</dd>
            <dt>Receipts signed</dt><dd>{{E(s.SignedCount)}} (last {{E(s.LastSignedUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))}})</dd>
            <dt>Last problem</dt><dd>{{E(s.LastError ?? "none")}}</dd>
            <dt>Version</dt><dd>{{E(ClientStore.Version)}}</dd></dl></body></html>
            """;
    }
}
