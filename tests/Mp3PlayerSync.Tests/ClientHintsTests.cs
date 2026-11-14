using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class ClientHintsTests
{
    [Test]
    public async Task Matches_what_Edge_157_reports()
    {
        // Reported by WebView2 and by Edge 157.0.4341.46 on the same machine
        BrandVersion[] webView2 = [new("Chromium", "157.0.8103.39"), new("Microsoft Edge WebView2", "157.0.4341.46"), new("Not-A.Brand", "99.0.0.0"), new("Microsoft Edge", "157.0.4341.46")];
        BrandVersion[] edge = [new("Not-A.Brand", "99.0.0.0"), new("Microsoft Edge", "157.0.4341.46"), new("Chromium", "157.0.8103.39")];

        await Assert.That(ClientHints.AsEdge(webView2)).IsEquivalentTo(edge, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(156, "Not)A;Brand|Chromium|Microsoft Edge")]
    [Arguments(158, "Chromium|Not)A;Brand|Microsoft Edge")]
    public async Task Orders_brands_by_major_version(int major, string expected)
    {
        await Assert.That(string.Join("|", ClientHints.AsEdge([new("Not)A;Brand", "8"), new("Chromium", $"{major}"), new("Microsoft Edge", $"{major}")]).Select(b => b.Brand))).IsEqualTo(expected);
    }

    [Test]
    public async Task Leaves_unexpected_brands_in_place_without_WebView2()
    {
        BrandVersion[] brands = [new("Chromium", "157"), new("Microsoft Edge WebView2", "157"), new("Something", "1"), new("Else", "2")];

        await Assert.That(ClientHints.AsEdge(brands).Select(b => b.Brand)).IsEquivalentTo(new[] { "Chromium", "Something", "Else" }, CollectionOrdering.Matching);
    }
}
