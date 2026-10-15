using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Dapper;
using Google.Protobuf;
using Hrs.Fiscal.Core.Receipts;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Client;
using Hrs.Fiscal.Core.Signing;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Hrs.Fiscal.Server.Signing;

/// <summary>
/// Server side of workstation-client signing. A workstation set to <i>Workstation client</i> gets an installation package
/// with a one-time pairing code. The Fiscal Client on that PC pairs with it (and receives a shared secret), creates its
/// own key, which never leaves the PC, and registers with ATK through the server: the server asks ATK to verify the
/// business and POS, the client makes the certificate request with its key, the server has ATK sign it.
/// </summary>
public sealed class ClientRegistry(
    NpgsqlDataSource db, SettingsStore settings, AuditLog audit, IAtkClientFactory atkFactory, IDataProtectionProvider protection,
    PropertyClock clock, Hrs.Fiscal.Server.Services.AtkHealthQuery atkHealth, ILogger<ClientRegistry> log)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Hrs.Fiscal.Server.FiscalClient.v1");
    /// <summary>ATK's verification between the two enrolment steps (start → finish), per workstation, for 10 minutes.</summary>
    private static readonly ConcurrentDictionary<long, (VerifyResponse Verify, DateTime Until)> PendingVerify = new();
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromDays(2);

    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);

    /// <summary>A new one-time pairing code for the workstation's installation package; the previous code stops working.</summary>
    public async Task<(string Code, DateTime Expires)> CreatePairingAsync(long terminalId, string actor, CancellationToken ct = default)
    {
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId) ?? throw new InvalidOperationException("Unknown workstation.");
        if (terminal.EffectiveMode(await settings.SigningModeDefaultAsync(ct)) != SigningModes.Client)
            throw new InvalidOperationException($"Workstation POS {terminal.PosId} signs on the server; set Signing to Workstation client first.");
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var raw = RandomNumberGenerator.GetString(alphabet, 12);
        var code = $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
        var expires = clock.Time.GetUtcNow().UtcDateTime + PairingLifetime;
        await using (var conn = await db.OpenConnectionAsync(ct))
            await conn.ExecuteAsync("UPDATE fiscal.terminal SET client_pairing = @p, client_pairing_expires = @expires WHERE id = @terminalId",
                new { p = Protect(code), expires, terminalId });
        await audit.WriteAsync(actor, AuditLog.Actions.ClientPairingCreated, "terminal", terminalId.ToString(), new { posId = terminal.PosId, expires }, terminalId, ct);
        return (code, expires);
    }

    /// <summary>The current pairing code, if one exists and has not expired.</summary>
    public (string Code, DateTime Expires)? CurrentPairing(TerminalEdit t) =>
        t.ClientPairing is { } p && t.ClientPairingExpires is { } e && e > clock.Time.GetUtcNow().UtcDateTime ? (Unprotect(p), DateTime.SpecifyKind(e, DateTimeKind.Utc)) : null;

    public async Task<PairResponse> PairAsync(PairRequest request, string? remoteIp, CancellationToken ct = default)
    {
        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        var now = clock.Time.GetUtcNow().UtcDateTime;
        var terminal = (await settings.TerminalsAsync(ct)).FirstOrDefault(t =>
            t.ClientPairing is { } p && t.ClientPairingExpires > now && SafeUnprotect(p) == code);
        if (terminal is null)
        {
            await audit.WriteAsync("client", AuditLog.Actions.ClientPairingRefused, "terminal", null, new { request.Hostname, remoteIp }, ct: ct);
            throw new ClientRequestException("The pairing code is not valid or has expired. Create a new installation package for this workstation.");
        }
        var business = await settings.BusinessAsync(ct) ?? throw new ClientRequestException("The business is not set up on the fiscal server.");
        var secret = ClientAuth.NewSecret();
        var endpoint = EndpointFor(request.AdvertiseUrl, remoteIp, request.ListenPort) ?? terminal.ClientEndpoint;
        await using (var conn = await db.OpenConnectionAsync(ct))
            await conn.ExecuteAsync("""
                UPDATE fiscal.terminal SET client_secret = @secret, client_paired_at = now(), client_pairing = NULL, client_pairing_expires = NULL,
                       client_endpoint = @endpoint, client_seen_at = now(), client_version = @version, hostname = coalesce(nullif(@host, ''), hostname)
                WHERE id = @id
                """, new { secret = Protect(secret), endpoint, version = request.ClientVersion, host = request.Hostname, id = terminal.Id });
        await audit.WriteAsync("client", AuditLog.Actions.ClientPaired, "terminal", terminal.Id.ToString(),
            new { terminal.PosId, request.Hostname, request.ClientVersion, endpoint }, terminal.Id, ct);
        var env = await settings.GetAsync("atk_environment", "Test", ct) ?? "Test";
        return new PairResponse(terminal.Id, terminal.PosId, business.Nui, business.BranchId, business.Name, secret, env);
    }

    /// <summary>The workstation that signed this request, or null if the signature is missing or wrong.</summary>
    public async Task<TerminalEdit?> AuthenticateAsync(HttpRequest request, byte[] body, CancellationToken ct = default)
    {
        if (!long.TryParse(request.Headers[ClientAuth.TerminalHeader], out var id)) return null;
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == id);
        if (terminal?.ClientSecret is not { } protectedSecret) return null;
        var ok = ClientAuth.Verify(Unprotect(protectedSecret), request.Method, request.Path, request.Headers[ClientAuth.TimeHeader],
            request.Headers[ClientAuth.SignatureHeader], body, clock.Time.GetUtcNow());
        if (!ok)
        {
            await audit.WriteAsync("client", AuditLog.Actions.ClientAuthFailed, "terminal", id.ToString(), new { path = request.Path.Value }, id, ct);
            return null;
        }
        return terminal;
    }

    /// <summary>Step 1 of the ATK registration: ATK verifies business, unit, POS and application.</summary>
    public async Task<EnrollStartResponse> EnrollStartAsync(TerminalEdit terminal, CancellationToken ct = default)
    {
        var (business, applicationId, atk) = await PrepareAsync(terminal, ct);
        var verify = await atk.VerifyAsync((ulong)business.Nui,
            new VerifyRequest(business.FiscalizationNo, (ulong)terminal.PosId, (ulong)business.BranchId, (ulong)applicationId), ct);
        PendingVerify[terminal.Id] = (verify, DateTime.UtcNow.AddMinutes(10));
        return new EnrollStartResponse(verify.BusinessName, business.Nui, business.BranchId, terminal.PosId);
    }

    /// <summary>Step 2: ATK signs the client's certificate request; the certificate must carry the client's key.</summary>
    public async Task<EnrollFinishResponse> EnrollFinishAsync(TerminalEdit terminal, string csrPem, CancellationToken ct = default)
    {
        if (!PendingVerify.TryRemove(terminal.Id, out var pending) || pending.Until < DateTime.UtcNow)
            throw new ClientRequestException("Start the registration again (the ATK verification has expired).");
        var (business, applicationId, atk) = await PrepareAsync(terminal, ct);
        CertificateRequest csr;
        try { csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default); }
        catch (CryptographicException) { throw new ClientRequestException("The certificate request is not valid."); }
        using var csrKey = csr.PublicKey.GetECDsaPublicKey() ?? throw new ClientRequestException("The certificate request has no ECDSA P-256 key.");

        var certificatePem = await atk.SignCsrAsync(new SignCsrRequest(pending.Verify.BusinessName, (ulong)business.Nui, (ulong)business.BranchId,
            pending.Verify.VerificationCodeText, (ulong)terminal.PosId, (ulong)applicationId, csrPem), ct);
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        using var certKey = certificate.GetECDsaPublicKey() ?? throw new CryptographicException("ATK returned a certificate without an ECDSA key.");
        if (!certKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(csrKey.ExportSubjectPublicKeyInfo()))
            throw new CryptographicException("The certificate returned by ATK does not carry the workstation's key.");

        var expires = certificate.NotAfter.ToUniversalTime();
        await using (var conn = await db.OpenConnectionAsync(ct))
            await conn.ExecuteAsync("""
                UPDATE fiscal.terminal SET certificate_pem = @certificatePem, certificate_expires = @expires, enrolled_mode = 'client',
                       key_reference = 'client', status = 'active'
                WHERE id = @id
                """, new { certificatePem, expires, id = terminal.Id });
        var env = await settings.GetAsync("atk_environment", "Test", ct) ?? "Test";
        await audit.WriteAsync("client", AuditLog.Actions.TerminalEnrolled, "terminal", terminal.Id.ToString(), new
        {
            mode = SigningModes.Client, environment = env, posId = terminal.PosId, keyStore = "workstation", certificateSerial = certificate.SerialNumber,
            certificateThumbprint = certificate.Thumbprint, certificateExpires = expires, previousCertificateExpires = terminal.CertificateExpires,
            previousMode = terminal.EnrolledMode, hostname = terminal.Hostname,
        }, terminal.Id, ct);
        if (!string.IsNullOrWhiteSpace(pending.Verify.BusinessName))
            await settings.SetManyAsync(new Dictionary<string, object?>
            {
                ["atk_business_name"] = pending.Verify.BusinessName.Trim(),
                ["atk_business_name_source"] = $"ATK registration of POS {terminal.PosId} (Fiscal Client, {env})",
            }, "client", ct);
        return new EnrollFinishResponse(certificatePem, expires);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(TerminalEdit terminal, HeartbeatRequest request, string? remoteIp, CancellationToken ct = default)
    {
        var endpoint = EndpointFor(request.AdvertiseUrl, remoteIp, request.ListenPort) ?? terminal.ClientEndpoint;
        var printers = JsonSerializer.Serialize(new { printers = request.Printers, ports = request.SerialPorts });
        await using (var conn = await db.OpenConnectionAsync(ct))
            await conn.ExecuteAsync("""
                UPDATE fiscal.terminal SET client_seen_at = now(), client_version = @version, client_endpoint = @endpoint, client_printers = @printers::jsonb
                WHERE id = @id
                """, new { version = request.ClientVersion, endpoint, printers, id = terminal.Id });
        if (endpoint != terminal.ClientEndpoint)
            log.LogInformation("Fiscal Client of POS {PosId} now answers at {Endpoint}", terminal.PosId, endpoint);
        var registered = terminal.EnrolledMode == SigningModes.Client && terminal.CertificateExpires > DateTime.UtcNow;
        return new HeartbeatResponse(registered, registered ? null : "Not registered with ATK yet.", await atkHealth.GetAsync(ct));
    }

    private async Task<(BusinessInfo Business, long ApplicationId, AtkClient Atk)> PrepareAsync(TerminalEdit terminal, CancellationToken ct)
    {
        if (terminal.Status == "disabled") throw new ClientRequestException("The workstation is disabled on the fiscal server.");
        if (terminal.EffectiveMode(await settings.SigningModeDefaultAsync(ct)) != SigningModes.Client)
            throw new ClientRequestException("This workstation is set to central signing on the fiscal server.");
        var business = await settings.BusinessAsync(ct) ?? throw new ClientRequestException("The business is not set up on the fiscal server.");
        var applicationId = await settings.GetAsync<long>("atk_application_id", 0, ct);
        if (applicationId <= 0) throw new ClientRequestException("The ATK Application ID is not set on the fiscal server.");
        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        var timeout = TimeSpan.FromSeconds(Math.Max(10, await settings.GetAsync("atk_timeout_seconds", 10, ct)));
        return (business, applicationId, atkFactory.Create(environment, timeout));
    }

    private string? SafeUnprotect(string value)
    {
        try { return Unprotect(value); } catch (CryptographicException) { return null; }
    }

    /// <summary>The address the server calls the client at: the URL it advertises, or the address it called from.</summary>
    public static string? EndpointFor(string? advertiseUrl, string? remoteIp, int port)
    {
        if (!string.IsNullOrWhiteSpace(advertiseUrl)) return advertiseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(remoteIp) || port <= 0) return null;
        var ip = System.Net.IPAddress.TryParse(remoteIp, out var a) && a.IsIPv4MappedToIPv6 ? a.MapToIPv4().ToString() : remoteIp;
        return ip.Contains(':') ? $"http://[{ip}]:{port}" : $"http://{ip}:{port}";
    }
}

