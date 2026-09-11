using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection.Sites.TikTok;

namespace VideoDownloader.Infrastructure.Tests;

public class TikTokMediaDetectorTests
{
    [Fact]
    public async Task Observation_Prefers_Tiktokcdn_Over_WebappPrime()
    {
        var detector = CreateDetector();
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.tiktok.com/@u/video/7682788705483984135");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(
            page,
            "caption",
            """{"identity":"content:7682788705483984135","media":["https://v16-webapp-prime.tiktok.com/video/tos/alisg/x?signature=dead","https://v16.tiktokcdn.com/obj/play.mp4"]}""",
            RequestContext.CreateEmpty(),
            CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Contains("tiktokcdn", last!.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webapp-prime", last.Video.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(last.Formats, v => v.SourceUrl.Host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unmatched_Network_Preload_Does_Not_Replace_Current_Work()
    {
        var detector = CreateDetector();
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.tiktok.com/@u/video/7682788705483984135");
        var session = Guid.NewGuid();
        detector.BeginSession(page, session);
        await detector.ProcessPageObservationAsync(
            page,
            "caption",
            """{"identity":"content:7682788705483984135","media":["https://v16-webapp-prime.tiktok.com/video/tos/alisg/current?signature=dead"]}""",
            RequestContext.CreateEmpty(),
            CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Contains("webapp-prime", last!.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);

        await detector.ProcessNetworkAsync(
            Evt(page, "https://v16.tiktokcdn.com/obj/preload-next.mp4", session, 8_000_000),
            CancellationToken.None);

        Assert.Contains("webapp-prime", last!.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preload-next", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Network_Matching_Observed_Path_Can_Keep_Current_Tiktokcdn()
    {
        var detector = CreateDetector();
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.tiktok.com/@u/video/7682788705483984135");
        var session = Guid.NewGuid();
        detector.BeginSession(page, session);
        await detector.ProcessPageObservationAsync(
            page,
            "caption",
            """{"identity":"content:7682788705483984135","media":["https://v16.tiktokcdn.com/obj/play.mp4"]}""",
            RequestContext.CreateEmpty(),
            CancellationToken.None);

        await detector.ProcessNetworkAsync(
            Evt(page, "https://v16.tiktokcdn.com/obj/play.mp4?token=1", session, 8_000_000),
            CancellationToken.None);

        Assert.NotNull(last);
        Assert.Contains("tiktokcdn", last!.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/obj/play.mp4", last.Video.SourceUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anonymous_Network_Before_Identity_Does_Not_Emit()
    {
        var detector = CreateDetector();
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.tiktok.com/");
        var session = Guid.NewGuid();
        detector.BeginSession(page, session);
        await detector.ProcessNetworkAsync(
            Evt(page, "https://v16.tiktokcdn.com/obj/preload.mp4", session, 8_000_000),
            CancellationToken.None);
        Assert.Null(last);
    }

    private static NormalizedNetworkEvent Evt(Uri page, string mediaUrl, Guid sessionId, long contentLength) =>
        new(
            new Uri(mediaUrl),
            "GET",
            206,
            "video/mp4",
            contentLength,
            "Media",
            null,
            page,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            RequestContext.CreateEmpty(),
            DateTimeOffset.UtcNow,
            NetworkEventSource.Cdp)
        { SessionId = sessionId };

    [Fact]
    public void ParseJson_Prefers_Tiktokcdn_Combined_Over_Higher_WebappPrime()
    {
        const string json = """
            {
              "id":"7682788705483984135", "title":"clip",
              "formats":[
                {"format_id":"prime","url":"https://v16-webapp-prime.tiktok.com/video/tos/x?signature=dead","ext":"mp4","height":1080,"tbr":4000,"filesize":40000000,"vcodec":"h264","acodec":"aac","protocol":"https"},
                {"format_id":"cdn","url":"https://v16.tiktokcdn.com/obj/play.mp4","ext":"mp4","height":720,"tbr":2000,"filesize":20000000,"vcodec":"h264","acodec":"aac","protocol":"https"}
              ]
            }
            """;
        var extractor = new TikTokYtDlpExtractor(
            Options.Create(new AppOptions()),
            NullLogger<TikTokYtDlpExtractor>.Instance);
        var video = Assert.Single(extractor.ParseJson(
            json,
            new Uri("https://www.tiktok.com/@u/video/7682788705483984135"),
            RequestContext.CreateEmpty(),
            SiteIds.TikTok));
        Assert.Contains("tiktokcdn", video.Variants[0].SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static TikTokMediaDetector CreateDetector() =>
        new(
            NullLogger<TikTokMediaDetector>.Instance,
            new TikTokYtDlpExtractor(
                Options.Create(new AppOptions { ExternalResolvers = new() { Enabled = false } }),
                NullLogger<TikTokYtDlpExtractor>.Instance),
            Substitute.For<IProbeMethodStats>());
}
