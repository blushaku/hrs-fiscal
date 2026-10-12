using System.Net;
using Hrs.Fiscal.Server.Localization;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>Interface in the official languages, Latin script (Doc. 01-06-1967 Art 25.14; UA Art 32.2.9).</summary>
public class LanguageTests(ServerFixture app) : IClassFixture<ServerFixture>
{
    [Fact]
    public void Dictionary_translates_whole_texts_and_sentences_with_data()
    {
        var d = new Dictionary(new Dictionary<string, string>
        {
            ["Receipts"] = "Kuponët",
            ["{0} receipt(s) not yet sent to ATK."] = "{0} kupon(ë) ende të padërguar në ATK.",
            ["{0} · Opera Cloud Fiscal Solution - Kosovo"] = "{0} · Opera Cloud Fiscal Solution - Kosovo",
        });
        Assert.Equal("Kuponët", d.Translate("Receipts"));
        Assert.Equal("3 kupon(ë) ende të padërguar në ATK.", d.Translate("3 receipt(s) not yet sent to ATK."));
        Assert.Equal("Kuponët · Opera Cloud Fiscal Solution - Kosovo", d.Translate("Receipts · Opera Cloud Fiscal Solution - Kosovo"));
        Assert.Null(d.Translate("Hotel Demo SH.P.K."));
    }

    [Fact]
    public void Html_translation_leaves_data_and_form_values_alone()
    {
        var html = """<p class="x">Receipts</p><span class="mono">Receipts</span><input type="text" name="q" value="Receipts" placeholder="Receipts"><button type="submit" value="Receipts">Receipts</button><script>var a = "Receipts";</script>""";
        var result = HtmlTranslator.Translate(html, t => t == "Receipts" ? "Kuponët" : null);
        Assert.Equal("""<p class="x">Kuponët</p><span class="mono">Receipts</span><input type="text" name="q" value="Receipts" placeholder="Kuponët"><button type="submit" value="Kuponët">Kuponët</button><script>var a = "Receipts";</script>""", result);
    }

    [PgFact]
    public async Task Pages_are_shown_in_Albanian_and_Serbian_on_request()
    {
        var client = await app.SignedInAsync("admin", ServerFixture.AdminPassword);
        Assert.Contains("Dashboard", await client.GetStringAsync("/"));

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/lang/sq?returnUrl=/Receipts")).StatusCode);
        var sq = WebUtility.HtmlDecode(await client.GetStringAsync("/Receipts"));
        Assert.Contains("<html lang=\"sq\">", sq);
        Assert.Contains("Kuponët", sq);
        Assert.Contains("Të gjithë kuponët fiskalë ruhen përgjithmonë", sq);

        await client.GetAsync("/lang/sr?returnUrl=/");
        var sr = WebUtility.HtmlDecode(await client.GetStringAsync("/Settings/Mapping"));
        Assert.Contains("Stope PDV-a", sr);
        Assert.Contains("Podešavanja", sr);

        // Only local paths are accepted as the return address.
        var outside = await client.GetAsync("/lang/en?returnUrl=//evil.example");
        Assert.Equal("/", outside.Headers.Location!.ToString());
    }
}
