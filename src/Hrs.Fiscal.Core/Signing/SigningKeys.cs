using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hrs.Fiscal.Core.Signing;

/// <summary>
/// A per-workstation ECDSA P-256 key. ATK: "the private key must never leave the machine on which it was generated".
/// Production implementations keep the key non-exportable (Windows CNG / TPM); see <see cref="CngSigningKey"/>.
/// </summary>
public interface ISigningKey
{
    /// <summary>ECDSA-SHA256 signature over <paramref name="data"/>, DER encoded.</summary>
    byte[] SignData(byte[] data);

    ECDsa PublicKey { get; }
}

/// <summary>In-memory/PEM key. For tests and the ATK TEST environment only.</summary>
public sealed class PemSigningKey : ISigningKey, IDisposable
{
    private readonly ECDsa _ecdsa;

    private PemSigningKey(ECDsa ecdsa) => _ecdsa = ecdsa;

    public static PemSigningKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static PemSigningKey FromPem(string pem)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        if (ecdsa.KeySize != 256) throw new CryptographicException("ATK requires an ECDSA P-256 key.");
        return new PemSigningKey(ecdsa);
    }

    public string ExportPrivateKeyPem() => _ecdsa.ExportECPrivateKeyPem();

    public ECDsa PublicKey => _ecdsa;

    public ECDsa Ecdsa => _ecdsa;

    public byte[] SignData(byte[] data) =>
        _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>
/// Windows CNG persisted key, created non-exportable (optionally in the TPM via the
/// "Microsoft Platform Crypto Provider"). The private key cannot be copied off the machine.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CngSigningKey : ISigningKey, IDisposable
{
    public const string SoftwareProvider = "Microsoft Software Key Storage Provider";
    public const string TpmProvider = "Microsoft Platform Crypto Provider";

    private readonly ECDsaCng _ecdsa;

    private CngSigningKey(CngKey key) => _ecdsa = new ECDsaCng(key);

    public static bool Exists(string keyName, string provider = SoftwareProvider) =>
        OperatingSystem.IsWindows() && CngKey.Exists(keyName, new CngProvider(provider), CngKeyOpenOptions.MachineKey);

    public static CngSigningKey OpenOrCreate(string keyName, bool preferTpm = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CNG keys require Windows.");

        foreach (var provider in preferTpm ? new[] { TpmProvider, SoftwareProvider } : new[] { SoftwareProvider })
        {
            var cngProvider = new CngProvider(provider);
            if (CngKey.Exists(keyName, cngProvider, CngKeyOpenOptions.MachineKey))
                return new CngSigningKey(CngKey.Open(keyName, cngProvider, CngKeyOpenOptions.MachineKey));

            try
            {
                var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
                {
                    Provider = cngProvider,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                    ExportPolicy = CngExportPolicies.None, // non-exportable
                });
                return new CngSigningKey(key);
            }
            catch (CryptographicException) when (provider == TpmProvider)
            {
                // No TPM or TPM unavailable: fall back to the software provider (still non-exportable).
            }
        }

        throw new CryptographicException($"Could not open or create CNG key '{keyName}'.");
    }

    public ECDsa PublicKey => _ecdsa;

    public byte[] SignData(byte[] data) =>
        _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose() => _ecdsa.Dispose();
}

public static class CertificateInfo
{
    /// <summary>Reads a PEM certificate returned by ATK's /ca/signcsr and returns its expiry.</summary>
    public static DateTime ExpiresUtc(string certificatePem) =>
        X509Certificate2.CreateFromPem(certificatePem).NotAfter.ToUniversalTime();
}
