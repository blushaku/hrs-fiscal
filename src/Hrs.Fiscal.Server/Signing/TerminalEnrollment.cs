using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Signing;
using Hrs.Fiscal.Server.Data;
using Npgsql;

namespace Hrs.Fiscal.Server.Signing;

public static class SigningModes
{
    public const string Client = "client";
    public const string Server = "server";

    public static bool IsValid(string? mode) => mode is Client or Server;

    public static string Label(string mode) => mode == Server ? "Server (central)" : "Workstation client";
}

/// <summary>Creates ATK API clients; replaced in tests.</summary>
public interface IAtkClientFactory
{
    AtkClient Create(AtkEnvironment environment, TimeSpan timeout);
}

public sealed class AtkClientFactory : IAtkClientFactory
{
    public AtkClient Create(AtkEnvironment environment, TimeSpan timeout) =>
        new(new HttpClient { BaseAddress = AtkEndpoints.BaseUri(environment), Timeout = timeout });
}

/// <summary>
/// Central signing mode: registers a workstation with ATK from the server (ATK verify + CSR signing) and opens its key
/// when a receipt has to be signed on its behalf.
/// </summary>
public sealed class TerminalEnrollment(
    NpgsqlDataSource db, SettingsStore settings, AuditLog audit, IServerKeyStore keys, IAtkClientFactory atkFactory)
{
    public sealed record Result(DateTime CertificateExpiresUtc, string BusinessName, string KeyStoreKind, AtkEnvironment Environment);

    public async Task<Result> EnrollAsync(long terminalId, string actor, CancellationToken ct = default)
    {
        var business = await settings.BusinessAsync(ct) ?? throw new InvalidOperationException("Set up the business first.");
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId)
                       ?? throw new InvalidOperationException("Unknown workstation.");
        var defaultMode = await settings.SigningModeDefaultAsync(ct);
        if (terminal.EffectiveMode(defaultMode) != SigningModes.Server)
            throw new InvalidOperationException("This workstation signs with the HRS Fiscal Client. Register it from the client on that computer.");
        if (terminal.Status == "disabled") throw new InvalidOperationException("The workstation is disabled.");

        var applicationId = await settings.GetAsync<long>("atk_application_id", 0, ct);
        if (applicationId <= 0) throw new InvalidOperationException("Enter the ATK Application ID under Settings › General first.");
        var environment = Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test");
        var timeout = TimeSpan.FromSeconds(Math.Max(10, await settings.GetAsync("atk_timeout_seconds", 10, ct)));
        var atk = atkFactory.Create(environment, timeout);

        var nui = (ulong)business.Nui;
        var pos = (ulong)terminal.PosId;
        var branch = (ulong)business.BranchId;

        // 1. ATK checks the business, unit, POS and application.
        var verify = await atk.VerifyAsync(nui, new VerifyRequest(business.FiscalizationNo, pos, branch, (ulong)applicationId), ct);

        // 2. New key in the server key store; only the CSR (public key) goes to ATK.
        var (key, reference) = keys.Create(business.BranchId, terminal.PosId);
        string certificatePem;
        X509Certificate2 certificate;
        using (key as IDisposable)
        {
            var csr = CsrFactory.CreatePem(key.PublicKey, nui, pos, branch, verify.BusinessName);
            certificatePem = await atk.SignCsrAsync(
                new SignCsrRequest(verify.BusinessName, nui, branch, verify.VerificationCodeText, pos, (ulong)applicationId, csr), ct);

            // 3. The certificate must carry this key.
            certificate = X509Certificate2.CreateFromPem(certificatePem);
            using var certKey = certificate.GetECDsaPublicKey() ?? throw new CryptographicException("ATK returned a certificate without an ECDSA key.");
            var probe = "hrs-fiscal-enrolment"u8.ToArray();
            if (!certKey.VerifyData(probe, key.SignData(probe), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CryptographicException("The certificate returned by ATK does not match the new key.");
        }

        var expires = certificate.NotAfter.ToUniversalTime();
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            await conn.ExecuteAsync("""
                UPDATE fiscal.terminal SET certificate_pem = @certificatePem, certificate_expires = @expires, enrolled_mode = 'server',
                       key_reference = @reference, status = 'active'
                WHERE id = @terminalId
                """, new { certificatePem, expires, reference, terminalId });
        }
        await audit.WriteAsync(actor, AuditLog.Actions.TerminalEnrolled, "terminal", terminalId.ToString(), new
        {
            mode = SigningModes.Server, environment = environment.ToString(), posId = terminal.PosId, keyStore = keys.Kind,
            keyReference = reference, certificateSerial = certificate.SerialNumber, certificateThumbprint = certificate.Thumbprint,
            certificateExpires = expires, previousCertificateExpires = terminal.CertificateExpires, previousMode = terminal.EnrolledMode,
        }, terminalId, ct);
        return new Result(expires, verify.BusinessName, keys.Kind, environment);
    }

    /// <summary>
    /// Central mode, alternative to <see cref="EnrollAsync"/>: takes over a key and certificate made with ATK's onboarder
    /// tool for this workstation. The certificate must name this business (O), POS (OU) and unit (L) and match the key.
    /// </summary>
    public async Task<Result> ImportAsync(long terminalId, string keyPem, string certificatePem, string actor, CancellationToken ct = default)
    {
        var business = await settings.BusinessAsync(ct) ?? throw new InvalidOperationException("Set up the business first.");
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId)
                       ?? throw new InvalidOperationException("Unknown workstation.");
        if (terminal.EffectiveMode(await settings.SigningModeDefaultAsync(ct)) != SigningModes.Server)
            throw new InvalidOperationException("This workstation signs with the HRS Fiscal Client. Import the certificate on that computer.");

        X509Certificate2 certificate;
        try { certificate = X509Certificate2.CreateFromPem(certificatePem); }
        catch (CryptographicException) { throw new InvalidOperationException("The certificate file is not a PEM certificate."); }
        string Part(string oid) => certificate.SubjectName.EnumerateRelativeDistinguishedNames()
            .FirstOrDefault(r => r.GetSingleElementType().Value == oid)?.GetSingleElementValue() ?? "";
        if (Part("2.5.4.10") != business.Nui.ToString() || Part("2.5.4.11") != terminal.PosId.ToString() || Part("2.5.4.7") != business.BranchId.ToString())
            throw new InvalidOperationException($"The certificate is for NUI {Part("2.5.4.10")}, POS {Part("2.5.4.11")}, unit {Part("2.5.4.7")}; " +
                                                $"this workstation is NUI {business.Nui}, POS {terminal.PosId}, unit {business.BranchId}.");
        using (var probeKey = PemSigningKey.FromPem(keyPem))
        using (var certKey = certificate.GetECDsaPublicKey() ?? throw new CryptographicException("The certificate has no ECDSA key."))
        {
            var probe = "hrs-fiscal-import"u8.ToArray();
            if (!certKey.VerifyData(probe, probeKey.SignData(probe), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new InvalidOperationException("The private key does not belong to this certificate.");
        }

        var (key, reference) = keys.Import(business.BranchId, terminal.PosId, keyPem, certificatePem);
        (key as IDisposable)?.Dispose();
        var expires = certificate.NotAfter.ToUniversalTime();
        await using (var conn = await db.OpenConnectionAsync(ct))
            await conn.ExecuteAsync("""
                UPDATE fiscal.terminal SET certificate_pem = @certificatePem, certificate_expires = @expires, enrolled_mode = 'server',
                       key_reference = @reference, status = 'active'
                WHERE id = @terminalId
                """, new { certificatePem, expires, reference, terminalId });
        await audit.WriteAsync(actor, AuditLog.Actions.TerminalEnrolled, "terminal", terminalId.ToString(), new
        {
            mode = SigningModes.Server, method = "import (ATK onboarder)", posId = terminal.PosId, keyStore = keys.Kind,
            keyReference = reference, certificateSerial = certificate.SerialNumber, certificateThumbprint = certificate.Thumbprint, certificateExpires = expires,
        }, terminalId, ct);
        return new Result(expires, Part("2.5.4.3"), keys.Kind, Enum.Parse<AtkEnvironment>(await settings.GetAsync("atk_environment", "Test", ct) ?? "Test"));
    }

    /// <summary>The key to sign with for a workstation in central mode. Fails if it must be registered (again) first.</summary>
    public async Task<ISigningKey> OpenKeyAsync(long terminalId, CancellationToken ct = default)
    {
        var terminal = (await settings.TerminalsAsync(ct)).SingleOrDefault(t => t.Id == terminalId)
                       ?? throw new InvalidOperationException("Unknown workstation.");
        var state = terminal.SigningState(await settings.SigningModeDefaultAsync(ct));
        if (state != TerminalSigningState.Ready || terminal.EffectiveMode(await settings.SigningModeDefaultAsync(ct)) != SigningModes.Server)
            throw new InvalidOperationException($"Workstation POS {terminal.PosId} cannot be signed for on the server ({state}).");
        return keys.Open(terminal.KeyReference!);
    }
}