/// <summary>Calls a workstation's Fiscal Client: sign a receipt, print a job.</summary>
public sealed class ClientChannel(ClientRegistry registry, PropertyClock clock)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new() { Timeout = Timeout };

    public async Task<IReadOnlyList<string>> SignAsync(TerminalEdit terminal, IReadOnlyList<SignItem> items, CancellationToken ct = default)
    {
        var response = await SendAsync<SignResponse>(terminal, "/sign", new SignRequest(items), ct);
        if (response.Signatures.Count != items.Count) throw new ClientUnavailableException($"The Fiscal Client of POS {terminal.PosId} returned {response.Signatures.Count} signatures for {items.Count} items.");
        return response.Signatures;
    }

    public Task PrintAsync(TerminalEdit terminal, PrintRequest request, CancellationToken ct = default) =>
        SendAsync<ClientError>(terminal, "/print", request, ct);

    private async Task<T> SendAsync<T>(TerminalEdit terminal, string path, object payload, CancellationToken ct)
    {
        if (terminal.ClientSecret is null || string.IsNullOrWhiteSpace(terminal.ClientEndpoint))
            throw new ClientUnavailableException($"The Fiscal Client of POS {terminal.PosId} is not installed yet.");
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(terminal.ClientEndpoint.TrimEnd('/') + "/"), path.TrimStart('/')))
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
        };
        ClientAuth.Apply(request, terminal.Id, registry.Unprotect(terminal.ClientSecret), body, clock.Time.GetUtcNow());
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ClientUnavailableException($"The Fiscal Client of POS {terminal.PosId} ({terminal.Hostname}, {terminal.ClientEndpoint}) does not answer. Check that the computer is on and the service \"Opera Cloud Fiscal Solution - Kosovo - Client\" is running.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await SafeReadAsync<ClientError>(response, ct);
                throw new ClientUnavailableException($"The Fiscal Client of POS {terminal.PosId} refused the request: {error?.Message ?? ((int)response.StatusCode).ToString()}");
            }
            return await SafeReadAsync<T>(response, ct) ?? throw new ClientUnavailableException($"The Fiscal Client of POS {terminal.PosId} sent an empty answer.");
        }
    }

    private static async Task<T?> SafeReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<T>(ct); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return default; }
    }
}

