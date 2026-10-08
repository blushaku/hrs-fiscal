using System.Net;
using System.Text;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Signing;

namespace Hrs.Fiscal.Core.Tests;

public class AtkClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static (AtkClient, StubHandler) Client(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var http = new HttpClient(handler) { BaseAddress = AtkEndpoints.BaseUri(AtkEnvironment.Test) };
        return (new AtkClient(http), handler);
    }

    private static readonly SignedPayload Payload = new("ZGV0YWlscw==", "c2ln");

    [Fact]
    public async Task Accepted_returns_transaction_id_and_posts_details_and_signature()
    {
        var (client, handler) = Client(HttpStatusCode.OK, """{"message":"ok","transaction_id":123456789}""");

        var result = await client.SendPosCouponAsync(Payload);

        Assert.Equal(AtkOutcome.Accepted, result.Outcome);
        Assert.Equal(123456789ul, result.TransactionId);
        Assert.Equal("https://fiskalizimi-test.atk-ks.org/pos/coupon", handler.LastUri!.ToString());
        Assert.Contains("\"details\":\"ZGV0YWlscw==\"", handler.LastBody);
        Assert.Contains("\"signature\":\"c2ln\"", handler.LastBody);
    }

    [Fact]
    public async Task Bad_request_is_a_final_rejection()
    {
        var (client, _) = Client(HttpStatusCode.BadRequest, """{"error":"details and signature don't match"}""");
        var result = await client.SendPosCouponAsync(Payload);
        Assert.Equal(AtkOutcome.Rejected, result.Outcome);
        Assert.Equal("details and signature don't match", result.Message);
    }

    [Fact]
    public async Task Server_error_is_transient()
    {
        var (client, _) = Client(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        Assert.Equal(AtkOutcome.Transient, (await client.SendPosCouponAsync(Payload)).Outcome);
    }

    [Fact]
    public async Task Network_failure_is_transient()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route to host"));
        var client = new AtkClient(new HttpClient(handler) { BaseAddress = AtkEndpoints.BaseUri(AtkEnvironment.Test) });
        var result = await client.SendPosCouponAsync(Payload);
        Assert.Equal(AtkOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task Sign_csr_sends_both_verification_field_spellings()
    {
        var (client, handler) = Client(HttpStatusCode.OK, """{"signed_certificate":"-----BEGIN CERTIFICATE-----"}""");

        var cert = await client.SignCsrAsync(new SignCsrRequest("Hotel", 1, 2, "4711", 3, 4, "csr"));

        Assert.StartsWith("-----BEGIN CERTIFICATE", cert);
        Assert.Contains("\"verification_code\":\"4711\"", handler.LastBody);
        Assert.Contains("\"verification_no\":4711", handler.LastBody);
    }
}
