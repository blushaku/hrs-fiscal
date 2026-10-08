using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hrs.Fiscal.Core.Signing;

namespace Hrs.Fiscal.Core.Atk;

public enum AtkEnvironment { Test, Production }

public static class AtkEndpoints
{
    public static Uri BaseUri(AtkEnvironment env) => env == AtkEnvironment.Production
        ? new Uri("https://fiskalizimi.atk-ks.org/")
        : new Uri("https://fiskalizimi-test.atk-ks.org/");

    public const string PosCoupon = "pos/coupon";
    public const string CaSignCsr = "ca/signcsr";
    public static string CaVerify(ulong nui) => $"ca/verify/{nui}";
}

/// <summary>Outcome classes drive retry policy: Rejected is final, Transient goes to the offline queue.</summary>
public enum AtkOutcome { Accepted, Rejected, Transient }

public sealed record AtkSendResult(AtkOutcome Outcome, ulong? TransactionId, string? Message, int? HttpStatus)
{
    public static AtkSendResult Transient(string message, int? status = null) => new(AtkOutcome.Transient, null, message, status);
}

/// <summary>Typed client for ATK's fiscalization API (Swagger v0.9).</summary>
public sealed class AtkClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    /// <param name="http">HttpClient with BaseAddress = <see cref="AtkEndpoints.BaseUri"/> and a sensible Timeout (e.g. 10s).</param>
    public AtkClient(HttpClient http) => _http = http;

    /// <summary>POST /pos/coupon. Never throws for network/server errors; returns Transient instead.</summary>
    public async Task<AtkSendResult> SendPosCouponAsync(SignedPayload payload, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(AtkEndpoints.PosCoupon,
                new PosCouponRequest(payload.Details, payload.Signature), Json, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var ok = JsonSerializer.Deserialize<PosCouponResponse>(body, Json);
                return new AtkSendResult(AtkOutcome.Accepted, ok?.TransactionId, ok?.Message, (int)response.StatusCode);
            }

            var error = TryReadError(body);
            // 400 = ATK rejected the coupon (bad signature/data). Retrying the same payload will not help.
            if (response.StatusCode == HttpStatusCode.BadRequest)
                return new AtkSendResult(AtkOutcome.Rejected, null, error, 400);

            return AtkSendResult.Transient(error ?? response.ReasonPhrase ?? "ATK error", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return AtkSendResult.Transient(ex.Message);
        }
    }

    /// <summary>Onboarding step 1: POST /ca/verify/{nui}.</summary>
    public async Task<VerifyResponse> VerifyAsync(ulong nui, VerifyRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(AtkEndpoints.CaVerify(nui), request, Json, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AtkApiException((int)response.StatusCode, TryReadError(body) ?? body);
        return JsonSerializer.Deserialize<VerifyResponse>(body, Json)
               ?? throw new AtkApiException((int)response.StatusCode, "Empty verify response");
    }

    /// <summary>Onboarding step 2: POST /ca/signcsr. Returns the signed certificate (PEM).</summary>
    public async Task<string> SignCsrAsync(SignCsrRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(AtkEndpoints.CaSignCsr, request, Json, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AtkApiException((int)response.StatusCode, TryReadError(body) ?? body);
        return JsonSerializer.Deserialize<SignCsrResponse>(body, Json)?.SignedCertificate
               ?? throw new AtkApiException((int)response.StatusCode, "No certificate returned");
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString();
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }
}

public sealed class AtkApiException(int status, string message) : Exception($"ATK API {status}: {message}")
{
    public int Status { get; } = status;
}

public sealed record PosCouponRequest(
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("signature")] string Signature);

public sealed record PosCouponResponse(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("transaction_id")] ulong? TransactionId);

public sealed record VerifyRequest(
    [property: JsonPropertyName("fiscalization_no")] string FiscalizationNo,
    [property: JsonPropertyName("pos_id")] ulong PosId,
    [property: JsonPropertyName("branch_id")] ulong BranchId,
    [property: JsonPropertyName("application_id")] ulong ApplicationId);

public sealed record VerifyResponse(
    [property: JsonPropertyName("business_name")] string BusinessName,
    [property: JsonPropertyName("verification_code")] JsonElement VerificationCode) // string in README, integer in Swagger
{
    public string VerificationCodeText => VerificationCode.ValueKind == JsonValueKind.String
        ? VerificationCode.GetString()!
        : VerificationCode.GetRawText();
}

/// <summary>
/// README names the field "verification_code" (string); Swagger v0.9 names it "verification_no" (integer).
/// Both are sent until ATK confirms which one is authoritative.
/// </summary>
public sealed record SignCsrRequest(
    [property: JsonPropertyName("business_name")] string BusinessName,
    [property: JsonPropertyName("business_id")] ulong BusinessId,
    [property: JsonPropertyName("branch_id")] ulong BranchId,
    [property: JsonPropertyName("verification_code")] string VerificationCode,
    [property: JsonPropertyName("pos_id")] ulong PosId,
    [property: JsonPropertyName("application_id")] ulong ApplicationId,
    [property: JsonPropertyName("csr")] string Csr)
{
    [JsonPropertyName("verification_no")]
    public ulong? VerificationNo => ulong.TryParse(VerificationCode, out var n) ? n : null;
}

public sealed record SignCsrResponse([property: JsonPropertyName("signed_certificate")] string SignedCertificate);
