using System.Security.Cryptography;
using System.Text;

namespace Hrs.Fiscal.Core.Client;

// Messages between the fiscal server and the Fiscal Client on a workstation (JSON over HTTP in the hotel network).
// Every request after pairing carries an HMAC-SHA256 signature made with the secret both sides received at pairing,
// so neither side accepts a request from anyone else, and a recorded request cannot be replayed later.

/// <summary>Client → server, once: the one-time pairing code from the installation package.</summary>
public sealed record PairRequest(string Code, string Hostname, string ClientVersion, int ListenPort, string? AdvertiseUrl = null);

/// <summary>Server → client: who this workstation is, and the shared secret for all later requests.</summary>
public sealed record PairResponse(long TerminalId, long PosId, long Nui, long BranchId, string BusinessName, string Secret, string Environment);

/// <summary>Server → client after ATK verified the business and POS: what goes into the certificate request.</summary>
public sealed record EnrollStartResponse(string BusinessName, long Nui, long BranchId, long PosId);

/// <summary>Client → server: the certificate signing request made with the key that never leaves the workstation.</summary>
public sealed record EnrollFinishRequest(string CsrPem);

/// <summary>Server → client: ATK's certificate for this workstation.</summary>
public sealed record EnrollFinishResponse(string CertificatePem, DateTime ExpiresUtc);

/// <summary>Client → server every 15 seconds: alive, version, its address, certificate and local printers.</summary>
public sealed record HeartbeatRequest(string ClientVersion, string Hostname, int ListenPort, string? AdvertiseUrl, string? CertificateThumbprint,
    IReadOnlyList<string> Printers, IReadOnlyList<string> SerialPorts);

/// <summary>Server → client: what the server expects (e.g. register again) and whether ATK is reachable.</summary>
public sealed record HeartbeatResponse(bool Registered, string? Message, AtkHealth? Atk = null);

/// <summary>
/// ATK as the fiscal server sees it (last sending of a receipt or clock check, and the offline queue). The Fiscal Client
/// shows it at the workstation; while offline the user gets a notice (Doc. 01-06-1967 Art 26.13).
/// </summary>
public sealed record AtkHealth(string State, string Environment, int Pending, DateTime? OldestPendingUtc, DateTime? DeadlineUtc,
    DateTime? OfflineSinceUtc, DateTime? LastContactUtc, string? Message, string Language);

/// <summary>Values of <see cref="AtkHealth.State"/>.</summary>
public static class AtkStates
{
    public const string Online = "online", Pending = "pending", Offline = "offline", Unknown = "unknown";
}

/// <summary>One text to sign: the Base64 of a PosCoupon ("pos") or of a CitizenCoupon for the QR ("citizen").</summary>
public sealed record SignItem(string Kind, string Details);

public sealed record SignRequest(IReadOnlyList<SignItem> Items);

/// <summary>Base64 DER ECDSA signatures, in the same order as the items.</summary>
public sealed record SignResponse(IReadOnlyList<string> Signatures);

/// <summary>A finished ESC/POS print job for a printer connected to the workstation.</summary>
public sealed record PrintRequest(string Kind, string Address, int Baud, string Flow, string JobBase64);

public sealed record ClientError(string Message);

/// <summary>HMAC request signing shared by server and client.</summary>
public static class ClientAuth
{
    public const string TerminalHeader = "X-Fiscal-Terminal";
    public const string TimeHeader = "X-Fiscal-Time";
    public const string SignatureHeader = "X-Fiscal-Signature";
    /// <summary>Requests older or newer than this are refused (clocks are kept in sync with ATK).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, string method, string path, long unixTime, ReadOnlySpan<byte> body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body));
        var text = $"{method.ToUpperInvariant()}\n{path}\n{unixTime}\n{bodyHash}";
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    public static bool Verify(string secret, string method, string path, string? time, string? signature, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (!long.TryParse(time, out var unix) || string.IsNullOrEmpty(signature)) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - unix) > Window.TotalSeconds) return false;
        var expected = Convert.FromBase64String(Sign(secret, method, path, unix, body));
        byte[] given;
        try { given = Convert.FromBase64String(signature); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>Adds the authentication headers to an outgoing request.</summary>
    public static void Apply(HttpRequestMessage request, long terminalId, string secret, byte[] body, DateTimeOffset now)
    {
        var unix = now.ToUnixTimeSeconds();
        request.Headers.Add(TerminalHeader, terminalId.ToString());
        request.Headers.Add(TimeHeader, unix.ToString());
        request.Headers.Add(SignatureHeader, Sign(secret, request.Method.Method, request.RequestUri!.AbsolutePath, unix, body));
    }
}
