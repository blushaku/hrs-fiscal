using System.Security.Cryptography;
using System.Text;
using Hrs.Fiscal.Server.Data;

namespace Hrs.Fiscal.Server.Flip;

public enum FlipAuthResult { NotRequired, Valid, Missing, Invalid }

/// <summary>
/// Access token that FLIP sends with every request to the HRS Fiscal Server.
/// Only a SHA-256 hash of the token is stored; the token itself is shown once, when it is generated.
/// Accepted forms in the configured header (default Authorization): "Bearer &lt;token&gt;", "Token &lt;token&gt;" or the bare token.
/// </summary>
public sealed class FlipAuth(SettingsStore settings, AuditLog audit)
{
    public const string DefaultHeader = "Authorization";

    public sealed record State(bool Required, string Header, bool HasToken, string? Hint, DateTime? CreatedUtc);

    public async Task<State> StateAsync(CancellationToken ct = default)
    {
        var hash = await settings.GetAsync<string>("flip_token_sha256", "", ct);
        var created = await settings.GetAsync<string>("flip_token_created", "", ct);
        return new State(
            await settings.GetAsync("flip_auth_required", false, ct),
            NormalizeHeader(await settings.GetAsync("flip_auth_header", DefaultHeader, ct)),
            !string.IsNullOrEmpty(hash),
            await settings.GetAsync<string>("flip_token_hint", "", ct) is { Length: > 0 } h ? h : null,
            DateTime.TryParse(created, null, System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c : null);
    }

    public static string NormalizeHeader(string? header) =>
        string.IsNullOrWhiteSpace(header) ? DefaultHeader : header.Trim();

    /// <summary>Creates a new token (256 bit, base64url), replacing the previous one at once. Returns the token to show once.</summary>
    public async Task<string> GenerateAsync(string actor, CancellationToken ct = default)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        await settings.SetSecretAsync(new Dictionary<string, object?>
        {
            ["flip_token_sha256"] = Hash(token),
            ["flip_token_hint"] = token[^4..],
            ["flip_token_created"] = now.ToString("O"),
            ["flip_auth_required"] = true, // a freshly generated token is enforced
        }, actor, ct);
        await audit.WriteAsync(actor, AuditLog.Actions.FlipTokenCreated, "setting", "flip_token",
            new { hint = "…" + token[^4..], created = now, required = true }, ct: ct);
        return token;
    }

    /// <summary>Checks the request's token. Constant-time comparison of SHA-256 hashes.</summary>
    public async Task<FlipAuthResult> CheckAsync(IHeaderDictionary headers, CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        var presented = ExtractToken(headers[state.Header].ToString());
        if (!state.Required) return FlipAuthResult.NotRequired;
        if (presented is null) return FlipAuthResult.Missing;
        var stored = await settings.GetAsync<string>("flip_token_sha256", "", ct);
        if (string.IsNullOrEmpty(stored)) return FlipAuthResult.Invalid; // required but no token generated: nothing can pass
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(presented)), Encoding.ASCII.GetBytes(stored))
            ? FlipAuthResult.Valid
            : FlipAuthResult.Invalid;
    }

    public static string? ExtractToken(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        var v = headerValue.Trim();
        foreach (var scheme in new[] { "Bearer ", "Token " })
            if (v.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return v[scheme.Length..].Trim();
        return v;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
