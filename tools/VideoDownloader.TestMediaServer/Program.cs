using System.Collections.Concurrent;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var contentRoot = Path.Combine(app.Environment.ContentRootPath, "TestAssets");
Directory.CreateDirectory(contentRoot);

var sampleMp4 = Path.Combine(contentRoot, "public.mp4");
if (!File.Exists(sampleMp4))
    await CreateSampleMp4Async(sampleMp4);

await EnsureHlsAssetsAsync(contentRoot, sampleMp4);

const string hlsMaster = """
#EXTM3U
#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
1080p/playlist.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2"
720p/playlist.m3u8
""";

const string hls1080Playlist = """
#EXTM3U
#EXT-X-TARGETDURATION:6
#EXT-X-VERSION:3
#EXTINF:6.0,
seg0.ts
#EXTINF:6.0,
seg1.ts
#EXTINF:6.0,
seg2.ts
#EXT-X-ENDLIST
""";

const string hls720Playlist = """
#EXTM3U
#EXT-X-TARGETDURATION:6
#EXT-X-VERSION:3
#EXTINF:6.0,
seg0.ts
#EXTINF:6.0,
seg1.ts
#EXT-X-ENDLIST
""";

const string segmentDemoHtml = """
<!DOCTYPE html>
<html><body>
<h1>T07 Segment Noise</h1>
<p>This page loads a media playlist and many TS segments.</p>
<script>
fetch('/hls/1080p/playlist.m3u8');
for (let i = 0; i < 50; i++) {
  fetch('/hls/1080p/seg' + (i % 3) + '.ts');
}
</script>
</body></html>
""";

const string dashCleanMpd = """
<?xml version="1.0" encoding="UTF-8"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
  <Period>
    <AdaptationSet mimeType="video/mp4">
      <Representation id="1080" bandwidth="5000000" width="1920" height="1080" codecs="avc1.640028"/>
    </AdaptationSet>
    <AdaptationSet mimeType="audio/mp4">
      <Representation id="audio" bandwidth="128000" codecs="mp4a.40.2"/>
    </AdaptationSet>
  </Period>
</MPD>
""";

const string dashDrmMpd = """
<?xml version="1.0" encoding="UTF-8"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" type="static">
  <Period>
    <AdaptationSet mimeType="video/mp4">
      <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
      <Representation id="1080" bandwidth="5000000" width="1920" height="1080"/>
    </AdaptationSet>
  </Period>
</MPD>
""";

const string hlsAes128Playlist = """
#EXTM3U
#EXT-X-TARGETDURATION:6
#EXT-X-VERSION:3
#EXT-X-KEY:METHOD=AES-128,URI="/keys/aes128.key"
#EXTINF:6.0,
/hls/1080p/seg0.ts
#EXT-X-ENDLIST
""";

const string hlsWidevinePlaylist = """
#EXTM3U
#EXT-X-TARGETDURATION:6
#EXT-X-VERSION:6
#EXT-X-KEY:METHOD=SAMPLE-AES,KEYFORMAT="com.widevine",URI="data:text/plain;base64,AAAA"
#EXTINF:6.0,
/hls/1080p/seg0.ts
#EXT-X-ENDLIST
""";

const string floodHtml = """
<!DOCTYPE html>
<html><body>
<h1>T17 Event Flood</h1>
<script>
for (let i = 0; i < 3000; i++) {
  fetch('/noise/pixel-' + i + '.txt').catch(() => {});
}
fetch('/hls/master.m3u8');
fetch('/media/public.mp4');
</script>
</body></html>
""";

var etagVersion = 0;
var flakyHits = new ConcurrentDictionary<string, int>();
var once403Seen = new ConcurrentDictionary<string, byte>();

app.Use(async (context, next) =>
{
    context.Response.Headers["Accept-Ranges"] = "bytes";
    await next();
});

