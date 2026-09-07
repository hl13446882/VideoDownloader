using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Sites;
using VideoDownloader.Infrastructure.Sites.ExternalResolvers;

namespace VideoDownloader.Infrastructure.Tests;

public class BilibiliSiteAdapterTests
{
    private const string SamplePlayInfo = """
        {
          "dash": {
            "video": [
              {
                "id": 80,
                "baseUrl": "https://example.com/video-1080.m4s",
                "codecid": 7,
                "codecs": "avc1.640028",
                "bandwidth": 5000000,
                "width": 1920,
                "height": 1080
              }
            ],
            "audio": [
              {
                "id": 30280,
                "baseUrl": "https://example.com/audio.m4s",
                "codecs": "mp4a.40.2",
                "bandwidth": 128000
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task ProbeAsync_WithPlayInfo_ReturnsVideoAndAudioTracks()
    {
        var adapter = new BilibiliSiteAdapter(Array.Empty<Core.Contracts.IExternalSiteResolver>());
        var scriptJson = JsonSerializer.Serialize(new { playinfo = JsonSerializer.Deserialize<object>(SamplePlayInfo) });
        var context = new SiteProbeContext(
            new Uri("https://www.bilibili.com/video/BV1xx411c7mD"),
            "Test Video",
            scriptJson,
            [],
            [],
            [],
            RequestContext.CreateEmpty());

        var result = await adapter.ProbeAsync(context, CancellationToken.None);

        Assert.Equal(SiteProbeStatus.Success, result.Status);
        Assert.Single(result.Videos);
        Assert.Equal(2, result.Videos[0].Variants[0].Tracks.Count);
    }

    [Fact]
    public void CanHandle_BilibiliHost_ReturnsTrue()
    {
        var adapter = new BilibiliSiteAdapter(Array.Empty<Core.Contracts.IExternalSiteResolver>());
        Assert.True(adapter.CanHandle(new Uri("https://www.bilibili.com/video/BV1")));
    }
}

public class YouTubeSiteAdapterTests
{
    [Fact]
    public async Task ProbeAsync_WithGoogleVideoNetwork_ReturnsVariant()
    {
        var adapter = new YouTubeSiteAdapter(Array.Empty<Core.Contracts.IExternalSiteResolver>());
        var events = new[]
        {
            new NormalizedNetworkEvent(
                new Uri("https://rr3---sn.googlevideo.com/videoplayback?mime=video/mp4&itag=137"),
                "GET",
                200,
                "video/mp4",
                5_000_000,
                "media",
                null,
                new Uri("https://www.youtube.com/watch?v=abc"),
                null,
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                RequestContext.CreateEmpty(),
                DateTimeOffset.UtcNow,
                NetworkEventSource.Cdp),
            new NormalizedNetworkEvent(
                new Uri("https://rr3---sn.googlevideo.com/videoplayback?mime=audio/mp4&itag=140"),
                "GET",
                200,
                "audio/mp4",
                500_000,
                "media",
                null,
                new Uri("https://www.youtube.com/watch?v=abc"),
                null,
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                RequestContext.CreateEmpty(),
                DateTimeOffset.UtcNow,
                NetworkEventSource.Cdp)
        };

        var context = new SiteProbeContext(
            new Uri("https://www.youtube.com/watch?v=abc"),
            "YouTube Test",
            null,
            [],
            [],
            events,
            RequestContext.CreateEmpty());

        var result = await adapter.ProbeAsync(context, CancellationToken.None);
        Assert.Equal(SiteProbeStatus.Success, result.Status);
        Assert.NotEmpty(result.Videos[0].Variants);
    }
}

public class DownloadBackendRouterTests
{
    [Fact]
    public void Resolve_MultiTrack_UsesFfmpegMultiInput()
    {
        var ctx = RequestContext.CreateEmpty();
        var variant = MediaVariant.FromTracks(
            "1080p",
            1920,
            1080,
            null,
            "mp4",
            [
                new MediaTrack("v", MediaTrackKind.Video, new Uri("http://a/v"), null, "mp4", null, null, ctx),
                new MediaTrack("a", MediaTrackKind.Audio, new Uri("http://a/a"), null, "m4a", null, null, ctx)
            ]);

        var router = new Infrastructure.Download.DownloadBackendRouter();
        Assert.Equal(Core.Contracts.DownloadBackendKind.FfmpegMultiInput, router.Resolve(variant));
    }
}

public class YtDlpResolverTests
{
    [Fact]
    public void ParseJson_WithSplitYouTubeFormats_ReturnsDownloadableMultiTrackVariant()
    {
        const string json = """
            {
              "id": "abc",
              "title": "Split Video",
              "uploader": "Test Channel",
              "formats": [
                {
                  "format_id": "270",
                  "url": "https://manifest.example.test/playlist/index.m3u8",
                  "ext": "mp4",
                  "protocol": "m3u8_native",
                  "vcodec": "avc1.64002a",
                  "acodec": "none",
                  "height": 2160,
                  "tbr": 2400,
                  "filesize": 200000000
                },
                {
                  "format_id": "399",
                  "url": "https://video.example.test/v.mp4",
                  "ext": "mp4",
                  "vcodec": "av01.0.08M.08",
                  "acodec": "none",
                  "height": 1080,
                  "tbr": 1200,
                  "filesize": 72000000,
                  "http_headers": {
                    "User-Agent": "yt-test",
                    "Referer": "https://www.youtube.com/"
                  }
                },
                {
                  "format_id": "140",
                  "url": "https://video.example.test/a.m4a",
                  "ext": "m4a",
                  "vcodec": "none",
                  "acodec": "mp4a.40.2",
                  "abr": 128,
                  "filesize": 6000000,
                  "http_headers": {
                    "User-Agent": "yt-test",
                    "Referer": "https://www.youtube.com/"
                  }
                }
              ]
            }
            """;

        var resolver = new YtDlpResolver(
            Options.Create(new AppOptions()),
            NullLogger<YtDlpResolver>.Instance);

        var videos = InvokeParseJson(
            resolver,
            json,
            new Uri("https://www.youtube.com/watch?v=abc"),
            RequestContext.CreateEmpty(),
            SiteIds.YouTube);

        var video = Assert.Single(videos);
        var variant = Assert.Single(video.Variants, v => v.Height == 1080);

        Assert.Equal(2, variant.Tracks.Count);
        Assert.Contains(video.Variants, v => v.Height == 2160);
        Assert.Contains(video.Variants, v => v.Tracks.All(t => t.Kind == MediaTrackKind.Audio));
        Assert.Equal("Test Channel", video.Metadata?["uploader"]);
        Assert.Equal(78_000_000, variant.TotalContentLength);
        Assert.Equal(new Uri("https://video.example.test/v.mp4"), variant.Tracks[0].SourceUrl);
        Assert.Equal("yt-test", variant.Tracks[0].RequestContext.UserAgent);
        Assert.Equal(DownloadBackendKind.FfmpegMultiInput, new Infrastructure.Download.DownloadBackendRouter().Resolve(variant));
    }

    private static IReadOnlyList<DetectedVideo> InvokeParseJson(
        YtDlpResolver resolver,
        string json,
        Uri pageUrl,
        RequestContext context,
        string siteId)
    {
        var method = typeof(YtDlpResolver).GetMethod(
            "ParseJson",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<DetectedVideo>>(
            method.Invoke(resolver, [json, pageUrl, context, siteId]));
    }
}