/// <summary>Signs a receipt and its QR for one workstation, on the server (central) or on the workstation (client).</summary>
public sealed class ReceiptSigner : IDisposable
{
    private readonly CouponSigner? _local;
    private readonly Func<IReadOnlyList<SignItem>, Task<IReadOnlyList<string>>>? _remote;
    private readonly ECDsa? _certificateKey;
    private IDisposable? _key;

    private ReceiptSigner(CouponSigner? local, Func<IReadOnlyList<SignItem>, Task<IReadOnlyList<string>>>? remote, ECDsa? certificateKey)
    {
        _local = local; _remote = remote; _certificateKey = certificateKey;
    }

    public static ReceiptSigner Local(ISigningKey key) => new(new CouponSigner(key), null, null) { _key = key as IDisposable };

    public void Dispose() { _key?.Dispose(); _certificateKey?.Dispose(); }

    public static ReceiptSigner Remote(Func<IReadOnlyList<SignItem>, Task<IReadOnlyList<string>>> remote, string certificatePem)
    {
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        return new(null, remote, certificate.GetECDsaPublicKey());
    }

    /// <summary>The signed coupon for ATK and the QR text.</summary>
    public async Task<(SignedPayload Signed, string Qr)> SignAsync(PosCoupon coupon)
    {
        var citizen = CouponBuilder.ToCitizenCoupon(coupon);
        if (_local is not null) return (_local.Sign(coupon), _local.Sign(citizen).ToQrString());

        var items = new[] { new SignItem("pos", Convert.ToBase64String(coupon.ToByteArray())), new SignItem("citizen", Convert.ToBase64String(citizen.ToByteArray())) };
        var signatures = await _remote!(items);
        var signed = new SignedPayload(items[0].Details, signatures[0]);
        var qr = new SignedPayload(items[1].Details, signatures[1]);
        // A receipt is archived only with signatures that match the workstation's ATK certificate.
        if (!CouponSigner.Verify(signed, _certificateKey!) || !CouponSigner.Verify(qr, _certificateKey!))
            throw new ClientUnavailableException("The Fiscal Client's signature does not match the workstation's ATK certificate. Register the workstation again.");
        return (signed, qr.ToQrString());
    }
}

public sealed class ClientRequestException(string message) : Exception(message);

/// <summary>The workstation's Fiscal Client cannot sign or print right now.</summary>
public sealed class ClientUnavailableException(string message) : Exception(message);
