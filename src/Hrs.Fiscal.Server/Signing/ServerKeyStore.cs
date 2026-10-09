using System.Security.Cryptography;
using Hrs.Fiscal.Core.Signing;
using Microsoft.AspNetCore.DataProtection;

namespace Hrs.Fiscal.Server.Signing;

/// <summary>
/// Keys the server holds for workstations in central signing mode: one ECDSA P-256 key per workstation.
/// Only a reference to the key (e.g. "cng:HRS-Fiscal-1-11-20261009T101500Z") is stored in the database.
/// </summary>
public interface IServerKeyStore
{
    /// <summary>"cng" (Windows, non-exportable, TPM when available) or "file" (development / test only).</summary>
    string Kind { get; }

    /// <summary>Creates a new key for the workstation. A renewal creates a new key; the old one stays until removed by an admin.</summary>
    (ISigningKey Key, string Reference) Create(long branchId, long posId);

    ISigningKey Open(string reference);
}

public sealed class SigningOptions
{
    /// <summary>auto (cng on Windows, file elsewhere) | cng | file.</summary>
    public string KeyStore { get; set; } = "auto";
    public bool PreferTpm { get; set; } = true;
    /// <summary>Folder for the file key store. Default: %ProgramData%\HRS Fiscal\keys (or ./keys).</summary>
    public string? KeyFolder { get; set; }

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } pd ? pd : AppContext.BaseDirectory,
            "HRS Fiscal", "keys");
}

internal static class KeyNames
{
    public static string For(long branchId, long posId) => $"HRS-Fiscal-{branchId}-{posId}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
}

/// <summary>Windows CNG machine keys, created non-exportable. Uses the TPM ("Microsoft Platform Crypto Provider") when present.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CngServerKeyStore(bool preferTpm) : IServerKeyStore
{
    public string Kind => "cng";

    public (ISigningKey Key, string Reference) Create(long branchId, long posId)
    {
        var name = KeyNames.For(branchId, posId);
        return (CngSigningKey.OpenOrCreate(name, preferTpm), "cng:" + name);
    }

    public ISigningKey Open(string reference)
    {
        if (!reference.StartsWith("cng:", StringComparison.Ordinal)) throw new CryptographicException($"Not a CNG key reference: {reference}");
        var name = reference[4..];
        if (!CngSigningKey.Exists(name) && !CngSigningKey.Exists(name, CngSigningKey.TpmProvider))
            throw new CryptographicException($"Signing key {name} was not found on this server. Register the workstation again.");
        return CngSigningKey.OpenOrCreate(name, preferTpm);
    }
}

/// <summary>
/// DEVELOPMENT / TEST ONLY. Keys are PEM files encrypted with ASP.NET Core Data Protection (DPAPI on Windows).
/// Unlike CNG, a server administrator could in principle extract them, so production uses <see cref="CngServerKeyStore"/>.
/// </summary>
public sealed class FileServerKeyStore(string folder, IDataProtectionProvider protection) : IServerKeyStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("Hrs.Fiscal.Server.SigningKeys.v1");

    public string Kind => "file";

    public (ISigningKey Key, string Reference) Create(long branchId, long posId)
    {
        Directory.CreateDirectory(folder);
        var name = KeyNames.For(branchId, posId);
        var key = PemSigningKey.Generate();
        File.WriteAllText(PathFor(name), _protector.Protect(key.ExportPrivateKeyPem()));
        return (key, "file:" + name);
    }

    public ISigningKey Open(string reference)
    {
        if (!reference.StartsWith("file:", StringComparison.Ordinal)) throw new CryptographicException($"Not a file key reference: {reference}");
        var path = PathFor(reference[5..]);
        if (!File.Exists(path)) throw new CryptographicException($"Signing key {reference} was not found on this server. Register the workstation again.");
        return PemSigningKey.FromPem(_protector.Unprotect(File.ReadAllText(path)));
    }

    private string PathFor(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) throw new CryptographicException("Invalid key name.");
        return Path.Combine(folder, name + ".key");
    }
}

public static class ServerKeyStoreSetup
{
    public static IServiceCollection AddServerKeyStore(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Signing").Get<SigningOptions>() ?? new SigningOptions();
        var folder = string.IsNullOrWhiteSpace(options.KeyFolder) ? SigningOptions.DefaultFolder() : options.KeyFolder;
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(folder, "protection")))
            .SetApplicationName("HRS Fiscal Server");

        var kind = options.KeyStore.ToLowerInvariant() switch
        {
            "auto" => OperatingSystem.IsWindows() ? "cng" : "file",
            "cng" or "file" => options.KeyStore.ToLowerInvariant(),
            _ => throw new InvalidOperationException($"Signing:KeyStore must be auto, cng or file (was '{options.KeyStore}')."),
        };
        if (kind == "cng")
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Signing:KeyStore=cng requires Windows.");
            services.AddSingleton<IServerKeyStore>(new CngServerKeyStore(options.PreferTpm));
        }
        else
        {
            services.AddSingleton<IServerKeyStore>(sp => new FileServerKeyStore(folder, sp.GetRequiredService<IDataProtectionProvider>()));
        }
        return services;
    }
}
