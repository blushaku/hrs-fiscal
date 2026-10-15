using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Hrs.Fiscal.Core.Client;
using Hrs.Fiscal.Core.Signing;
using Microsoft.AspNetCore.DataProtection;

namespace Hrs.Fiscal.Client;

public sealed class ClientOptions
{
    /// <summary>Fiscal server address as the admin opened it, e.g. http://192.168.1.10:5080 (also in pairing.json).</summary>
    public string? ServerUrl { get; set; }
    /// <summary>Port this client listens on for the server's requests.</summary>
    public int Port { get; set; } = 5110;
    /// <summary>Address the server should call; empty = the address the client connects from, with <see cref="Port"/>.</summary>
    public string? AdvertiseUrl { get; set; }
    /// <summary>Data folder (state, key file in test mode, signing log). Default: %ProgramData%\Opera Cloud Fiscal Solution\Client.</summary>
    public string? DataFolder { get; set; }
    /// <summary>auto (CNG on Windows, file elsewhere) | cng | file (test only).</summary>
    public string KeyStore { get; set; } = "auto";
    public bool PreferTpm { get; set; } = true;
    /// <summary>Pairing code, when not taken from pairing.json.</summary>
    public string? PairingCode { get; set; }
    /// <summary>How often the client reports to the server and learns ATK's status (offline notice within this time).</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    public string Folder() => string.IsNullOrWhiteSpace(DataFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } pd ? pd : AppContext.BaseDirectory,
            "Opera Cloud Fiscal Solution", "Client")
        : DataFolder;
}

/// <summary>What the client knows about itself. The shared secret is kept protected (DPAPI, machine scope, on Windows).</summary>
public sealed class ClientState
{
    public string? ServerUrl { get; set; }
    public long TerminalId { get; set; }
    public long PosId { get; set; }
    public long Nui { get; set; }
    public long BranchId { get; set; }
    public string? BusinessName { get; set; }
    public string? Environment { get; set; }
    public string? SecretProtected { get; set; }
    public string? KeyReference { get; set; }
    public string? CertificatePem { get; set; }
    public DateTime? CertificateExpiresUtc { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSignedUtc { get; set; }
    public long SignedCount { get; set; }

    public bool Paired => TerminalId > 0 && SecretProtected is not null;
    public bool Registered => Paired && CertificatePem is not null && KeyReference is not null;
}

/// <summary>Loads and saves <see cref="ClientState"/>, opens the key, protects the secret.</summary>
public sealed class ClientStore
{
    private readonly string _folder;
    private readonly IDataProtector _protector;
    private readonly ClientOptions _options;
    private readonly object _gate = new();
    private ClientState _state;
    public static string Version { get; } =
        typeof(ClientStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public ClientStore(ClientOptions options, IDataProtectionProvider protection)
    {
        _options = options;
        _folder = options.Folder();
        Directory.CreateDirectory(_folder);
        _protector = protection.CreateProtector("Hrs.Fiscal.Client.State.v1");
        _state = File.Exists(StatePath) ? JsonSerializer.Deserialize<ClientState>(File.ReadAllText(StatePath)) ?? new() : new();
    }

    public string Folder => _folder;
    private string StatePath => Path.Combine(_folder, "state.json");
    public string PairingPath => Path.Combine(_folder, "pairing.json");
    public string KeyKind => ResolveKeyStore();

    public ClientState State { get { lock (_gate) return _state; } }

    /// <summary>ATK's status from the server's last answer (kept in memory only; refreshed every heartbeat).</summary>
    public AtkHealth? Atk { get; set; }

    public void Update(Action<ClientState> change)
    {
        lock (_gate)
        {
            change(_state);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StatePath, overwrite: true);
        }
    }

    public string Protect(string secret) => _protector.Protect(secret);
    public string? Secret => State.SecretProtected is { } p ? _protector.Unprotect(p) : null;

    private string ResolveKeyStore() => _options.KeyStore.ToLowerInvariant() switch
    {
        "auto" => OperatingSystem.IsWindows() ? "cng" : "file",
        "cng" => "cng",
        "file" => "file",
        var other => throw new InvalidOperationException($"Client:KeyStore must be auto, cng or file (was '{other}')."),
    };

    /// <summary>A new key for this workstation: CNG machine key, non-exportable, in the TPM when there is one.</summary>
    public (ISigningKey Key, string Reference) CreateKey(long branchId, long posId)
    {
        var name = $"FiscalKosovoClient-{branchId}-{posId}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
        if (KeyKind == "cng")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CNG keys require Windows.");
            return (CngSigningKey.OpenOrCreate(name, _options.PreferTpm), "cng:" + name);
        }
        var key = PemSigningKey.Generate();
        File.WriteAllText(Path.Combine(_folder, name + ".key"), _protector.Protect(key.ExportPrivateKeyPem()));
        return (key, "file:" + name);
    }

    public ISigningKey OpenKey(string reference)
    {
        if (reference.StartsWith("cng:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CNG keys require Windows.");
            var name = reference[4..];
            if (!CngSigningKey.Exists(name) && !CngSigningKey.Exists(name, CngSigningKey.TpmProvider))
                throw new CryptographicException($"The key {name} is not on this computer any more. Register the workstation again.");
            return CngSigningKey.OpenOrCreate(name, _options.PreferTpm);
        }
        if (reference.StartsWith("file:", StringComparison.Ordinal))
            return PemSigningKey.FromPem(_protector.Unprotect(File.ReadAllText(Path.Combine(_folder, reference[5..] + ".key"))));
        throw new CryptographicException($"Unknown key reference {reference}.");
    }
}

/// <summary>pairing.json, written by the installation package: where the server is and the one-time code.</summary>
public sealed record PairingFile(string ServerUrl, string Code, long PosId, DateTime ExpiresUtc);
