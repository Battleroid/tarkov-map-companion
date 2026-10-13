using System.Net;
using System.Text;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.WebSync;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The M2 imagery endpoints: <c>GET /map/{mapId}.svg</c> and
/// <c>GET /tiles/{mapId}/{z}/{x}/{y}.png</c>, behind the M1 token gate.
/// </summary>
/// <remarks>
/// Every host binds port 0, and the asset cache is rooted at a fresh temp directory with the network
/// off, so the whole suite is headless and offline. Cached files are seeded at
/// <see cref="AssetCache.PathFor"/> so the endpoints read exactly what the test wrote.
/// </remarks>
public sealed class WebSyncMapEndpointTests
{
    private const string Token = "ABCD2345";

    /// <summary>Shaped like the real maps: shared style/defs, the base group, and two floor groups.</summary>
    private const string Svg =
        """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
          <style id="style_common">.a{fill:#000}</style>
          <defs id="defs1"></defs>
          <g id="Ground_Level"><rect id="ground-rect" width="10" height="10"/></g>
          <g id="Second_Floor"><rect id="second-rect"/></g>
          <g id="Third_Floor"><rect id="third-rect"/></g>
        </svg>
        """;

    // ---- SVG: happy path ------------------------------------------------------------------------

    [Fact]
    public async Task SvgHappyPathReturnsFilteredSvgForTheRequestedFloor()
    {
        using var harness = new Harness();
        harness.SeedSvg("customs");
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(
            harness.Url($"/map/customs.svg?floors={Uri.EscapeDataString("2nd Floor")}&key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // The requested floor is present, an excluded one is gone, and the base survived.
        Assert.Contains("second-rect", body, StringComparison.Ordinal);
        Assert.DoesNotContain("third-rect", body, StringComparison.Ordinal);
        Assert.Contains("ground-rect", body, StringComparison.Ordinal);

        // Shared definitions must never be dropped or the map renders as black shapes.
        Assert.Contains("style_common", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseFalseDropsTheGroundLayer()
    {
        using var harness = new Harness();
        harness.SeedSvg("customs");
        harness.Start();

        using var http = new HttpClient();
        var body = await http.GetStringAsync(
            harness.Url($"/map/customs.svg?floors={Uri.EscapeDataString("2nd Floor")}&base=false&key={Token}"));

        Assert.Contains("second-rect", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ground-rect", body, StringComparison.Ordinal);
    }

    // ---- SVG: Name vs SvgLayerId (the regression the spec calls out) ------------------------------

    [Fact]
    public async Task FloorNamesAreMappedToSvgLayerIdsRatherThanPassedThrough()
    {
        using var harness = new Harness();
        harness.SeedSvg("customs");
        harness.Start();

        using var http = new HttpClient();

        // The floor NAME "2nd Floor" selects the group whose id is that floor's SvgLayerId.
        var byName = await http.GetStringAsync(
            harness.Url($"/map/customs.svg?floors={Uri.EscapeDataString("2nd Floor")}&key={Token}"));
        Assert.Contains("second-rect", byName, StringComparison.Ordinal);

        // Passing the layer id as if it were a name selects nothing: Customs has no floor named
        // "Second_Floor". A pass-through implementation would have kept it, so this is the regression.
        var byLayerId = await http.GetStringAsync(
            harness.Url($"/map/customs.svg?floors=Second_Floor&key={Token}"));
        Assert.DoesNotContain("second-rect", byLayerId, StringComparison.Ordinal);

        // A bogus name changes nothing either, and the base is still there.
        var byBogus = await http.GetStringAsync(
            harness.Url($"/map/customs.svg?floors=NotAFloor&key={Token}"));
        Assert.DoesNotContain("second-rect", byBogus, StringComparison.Ordinal);
        Assert.Contains("ground-rect", byBogus, StringComparison.Ordinal);
    }

    // ---- SVG: 404 and 503 branches ---------------------------------------------------------------

    [Fact]
    public async Task TileOnlyMapHasNoSvg()
    {
        using var harness = new Harness();
        harness.Start();

        // Icebreaker ships raster imagery only, so its SvgUrl is null.
        var map = harness.Map("icebreaker");
        Assert.False(map.HasSvg);

        using var http = new HttpClient();
        var response = await http.GetAsync(harness.Url($"/map/icebreaker.svg?key={Token}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownMapIdIsNotFound()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(harness.Url($"/map/does-not-exist.svg?key={Token}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TraversalShapedMapIdIsNotFound()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();

        // Encoded separators keep this a single path segment, so it actually reaches the route and
        // exercises the whitelist rather than being normalised away by the client.
        var encoded = await http.GetAsync(
            harness.Url($"/map/{Uri.EscapeDataString("../../etc")}.svg?key={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, encoded.StatusCode);

        // The plain form is normalised to a non-existent path by the client; either way, not served.
        var plain = await http.GetAsync(harness.Url($"/map/../../etc.svg?key={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
    }

    [Fact]
    public async Task UncachedAndOfflineIsServiceUnavailable()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();

        // HasSvg is true, but nothing is seeded and the cache has networking off.
        var response = await http.GetAsync(harness.Url($"/map/customs.svg?key={Token}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(Localizer.Loc("WebSync.ImageryUnavailable"), body);
    }

    // ---- Tiles -----------------------------------------------------------------------------------

    [Fact]
    public async Task TileInRangeIsServedFromTheCache()
    {
        using var harness = new Harness();
        var map = harness.Map("customs");

        // Customs declares zoom 2..6, so 2/0/0 is a valid cell.
        var url = Expand(TileMapSource.ResolveTemplate(map, [], includeBase: true)!, 2, 0, 0);
        byte[] tile = [0x89, 0x50, 0x4E, 0x47, 0x11, 0x22, 0x33];
        harness.Seed(url, Path.Combine("tiles", map.NormalizedName), tile);
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(harness.Url($"/tiles/customs/2/0/0.png?key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(tile, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WebpTilesAreServedAsWebp()
    {
        using var harness = new Harness();
        var map = harness.Map("customs");
        var url = Expand(TileMapSource.ResolveTemplate(map, [], includeBase: true)!, 2, 0, 0);

        // RIFF....WEBP magic bytes, which the endpoint probes for.
        var webp = "RIFF????WEBPVP8 "u8.ToArray();
        harness.Seed(url, Path.Combine("tiles", map.NormalizedName), webp);
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(harness.Url($"/tiles/customs/2/0/0.png?key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ZoomOutOfRangeIsNotFound()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();

        // Customs max zoom is 6.
        var response = await http.GetAsync(harness.Url($"/tiles/customs/7/0/0.png?key={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // x must be below 2^z; at z=2 the valid range is 0..3.
        var outOfGrid = await http.GetAsync(harness.Url($"/tiles/customs/2/4/0.png?key={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, outOfGrid.StatusCode);
    }

    [Fact]
    public async Task NonWhitelistedAndSvgOnlyMapsAreNotFound()
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();

        var unknown = await http.GetAsync(harness.Url($"/tiles/does-not-exist/2/0/0.png?key={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Lighthouse is SVG-only, so it has no pyramid to proxy.
        var map = harness.Map("lighthouse");
        Assert.False(map.HasTiles);

        var svgOnly = await http.GetAsync(harness.Url($"/tiles/lighthouse/2/0/0.png?key={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, svgOnly.StatusCode);
    }

    [Fact]
    public async Task TileLoadFailureServesAOnePixelPng()
    {
        using var harness = new Harness();
        var map = harness.Map("customs");
        var url = Expand(TileMapSource.ResolveTemplate(map, [], includeBase: true)!, 2, 0, 0);

        // A tile in the bare "tiles" category, which is NOT where the proxy reads from. Seeding it must
        // not be found, so this both documents the map-named category and guards against a regression.
        harness.Seed(url, "tiles", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xDE, 0xAD, 0xBE, 0xEF });
        harness.Start();

        using var http = new HttpClient();

        // Valid coordinates on a valid pyramid, but nothing cached in tiles/<map> and networking off.
        var response = await http.GetAsync(harness.Url($"/tiles/customs/2/0/0.png?key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((1, 1), PngSize(bytes));
    }

    // ---- Tiles: template parity with the desktop renderer ----------------------------------------

    [Fact]
    public void TileTemplateChoiceMatchesTheRenderer()
    {
        var map = MapCatalog.LoadEmbedded().Find("customs")!;
        var second = map.Floors.Single(f => f.Name == "2nd Floor");

        Assert.Equal(map.BaseTilePathTemplate, TileMapSource.ResolveTemplate(map, [], includeBase: true));
        Assert.Equal(second.TilePathTemplate, TileMapSource.ResolveTemplate(map, ["2nd Floor"], includeBase: true));

        // Raster floors swap: selecting one replaces the base even when the base is requested.
        Assert.Equal(second.TilePathTemplate, TileMapSource.ResolveTemplate(map, ["2nd Floor"], includeBase: false));

        // Base hidden and no floor selected is genuinely nothing to draw.
        Assert.Null(TileMapSource.ResolveTemplate(map, [], includeBase: false));
    }

    [Fact]
    public async Task TileEndpointUsesTheSameTemplateChoiceAsTheRenderer()
    {
        using var harness = new Harness();
        var map = harness.Map("customs");

        var baseTemplate = TileMapSource.ResolveTemplate(map, [], includeBase: true)!;
        var floorTemplate = TileMapSource.ResolveTemplate(map, ["2nd Floor"], includeBase: true)!;
        Assert.NotEqual(baseTemplate, floorTemplate);

        var baseUrl = Expand(baseTemplate, 2, 1, 1);
        var floorUrl = Expand(floorTemplate, 2, 1, 1);
        harness.Seed(baseUrl, Path.Combine("tiles", map.NormalizedName), new byte[] { 0x00, 0x01, 0x02 });
        harness.Seed(floorUrl, Path.Combine("tiles", map.NormalizedName), new byte[] { 0x0A, 0x0B, 0x0C });
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(
            harness.Url($"/tiles/customs/2/1/1.png?floor={Uri.EscapeDataString("2nd Floor")}&key={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, await response.Content.ReadAsByteArrayAsync());
    }

    // ---- Token gate and end-to-end ---------------------------------------------------------------

    [Theory]
    [InlineData("/map/customs.svg")]
    [InlineData("/tiles/customs/2/0/0.png")]
    public async Task ImageryRoutesRequireTheKey(string path)
    {
        using var harness = new Harness();
        harness.Start();

        using var http = new HttpClient();
        var response = await http.GetAsync(harness.Url(path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StateAndSvgBothServeEndToEnd()
    {
        using var harness = new Harness();
        harness.SeedSvg("customs");
        harness.Start();

        using var http = new HttpClient();

        var state = await http.GetAsync(harness.Url($"/api/state?key={Token}"));
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal("application/json", state.Content.Headers.ContentType?.MediaType);

        var svg = await http.GetAsync(harness.Url($"/map/customs.svg?key={Token}"));
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static string Expand(string template, int z, int x, int y) =>
        template.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());

    /// <summary>Reads the width and height out of a PNG's IHDR, after checking its signature.</summary>
    private static (int Width, int Height) PngSize(byte[] bytes)
    {
        Assert.True(bytes.Length >= 24, "not a PNG: too short");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

        return (width, height);
    }

    /// <summary>
    /// A real session, broadcaster and server, wired the way <c>MapSession</c> wires them, but with an
    /// <see cref="AssetCache"/> rooted at a temp directory so seed files land where the endpoints read.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), "tmc-websync-maps-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            Settings = new AppSettings
            {
                AllowNetwork = false,
                CurrentMap = "customs",
                CullMode = CullMode.Off,
                ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-websync-noshots"),
                WebSyncEnabled = false,
                WebSyncAccessToken = Token,
            };

            Catalog = MapCatalog.LoadEmbedded();
            Assets = new AssetCache { Root = _root, AllowNetwork = false };

            Session = new MapSession(Settings, Catalog);
            Broadcaster = new StateBroadcaster(Session, Settings);
            Server = new WebSyncServer(Broadcaster, Settings, Assets, Catalog, _ => { });
        }

        public AppSettings Settings { get; }

        public MapCatalog Catalog { get; }

        public AssetCache Assets { get; }

        public MapSession Session { get; }

        public StateBroadcaster Broadcaster { get; }

        public WebSyncServer Server { get; }

        public GameMap Map(string key) => Catalog.Maps.Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

        public void Seed(string url, string category, byte[] bytes) =>
            File.WriteAllBytes(Assets.PathFor(url, category), bytes);

        public void SeedSvg(string mapKey) =>
            Seed(Map(mapKey).SvgUrl!, "svg", Encoding.UTF8.GetBytes(Svg));

        /// <summary>Enables the server on an OS-assigned port and waits until it is listening.</summary>
        public void Start()
        {
            Server.Update(enabled: true, port: 0, Token);

            Assert.True(
                Server.WaitUntilStarted(TimeSpan.FromSeconds(5)),
                "the WebSync server did not start within 5s");
        }

        public string Url(string path) => $"http://127.0.0.1:{Server.BoundPort}{path}";

        public void Dispose()
        {
            Broadcaster.Dispose();
            Server.Dispose();
            Session.Dispose();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
