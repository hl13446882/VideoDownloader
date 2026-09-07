using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Sites.ExternalResolvers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoDownloader.Infrastructure.Configuration;

var opts = Options.Create(new AppOptions
{
    ExternalResolvers = { Enabled = true, YtDlpPath = @"D:\VideoDownloader\publish\VideoDownload\tools\yt-dlp.exe" }
});
var r = new YtDlpResolver(opts, NullLogger<YtDlpResolver>.Instance);
var ctx = new RequestContext(
    Guid.NewGuid(),
    1,
    "https://www.bilibili.com/video/BV13s4k63Ew8/",
    "https://www.bilibili.com",
    "Mozilla/5.0",
    new Dictionary<string, string>(),
    Array.Empty<BrowserCookie>(),
    DateTimeOffset.UtcNow);
var videos = await r.ResolveAsync(
    new Uri("https://www.bilibili.com/video/BV13s4k63Ew8/?spm_id_from=333.1007.tianma.2-2-5.click"),
    ctx,
    CancellationToken.None);
Console.WriteLine($"count={videos.Count} err={r.LastError}");
if (videos.Count > 0)
    Console.WriteLine($"title={videos[0].DisplayTitle} variants={videos[0].Variants.Count} url={videos[0].Variants[0].SourceUrl.Host}");
