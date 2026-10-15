using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hrs.Fiscal.Core.Client;
using Hrs.Fiscal.Core.Printing;
using Hrs.Fiscal.Core.Signing;

namespace Hrs.Fiscal.Client;

/// <summary>Calls the fiscal server: pairing, ATK registration, heartbeat.</summary>
public sealed class ServerApi(ClientStore store, ClientOptions options, HttpMessageHandler? handler = null)
{
    private readonly HttpClient _http = handler is null ? new() { Timeout = TimeSpan.FromSeconds(30) } : new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PairResponse> PairAsync(string serverUrl, string code, CancellationToken ct)
    {
        var request = new PairRequest(code, Environment.MachineName, ClientStore.Version, options.Port, NullIfEmpty(options.AdvertiseUrl));
        using var response = await _http.PostAsJsonAsync(Url(serverUrl, "/client/pair"), request, Json, ct);
        return await ReadAsync<PairResponse>(response, ct);
    }

    public Task<EnrollStartResponse> EnrollStartAsync(CancellationToken ct) => SendAsync<EnrollStartResponse>("/client/enroll/start", new { }, ct);
    public Task<EnrollFinishResponse> EnrollFinishAsync(string csrPem, CancellationToken ct) => SendAsync<EnrollFinishResponse>("/client/enroll/finish", new EnrollFinishRequest(csrPem), ct);
    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) => SendAsync<HeartbeatResponse>("/client/heartbeat", request, ct);

    private async Task<T> SendAsync<T>(string path, object payload, CancellationToken ct)
    {
        var state = store.State;
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(state.ServerUrl!, path))
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
        };
        ClientAuth.Apply(request, state.TerminalId, store.Secret!, body, DateTimeOffset.UtcNow);
        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidOperationException("Empty answer from the fiscal server.");
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ClientError>(Json, ct))?.Message; } catch (JsonException) { }
        throw new ServerRefusedException((int)response.StatusCode, message ?? $"The fiscal server answered {(int)response.StatusCode}.");
    }

    private static Uri Url(string server, string path) => new(new Uri(server.TrimEnd('/') + "/"), path.TrimStart('/'));
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public sealed class ServerRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Runs in the background: pairs with the server (pairing.json from the installation package), creates the key and
/// registers it with ATK through the server, then reports to the server every 15 seconds and learns ATK's status.
/// </summary>
public sealed class ClientWorker(ClientStore store, ServerApi server, ClientOptions options, IPrinterTransport printers, ILogger<ClientWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(stoppingToken);
                store.Update(s => s.LastError = null);
                delay = TimeSpan.FromSeconds(Math.Max(5, options.HeartbeatSeconds));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("{Message}", ex.Message);
                store.Update(s => s.LastError = ex.Message);
                delay = TimeSpan.FromSeconds(Math.Min(300, Math.Max(10, delay.TotalSeconds * 2)));
                if (ex is ServerRefusedException { Status: 401 })
                {
                    // The server no longer knows this client (workstation deleted or paired again): start over.
                    store.Update(s => { s.SecretProtected = null; s.TerminalId = 0; });
                }
            }
            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One round: pair if needed, register if needed, heartbeat.</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        if (!store.State.Paired) await PairAsync(ct);
        if (!store.State.Paired) return;
        if (!store.State.Registered || store.State.CertificateExpiresUtc < DateTime.UtcNow.AddDays(14)) await RegisterAsync(ct);
        var cert = store.State.CertificatePem is { } pem ? X509Certificate2.CreateFromPem(pem).Thumbprint : null;
        var answer = await server.HeartbeatAsync(new HeartbeatRequest(ClientStore.Version, Environment.MachineName, options.Port,
            string.IsNullOrWhiteSpace(options.AdvertiseUrl) ? null : options.AdvertiseUrl, cert, printers.InstalledPrinters(), printers.SerialPorts()), ct);
        store.Atk = answer.Atk;
        store.Update(s => s.LastHeartbeatUtc = DateTime.UtcNow);
        if (!answer.Registered && store.State.Registered)
        {
            log.LogWarning("The fiscal server asks to register again: {Message}", answer.Message);
            store.Update(s => { s.CertificatePem = null; s.CertificateExpiresUtc = null; });
        }
    }

    private async Task PairAsync(CancellationToken ct)
    {
        string? serverUrl = options.ServerUrl, code = options.PairingCode;
        if (File.Exists(store.PairingPath))
        {
            var file = JsonSerializer.Deserialize<PairingFile>(await File.ReadAllTextAsync(store.PairingPath, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (file is not null) { serverUrl = file.ServerUrl; code = file.Code; }
        }
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Not paired: install the package downloaded for this workstation (it contains pairing.json), or set Client:ServerUrl and Client:PairingCode.");
        var paired = await server.PairAsync(serverUrl, code, ct);
        store.Update(s =>
        {
            s.ServerUrl = serverUrl; s.TerminalId = paired.TerminalId; s.PosId = paired.PosId; s.Nui = paired.Nui; s.BranchId = paired.BranchId;
            s.BusinessName = paired.BusinessName; s.Environment = paired.Environment; s.SecretProtected = store.Protect(paired.Secret);
            s.CertificatePem = null; s.CertificateExpiresUtc = null; s.KeyReference = null;
        });
        File.Delete(store.PairingPath);   // the code is used up
        log.LogInformation("Paired with {Server} as POS {PosId}", serverUrl, paired.PosId);
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        var start = await server.EnrollStartAsync(ct);
        var (key, reference) = store.CreateKey(start.BranchId, start.PosId);
        using (key as IDisposable)
        {
            var csr = CsrFactory.CreatePem(key.PublicKey, (ulong)start.Nui, (ulong)start.PosId, (ulong)start.BranchId, start.BusinessName);
            var finish = await server.EnrollFinishAsync(csr, ct);
            using var certificate = X509Certificate2.CreateFromPem(finish.CertificatePem);
            using var certKey = certificate.GetECDsaPublicKey() ?? throw new CryptographicException("The certificate has no ECDSA key.");
            var probe = "fiscal-client-registration"u8.ToArray();
            if (!certKey.VerifyData(probe, key.SignData(probe), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CryptographicException("The certificate does not match this computer's key.");
            store.Update(s => { s.KeyReference = reference; s.CertificatePem = finish.CertificatePem; s.CertificateExpiresUtc = finish.ExpiresUtc; });
        }
        log.LogInformation("Registered with ATK as POS {PosId}; certificate valid until {Expires:d}", start.PosId, store.State.CertificateExpiresUtc);
    }
}
