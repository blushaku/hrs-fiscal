using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Signing;

namespace Hrs.Fiscal.Cli;

/// <summary>Creates the test workstation profile: from ATK's onboarder export, or by registering with ATK directly.</summary>
public static class Workstation
{
    public static AtkClient Atk(AtkEnvironment env) => new(new HttpClient
    {
        BaseAddress = AtkEndpoints.BaseUri(env),
        Timeout = TimeSpan.FromSeconds(20),
    });

    public static async Task<Profile> ImportAsync(string dir, string keyPem, string certPem, ulong applicationId, AtkEnvironment env,
        string? fiscalNo, string? location)
    {
        using var key = PemSigningKey.FromPem(keyPem);
        var cert = X509Certificate2.CreateFromPem(certPem);

        using var certKey = cert.GetECDsaPublicKey() ?? throw new ArgumentException("The certificate has no ECDSA public key.");
        var probe = "hrs-fiscal-import"u8.ToArray();
        if (!certKey.VerifyData(probe, key.SignData(probe), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new ArgumentException("The private key does not belong to this certificate.");

        // ATK subject layout: C=RKS, O=<NUI>, OU=<POS ID>, L=<branch>, CN=<business name>
        string Part(string oid) => cert.SubjectName.EnumerateRelativeDistinguishedNames()
            .FirstOrDefault(r => r.GetSingleElementType().Value == oid)?.GetSingleElementValue() ?? "";
        var profile = new Profile
        {
            Environment = env,
            Nui = ulong.Parse(Part("2.5.4.10"), CultureInfo.InvariantCulture),
            PosId = ulong.Parse(Part("2.5.4.11"), CultureInfo.InvariantCulture),
            BranchId = ulong.Parse(Part("2.5.4.7"), CultureInfo.InvariantCulture),
            BusinessName = Part("2.5.4.3"),
            ApplicationId = applicationId,
            FiscalizationNo = fiscalNo ?? "",
            Location = string.IsNullOrWhiteSpace(location) ? "Prishtinë" : location,
            CertificateExpiresUtc = cert.NotAfter.ToUniversalTime(),
        };

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "private-key.pem"), keyPem);
        await File.WriteAllTextAsync(Path.Combine(dir, "certificate.pem"), certPem);
        await profile.SaveAsync(dir);
        return profile;
    }

    public static async Task<Profile> OnboardAsync(string dir, ulong nui, string fiscalNo, ulong branch, ulong pos, ulong applicationId,
        AtkEnvironment env, string? location, Action<string>? progress = null)
    {
        var profile = new Profile
        {
            Environment = env, Nui = nui, FiscalizationNo = fiscalNo, BranchId = branch, PosId = pos, ApplicationId = applicationId,
            Location = string.IsNullOrWhiteSpace(location) ? "Prishtinë" : location,
        };
        var atk = Atk(env);

        progress?.Invoke($"1/3 Verifying business {nui} with ATK ({env})…");
        var verify = await atk.VerifyAsync(nui, new VerifyRequest(fiscalNo, pos, branch, applicationId));
        profile.BusinessName = verify.BusinessName;
        progress?.Invoke($"    business: {verify.BusinessName}");

        progress?.Invoke("2/3 Generating P-256 key and CSR…");
        using var key = PemSigningKey.Generate();
        var csr = CsrFactory.CreatePem(key.Ecdsa, nui, pos, branch, verify.BusinessName);

        progress?.Invoke("3/3 Sending CSR to ATK CA…");
        var cert = await atk.SignCsrAsync(new SignCsrRequest(verify.BusinessName, nui, branch, verify.VerificationCodeText, pos, applicationId, csr));

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "private-key.pem"), key.ExportPrivateKeyPem());
        await File.WriteAllTextAsync(Path.Combine(dir, "certificate.pem"), cert);
        profile.CertificateExpiresUtc = CertificateInfo.ExpiresUtc(cert);
        await profile.SaveAsync(dir);
        return profile;
    }

    public static async Task<(Profile Profile, PemSigningKey Key)> LoadAsync(string dir)
    {
        var profile = await Profile.LoadAsync(dir);
        var key = PemSigningKey.FromPem(await File.ReadAllTextAsync(Path.Combine(dir, "private-key.pem")));
        return (profile, key);
    }
}