app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html><head><title>VideoDownloader Test Media Server</title></head>
    <body>
    <h1>Test Media Server</h1>
    <ul>
      <li><a href="/media/public.mp4">T01 public.mp4</a></li>
      <li><a href="/media/range.mp4">T02 range.mp4 (supports Range)</a></li>
      <li><a href="/media/ignore-range.mp4">T03 ignore-range.mp4 (ignores Range)</a></li>
      <li><a href="/media/referer.mp4">T04 referer.mp4 (requires Referer)</a></li>
      <li><a href="/media/cookie.mp4">T05 cookie.mp4 (requires Cookie)</a></li>
      <li><a href="/hls/master.m3u8">T06 HLS master (1080p/720p)</a></li>
      <li><a href="/hls/segments-demo.html">T07 HLS segment noise test</a></li>
      <li><a href="/dash/manifest.mpd">T08 DASH clean (video+audio)</a></li>
      <li><a href="/dash/manifest-drm.mpd">T09 DASH DRM (ContentProtection)</a></li>
      <li><a href="/media/range416.mp4">T13 range416.mp4 (416 when range beyond size)</a></li>
      <li><a href="/media/etag-change.mp4">T14 etag-change.mp4 (ETag rotates on resume)</a></li>
      <li><a href="/media/403-once.mp4">T10 403-once.mp4 (403 then 200)</a></li>
      <li><a href="/media/flaky.mp4">T11 flaky.mp4 (500 then 200)</a></li>
      <li><a href="/media/huge-declared.mp4">T12 huge-declared.mp4 (disk pressure helper)</a></li>
      <li><a href="/media/cookie-expire.mp4">T16 cookie-expire.mp4 (403 without cookie)</a></li>
      <li><a href="/media/redirect.mp4">T15 redirect.mp4 (302 to final media)</a></li>
      <li><a href="/flood/events.html">T17 event flood page</a></li>
      <li><a href="/media/cookie-a.mp4">T18 cookie-a.mp4 (session=a)</a></li>
      <li><a href="/media/cookie-b.mp4">T18 cookie-b.mp4 (session=b)</a></li>
      <li><a href="/probe/sensitive">T19 sensitive probe (token in response)</a></li>
      <li><a href="/hls/aes128.m3u8">T20 HLS AES-128 (not DRM)</a></li>
      <li><a href="/hls/widevine.m3u8">T20 HLS Widevine (DRM)</a></li>
    </ul>
    <video controls width="640" src="/media/public.mp4"></video>
    </body></html>
    """, "text/html"));

app.MapGet("/media/public.mp4", (HttpContext ctx) => ServeFile(ctx, sampleMp4, requireReferer: false, requireCookie: false, honorRange: true));
app.MapGet("/media/range.mp4", (HttpContext ctx) => ServeFile(ctx, sampleMp4, false, false, true));
app.MapGet("/media/ignore-range.mp4", (HttpContext ctx) => ServeFile(ctx, sampleMp4, false, false, false));
app.MapGet("/media/referer.mp4", (HttpContext ctx) => ServeFile(ctx, sampleMp4, true, false, true));
app.MapGet("/media/cookie.mp4", (HttpContext ctx) => ServeFile(ctx, sampleMp4, false, true, true));

app.MapGet("/hls/master.m3u8", () => Results.Text(hlsMaster, "application/vnd.apple.mpegurl"));
app.MapGet("/hls/1080p/playlist.m3u8", () => Results.Text(hls1080Playlist, "application/vnd.apple.mpegurl"));
app.MapGet("/hls/720p/playlist.m3u8", () => Results.Text(hls720Playlist, "application/vnd.apple.mpegurl"));
app.MapGet("/hls/segments-demo.html", () => Results.Content(segmentDemoHtml, "text/html"));
app.MapGet("/hls/1080p/seg{index:int}.ts", (int index) => Results.File(sampleMp4, "video/mp2t"));
app.MapGet("/hls/720p/seg{index:int}.ts", (int index) => Results.File(sampleMp4, "video/mp2t"));

app.MapGet("/dash/manifest.mpd", () => Results.Text(dashCleanMpd, "application/dash+xml"));
app.MapGet("/dash/manifest-drm.mpd", () => Results.Text(dashDrmMpd, "application/dash+xml"));

app.MapGet("/media/range416.mp4", (HttpContext ctx) => ServeRange416(ctx, sampleMp4));
app.MapGet("/media/etag-change.mp4", (HttpContext ctx) => ServeEtagChange(ctx, sampleMp4, ref etagVersion));
app.MapGet("/media/403-once.mp4", (HttpContext ctx) => Serve403Once(ctx, sampleMp4, once403Seen));
app.MapGet("/media/flaky.mp4", (HttpContext ctx) => ServeFlaky(ctx, sampleMp4, flakyHits));
app.MapGet("/media/huge-declared.mp4", (HttpContext ctx) => ServeHugeDeclared(ctx));
app.MapGet("/media/redirect.mp4", () => Results.Redirect("/media/public.mp4", permanent: false));
app.MapGet("/media/cookie-expire.mp4", (HttpContext ctx) => ServeFile(ctx, sampleMp4, false, true, true));
app.MapGet("/media/cookie-a.mp4", (HttpContext ctx) => ServeCookieSession(ctx, sampleMp4, "session=a"));
app.MapGet("/media/cookie-b.mp4", (HttpContext ctx) => ServeCookieSession(ctx, sampleMp4, "session=b"));
app.MapGet("/flood/events.html", () => Results.Content(floodHtml, "text/html"));
app.MapGet("/noise/pixel-{index:int}.txt", (int index) => Results.Text($"noise-{index}", "text/plain"));
app.MapGet("/hls/aes128.m3u8", () => Results.Text(hlsAes128Playlist, "application/vnd.apple.mpegurl"));
app.MapGet("/hls/widevine.m3u8", () => Results.Text(hlsWidevinePlaylist, "application/vnd.apple.mpegurl"));
app.MapGet("/keys/aes128.key", () => Results.Bytes(new byte[16], "application/octet-stream"));
app.MapGet("/probe/sensitive", () => Results.Json(new
{
    token = "super-secret-test-token-abc123",
    authorization = "Bearer test-auth-value",
    cookie = "session=test; auth=hidden"
}));

var urls = builder.Configuration["urls"] ??
           Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ??
           "http://localhost:5088";

app.Run(urls);

static async Task EnsureHlsAssetsAsync(string contentRoot, string sampleMp4)
{
    Directory.CreateDirectory(Path.Combine(contentRoot, "hls", "1080p"));
    Directory.CreateDirectory(Path.Combine(contentRoot, "hls", "720p"));
    await Task.CompletedTask;
}

static IResult ServeFile(HttpContext ctx, string path, bool requireReferer, bool requireCookie, bool honorRange)
{
    if (requireReferer && !ctx.Request.Headers.ContainsKey("Referer"))
        return Results.StatusCode(403);

    if (requireCookie)
    {
        var cookie = ctx.Request.Headers.Cookie.ToString();
        if (!cookie.Contains("session=test", StringComparison.Ordinal))
            return Results.StatusCode(403);
    }

    if (!File.Exists(path))
        return Results.NotFound();

    var fileInfo = new FileInfo(path);
    var total = fileInfo.Length;

    if (honorRange && ctx.Request.Headers.TryGetValue("Range", out var rangeHeader))
    {
        var range = rangeHeader.ToString();
        if (range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var parts = range["bytes=".Length..].Split('-');
            if (long.TryParse(parts[0], out var start))
            {
                var end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? e : total - 1;
                end = Math.Min(end, total - 1);
                var length = end - start + 1;

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
                ctx.Response.Headers["Content-Length"] = length.ToString();
                ctx.Response.ContentType = "video/mp4";
                return Results.Stream(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                    "video/mp4",
                    enableRangeProcessing: false);
            }
        }
    }

    ctx.Response.Headers["Content-Length"] = total.ToString();
    return Results.File(path, "video/mp4", enableRangeProcessing: honorRange);
}

static async Task CreateSampleMp4Async(string path)
{
    var bytes = new byte[256 * 1024];
    RandomNumberGenerator.Fill(bytes);
    bytes[4] = (byte)'f';
    bytes[5] = (byte)'t';
    bytes[6] = (byte)'y';
    bytes[7] = (byte)'p';
    await File.WriteAllBytesAsync(path, bytes);
}

static IResult ServeRange416(HttpContext ctx, string path)
{
    if (!File.Exists(path))
        return Results.NotFound();

    var total = new FileInfo(path).Length;

    if (ctx.Request.Headers.TryGetValue("Range", out var rangeHeader))
    {
        var range = rangeHeader.ToString();
        if (range.StartsWith("bytes=", StringComparison.Ordinal) &&
            long.TryParse(range["bytes=".Length..].Split('-')[0], out var start) &&
            start >= total)
        {
            ctx.Response.StatusCode = 416;
            ctx.Response.Headers["Content-Range"] = $"bytes */{total}";
            return Results.Empty;
        }
    }

    return ServeFile(ctx, path, false, false, true);
}

static IResult ServeEtagChange(HttpContext ctx, string path, ref int etagVersion)
{
    if (!File.Exists(path))
        return Results.NotFound();

    var total = new FileInfo(path).Length;
    var hasRange = ctx.Request.Headers.TryGetValue("Range", out var rangeHeader) &&
                   rangeHeader.ToString().StartsWith("bytes=", StringComparison.Ordinal);

    if (hasRange)
        etagVersion++;

    var etag = $"\"etag-v{etagVersion}\"";
    ctx.Response.Headers.ETag = etag;

    if (hasRange && long.TryParse(rangeHeader.ToString()["bytes=".Length..].Split('-')[0], out var start))
    {
        var end = total - 1;
        var length = end - start + 1;
        ctx.Response.StatusCode = 206;
        ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
        ctx.Response.Headers["Content-Length"] = length.ToString();
        ctx.Response.ContentType = "video/mp4";

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(start, SeekOrigin.Begin);
        return Results.Stream(stream, "video/mp4", enableRangeProcessing: false);
    }

    ctx.Response.Headers["Content-Length"] = total.ToString();
    return Results.File(path, "video/mp4", enableRangeProcessing: true);
}

static IResult Serve403Once(HttpContext ctx, string path, ConcurrentDictionary<string, byte> seen)
{
    var key = ctx.Connection.Id.ToString();
    if (seen.TryAdd(key, 0))
        return Results.StatusCode(403);

    return ServeFile(ctx, path, false, false, true);
}

static IResult ServeFlaky(HttpContext ctx, string path, ConcurrentDictionary<string, int> hits)
{
    var key = ctx.Connection.Id.ToString();
    var count = hits.AddOrUpdate(key, 1, (_, v) => v + 1);
    if (count <= 2)
        return Results.StatusCode(500);

    return ServeFile(ctx, path, false, false, true);
}

static async Task ServeHugeDeclared(HttpContext ctx)
{
    const long total = 1024L * 1024 * 1024 * 1024;
    ctx.Response.ContentType = "video/mp4";
    ctx.Response.Headers.ContentLength = total;
    ctx.Response.Headers.AcceptRanges = "bytes";

    var buffer = new byte[1024 * 1024];
    RandomNumberGenerator.Fill(buffer);
    while (!ctx.RequestAborted.IsCancellationRequested)
        await ctx.Response.Body.WriteAsync(buffer, ctx.RequestAborted);
}

static IResult ServeCookieSession(HttpContext ctx, string path, string requiredSession)
{
    var cookie = ctx.Request.Headers.Cookie.ToString();
    if (!cookie.Contains(requiredSession, StringComparison.Ordinal))
        return Results.StatusCode(403);

    return ServeFile(ctx, path, false, false, true);
}
