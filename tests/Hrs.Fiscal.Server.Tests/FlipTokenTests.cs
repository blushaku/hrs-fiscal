using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Hrs.Fiscal.Server.Tests;

public class FlipTokenTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    private static HttpRequestMessage Folio(string? authorization = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/flip/fiscal?terminal=FO1")
        {
            Content = new StringContent("<Folio No=\"1\"/>", Encoding.UTF8, "application/xml"),
        };
        if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        return req;
    }

    [PgFact]
    public async Task Token_is_shown_once_stored_as_hash_and_enforced()
    {
        var flip = app.Anonymous();
        // No token generated yet: accepted (the firewall limits the source IP).
        Assert.Equal(HttpStatusCode.OK, (await flip.SendAsync(Folio())).StatusCode);

        var admin = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        var af = await ServerFixture.AntiforgeryTokenAsync(admin, "/Settings");
        var gen = await admin.PostAsync("/Settings?handler=GenerateToken",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = af }));
        Assert.Equal(HttpStatusCode.Redirect, gen.StatusCode);
        var page = await admin.GetStringAsync("/Settings");
        var token = Regex.Match(page, "id=\"flip-token\">([A-Za-z0-9_-]{40,})<").Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.DoesNotContain(token, await admin.GetStringAsync("/Settings")); // shown only once
        Assert.Contains("…" + token[^4..], await admin.GetStringAsync("/Settings"));

        // Missing and wrong tokens are refused, recorded without body, and audited.
        var missing = await flip.SendAsync(Folio());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("rejected", await app.ScalarAsync<string>("SELECT mode FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));
        Assert.Equal(0, await app.ScalarAsync<int>("SELECT length(body) FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await flip.SendAsync(Folio("Bearer not-the-token"))).StatusCode);
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'FLIP_AUTH_FAILED'"));

        // The right token works as "Bearer <token>" and bare.
        Assert.Equal(HttpStatusCode.OK, (await flip.SendAsync(Folio("Bearer " + token))).StatusCode);
        Assert.Equal("valid", await app.ScalarAsync<string>("SELECT headers->>'X-HRS-Token-Check' FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));
        Assert.Equal("[present, not stored]", await app.ScalarAsync<string>("SELECT headers->>'Authorization' FROM fiscal.flip_message ORDER BY id DESC LIMIT 1"));
        Assert.Equal(HttpStatusCode.OK, (await flip.SendAsync(Folio(token))).StatusCode);

        // The token itself is nowhere in the database.
        Assert.Equal(0L, await app.ScalarAsync<long>($"""
            SELECT (SELECT count(*) FROM fiscal.setting WHERE value::text LIKE '%{token}%')
                 + (SELECT count(*) FROM fiscal.flip_message WHERE headers::text LIKE '%{token}%')
                 + (SELECT count(*) FROM fiscal.audit_log WHERE details::text LIKE '%{token}%')
            """));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM fiscal.audit_log WHERE action = 'FLIP_TOKEN_CREATED'"));

        // A new token replaces the old one at once.
        af = await ServerFixture.AntiforgeryTokenAsync(admin, "/Settings");
        await admin.PostAsync("/Settings?handler=GenerateToken", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = af }));
        await admin.GetStringAsync("/Settings");
        Assert.Equal(HttpStatusCode.Unauthorized, (await flip.SendAsync(Folio("Bearer " + token))).StatusCode);
    }

    [PgFact]
    public void Token_extraction_accepts_common_forms()
    {
        Assert.Equal("abc", Flip.FlipAuth.ExtractToken("Bearer abc"));
        Assert.Equal("abc", Flip.FlipAuth.ExtractToken("bearer  abc "));
        Assert.Equal("abc", Flip.FlipAuth.ExtractToken("Token abc"));
        Assert.Equal("abc", Flip.FlipAuth.ExtractToken("abc"));
        Assert.Null(Flip.FlipAuth.ExtractToken(""));
    }
}
