namespace Mp3PlayerSync.Core;

public sealed record BrandVersion(string Brand, string Version);

/// <summary>
/// User-Agent Client Hints as Microsoft Edge reports them.
/// </summary>
/// <remarks>
/// WebView2 reports the same brands as Edge plus "Microsoft Edge WebView2", which also changes their order.
/// Chromium orders its three brands (a GREASE brand, Chromium, and the browser) by the major version:
/// see GenerateBrandVersionList in components/embedder_support/user_agent_utils.cc.
/// </remarks>
public static class ClientHints
{
    public const string WebView2Brand = "Microsoft Edge WebView2";
    public const string EdgeBrand = "Microsoft Edge";
    public const string ChromiumBrand = "Chromium";

    private static readonly int[][] Orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

    /// <summary>The brands Edge itself would report, given the brands WebView2 reports.</summary>
    public static List<BrandVersion> AsEdge(IReadOnlyList<BrandVersion> webView2Brands)
    {
        var brands = webView2Brands.Where(b => b.Brand != WebView2Brand).ToList();

        var chromium = brands.FirstOrDefault(b => b.Brand == ChromiumBrand);
        var edge = brands.FirstOrDefault(b => b.Brand == EdgeBrand);
        var grease = brands.Where(b => b != chromium && b != edge).ToList();
        if (chromium is null || edge is null || grease.Count != 1 || !int.TryParse(chromium.Version.Split('.')[0], out var major))
            return brands;

        var order = Orders[major % Orders.Length];
        var ordered = new BrandVersion[3];
        ordered[order[0]] = grease[0];
        ordered[order[1]] = chromium;
        ordered[order[2]] = edge;
        return [.. ordered];
    }
}
