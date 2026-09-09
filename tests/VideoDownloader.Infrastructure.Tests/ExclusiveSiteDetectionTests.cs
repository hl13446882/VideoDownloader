using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;
using VideoDownloader.Infrastructure.Detection.Sites.Douyin;
using VideoDownloader.Infrastructure.Detection.Sites.TikTok;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public class ExclusiveSiteDetectionTests
{
    [Fact]
    public async Task Douyin_RejectsNextAdFromNetworkAndObservation_AndKeepsAllFormatsOwned()
    {
        const string id = "7674888187625458982";
        const string ad = "7670164200798342410";
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=" + id);
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        detector.BeginSession(page, Guid.NewGuid());
        MediaDescriptor? result = null;
        detector.DescriptorsReady += (_, rows) => result = rows.Single();
        var adUrl = "https://v3.douyinvod.com/video/tos/cn/ad.mp4?__vid=" + ad;
        var current = "https://v3.douyinvod.com/video/tos/cn/current.mp4";
        await detector.ProcessNetworkAsync(Evt(page, adUrl) with { ContentLength = 90_000_000 }, CancellationToken.None);
        await detector.ProcessPageObservationAsync(page, "current",
            System.Text.Json.JsonSerializer.Serialize(new { identity = "content:" + id, media = new[] { adUrl, current } }),
            RequestContext.CreateEmpty(), CancellationToken.None);
        // A larger anonymous preload must not inherit the active work's identity.
        await detector.ProcessNetworkAsync(Evt(page, "https://v3.douyinvod.com/video/tos/cn/unknown-ad.mp4") with { ContentLength = 100_000_000 }, CancellationToken.None);
        // Alternate CDN of the verified object can enrich its length without crossing work boundaries.
        await detector.ProcessNetworkAsync(Evt(page, "https://v9.douyinvod.com/video/tos/cn/current.mp4") with { ContentLength = 8_000_000 }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(id, result.MediaId);
        Assert.Contains("current.mp4", result.Video!.SourceUrl.AbsoluteUri);
        Assert.Equal(8_000_000, result.Video.ContentLength);
        Assert.All(result.Formats, f => Assert.Contains("current.mp4", f.SourceUrl.AbsoluteUri));
    }

    [Fact]
    public async Task Douyin_Second_Unbound_Progressive_Does_Not_Bind_To_Current()
    {
        const string id = "7672351780155575579";
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=" + id);
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        detector.BeginSession(page, Guid.NewGuid());
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, rows) => last = rows.Single();

        await detector.ProcessPageObservationAsync(page, "当前",
            """{"identity":"content:7672351780155575579","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/tos-cn-ve-15/o03RIkiiwcCJp8VCAQBAEwQ9eO1?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 8_000_000
        }, CancellationToken.None);
        // Next-feed preload without __vid must not inherit the active work (黄龄错下).
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/tos-cn-ve-15/ogln3iF7a7TavwYBVEimAWsiFqi?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 72_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Contains("o03RIkiiwcCJp8VCAQBAEwQ9eO1", last!.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ogln3iF7", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.Single(last.Formats);
    }

    [Fact]
    public async Task Douyin_Parked_Other_Work_Progressive_Adopted_On_Switch()
    {
        const string current = "7674888187625458982";
        const string next = "7522534938898468147";
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=" + current);
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        detector.BeginSession(page, Guid.NewGuid());
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, rows) => last = rows.Single();

        await detector.ProcessPageObservationAsync(page, "当前",
            """{"identity":"content:7674888187625458982","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        // Next-work progressive is preloaded while current is active — must be parked, not lost.
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/obj/next.mp4?mime_type=video_mp4&__vid=" + next) with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 12_000_000
        }, CancellationToken.None);

        detector.Clear();
        var nextPage = new Uri("https://www.douyin.com/jingxuan?modal_id=" + next);
        detector.BeginSession(nextPage, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(nextPage, "下一条",
            """{"identity":"content:7522534938898468147","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(next, last!.MediaId);
        Assert.Contains("next.mp4", last.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.False(detector.Failed);
    }

    [Fact]
    public async Task Douyin_Prefers_Observation_Id_Over_Stamped_ModalId()
    {
        const string stamped = "7674888187625458982";
        const string playing = "7522534938898468147";
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=" + stamped);
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, rows) => last = rows.Single();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "正在播",
            "{\"identity\":\"content:" + playing + "\",\"album\":false,\"media\":[],\"durationSec\":40.5}",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/obj/play.mp4?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 8_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(playing, last!.MediaId);
        Assert.Contains("play.mp4", last.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Douyin_Rejects_Unbound_Progressive_That_Mismatches_Observed_Duration()
    {
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=7680898097882840454");
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, rows) => last = rows.FirstOrDefault();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "短片",
            """{"identity":"content:7680898097882840454","album":false,"media":[],"durationSec":40}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/hash/video/tos/cn/tos-cn-ve-15/huge.mp4?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 400_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.Null(last);
        Assert.True(detector.Failed);
    }

    [Fact]
    public async Task Douyin_Accepts_Unbound_Progressive_Matching_Observed_Duration()
    {
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=7680898097882840454");
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, rows) => last = rows.Single();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "作品",
            """{"identity":"content:7680898097882840454","album":false,"media":[],"durationSec":40}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/hash/video/tos/cn/tos-cn-ve-15/ok.mp4?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 35_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Contains("ok.mp4", last!.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Douyin_HardClear_Wipes_Parked_Progressive()
    {
        const string current = "7674888187625458982";
        const string next = "7522534938898468147";
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=" + current);
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        detector.BeginSession(page, Guid.NewGuid());
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, rows) => last = rows.Single();

        await detector.ProcessPageObservationAsync(page, "当前",
            """{"identity":"content:7674888187625458982","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/obj/next.mp4?mime_type=video_mp4&__vid=" + next) with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 12_000_000
        }, CancellationToken.None);

        detector.HardClear();
        var nextPage = new Uri("https://www.douyin.com/jingxuan?modal_id=" + next);
        detector.BeginSession(nextPage, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(nextPage, "下一条",
            """{"identity":"content:7522534938898468147","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.Null(last);
        Assert.True(detector.Failed);
    }

    [Fact]
    public async Task Douyin_RejectsMismatchedPlayerObservation_AndUnboundNetwork()
    {
        var page = new Uri("https://www.douyin.com/video/7674888187625458982");
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        detector.BeginSession(page, Guid.NewGuid());
        var emitted = false;
        detector.DescriptorsReady += (_, _) => emitted = true;
        await detector.ProcessNetworkAsync(Evt(page, "https://v3.douyinvod.com/unknown.mp4"), CancellationToken.None);
        await detector.ProcessPageObservationAsync(page, "ad",
            """{"identity":"content:7670164200798342410","media":["https://v3.douyinvod.com/ad.mp4"]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.False(emitted);
        Assert.True(detector.Failed);
    }

    [Fact]
    public async Task Douyin_LateIdentityMustNotAdoptUnidentifiedPreload()
    {
        var page = new Uri("https://www.douyin.com/jingxuan");
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        detector.BeginSession(page, Guid.NewGuid());
        var emitted = false;
        detector.DescriptorsReady += (_, _) => emitted = true;
        await detector.ProcessNetworkAsync(Evt(page, "https://v3.douyinvod.com/preload.mp4"), CancellationToken.None);
        await detector.ProcessPageObservationAsync(page, "current",
            """{"identity":"content:7674888187625458982","media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.False(emitted);
    }

    [Theory]
    [InlineData("bytes 0-52428799/52428800", 52428800L)]
    [InlineData("bytes 0-4613733/52428800", null)]
    [InlineData("bytes 0-65535/52428800", null)]
    [InlineData("bytes 0-4613733/*", null)]
    [InlineData(null, null)]
    public async Task Douyin_Partial_Response_Does_Not_Label_Chunk_As_Full_Size(string? range, long? expected)
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/video/7682715203741568283");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "当前作品", """{"media":[],"album":false}""", RequestContext.CreateEmpty(), CancellationToken.None);
        MediaDescriptor? result = null;
        detector.DescriptorsReady += (_, rows) => result = rows.Single();
        await detector.ProcessNetworkAsync(Evt(page, "https://v3.douyinvod.com/current.mp4?__vid=7682715203741568283") with
        {
            StatusCode = 206, ContentLength = 4613734,
            ResponseHeaders = range is null ? new Dictionary<string,string>() : new Dictionary<string,string> { ["content-range"] = range }
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Video!.ContentLength);
        Assert.DoesNotContain("4.4MB", MediaDescriptorMapper.ToDetectedVideo(result, Guid.NewGuid()).DisplayTitle);
    }

    [Theory]
    [InlineData("https://imapi.douyin.com/v1/stranger/get_conversation_list", "application/x-protobuf")]
    [InlineData("https://www.douyin.com/aweme/v1/web/feed/", "application/json")]
    [InlineData("https://v3.douyinvod.com/invalid.mp4", "text/html")]
    public async Task Douyin_Rejects_NonMedia_Response_Even_With_Large_Length(string url, string mime)
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/video/7682715203741568283");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "当前作品", """{"media":[],"album":false}""", RequestContext.CreateEmpty(), CancellationToken.None);
        var emitted = false;
        detector.DescriptorsReady += (_, _) => emitted = true;
        await detector.ProcessNetworkAsync(Evt(page,url) with { MimeType=mime, ContentLength=4613734 }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.False(emitted);
        Assert.True(detector.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completing_Old_Work_Does_Not_Seal_New_Session(bool switchWhileWaitingForLease)
    {
        var detector = Substitute.For<IExclusiveSiteMediaDetector>();
        detector.Matches(Arg.Any<Uri>()).Returns(true);
        detector.Site.Returns(SiteKind.YouTube);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        detector.CompleteAsync(Arg.Any<CancellationToken>()).Returns(completion.Task);
        var unified = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));
        var routed = new RoutedMediaDetectionPipeline(unified, new SiteDetectionRouter(),
            new ExclusiveSiteMediaDetectorResolver([detector]), NullLogger<RoutedMediaDetectionPipeline>.Instance);
        await routed.ProbePageAsync(new Uri("https://www.youtube.com/watch?v=first"), null, null,
            RequestContext.CreateEmpty(), CancellationToken.None);
        using var oldLease = routed.BeginDiscovery(routed.SessionId);
        if (switchWhileWaitingForLease) completion.SetResult();
        var finishing = routed.CompleteDiscoveryAsync(CancellationToken.None);
        var oldSession = routed.SessionId;
        await routed.ProbePageAsync(new Uri("https://www.youtube.com/watch?v=second"), null, null,
            RequestContext.CreateEmpty(), CancellationToken.None);
        if (!switchWhileWaitingForLease) completion.SetResult();
        await finishing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(oldSession, routed.SessionId);
        Assert.False(routed.IsCompleted);
        using var newLease = routed.BeginDiscovery(routed.SessionId);
        Assert.NotNull(newLease);
    }

    private static RoutedMediaDetectionPipeline CreateRouted(out UnifiedMediaPipeline unified)
    {
        unified = new UnifiedMediaPipeline(
            Substitute.For<IRequestMessageFactory>(),
            [],
            Options.Create(new AppOptions()));
        var detectors = new IExclusiveSiteMediaDetector[]
        {
            new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance),
            new TikTokMediaDetector(NullLogger<TikTokMediaDetector>.Instance),
            new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance),
            new BilibiliMediaDetector(NullLogger<BilibiliMediaDetector>.Instance)
        };
        return new RoutedMediaDetectionPipeline(
            unified,
            new SiteDetectionRouter(),
            new ExclusiveSiteMediaDetectorResolver(detectors),
            NullLogger<RoutedMediaDetectionPipeline>.Instance);
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/1234567890123456789", SiteKind.Douyin)]
    [InlineData("https://www.tiktok.com/@u/video/1234567890123456789", SiteKind.TikTok)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", SiteKind.YouTube)]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", SiteKind.Bilibili)]
    [InlineData("https://example.com/play/1", SiteKind.Other)]
    public void Router_Resolves_Exclusive_And_Other(string url, SiteKind expected)
    {
        Assert.Equal(expected, new SiteDetectionRouter().Resolve(new Uri(url)));
    }

    [Fact]
    public void Detectors_Are_Mutually_Exclusive()
    {
        var pages = new[]
        {
            new Uri("https://www.douyin.com/video/1"),
            new Uri("https://www.tiktok.com/@a/video/1"),
            new Uri("https://www.youtube.com/watch?v=abc"),
            new Uri("https://www.bilibili.com/video/BV1xx411c7mD")
        };
        var detectors = new IExclusiveSiteMediaDetector[]
        {
            new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance),
            new TikTokMediaDetector(NullLogger<TikTokMediaDetector>.Instance),
            new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance),
            new BilibiliMediaDetector(NullLogger<BilibiliMediaDetector>.Instance)
        };
        foreach (var page in pages)
            Assert.Equal(1, detectors.Count(d => d.Matches(page)));
    }

    [Fact]
    public void Unified_Rejects_Exclusive_Hosts()
    {
        Assert.True(UnifiedMediaPipeline.IsExclusiveSiteHost(new Uri("https://www.douyin.com/video/1")));
        Assert.True(UnifiedMediaPipeline.IsExclusiveSiteHost(new Uri("https://www.tiktok.com/@a/video/1")));
        Assert.False(UnifiedMediaPipeline.IsExclusiveSiteHost(new Uri("https://cdn.example.com/a.m3u8")));
    }

    [Fact]
    public async Task Douyin_Case1_Video_Emits_Descriptor_No_Generic()
    {
        var routed = CreateRouted(out var unified);
        DetectedVideo? detected = null;
        routed.VideoDetected += (_, v) => detected = v;
        var page = new Uri("https://www.douyin.com/video/7682715203741568283");
        routed.Clear();

        await routed.ProbePageAsync(page, "作品标题",
            """{"identity":"www.douyin.com:content:7682715203741568283","caption":"作品标题","media":["https://v3.douyinvod.com/video/tos/obj/?mime_type=video_mp4"],"album":false}""",
            RequestContext.CreateEmpty(), CancellationToken.None);

        await routed.ProcessAsync(Evt(page, "https://v3.douyinvod.com/video/tos/obj/play?mime_type=video_mp4", routed.SessionId), CancellationToken.None);
        await routed.CompleteDiscoveryAsync(CancellationToken.None);

        Assert.NotNull(detected);
        Assert.Equal(SiteIds.Douyin, detected!.SiteId);
        Assert.Empty(unified.LastProbeDecisions);
    }

    [Fact]
    public async Task Douyin_Case2_Scroll_Switches_Content()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page1 = new Uri("https://www.douyin.com/video/1111111111111111111");
        var page2 = new Uri("https://www.douyin.com/video/2222222222222222222");
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();

        detector.BeginSession(page1, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page1, "A",
            """{"identity":"content:1111111111111111111","media":["https://v3.douyinvod.com/a.mp4"],"album":false}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page1, "https://v3.douyinvod.com/a.mp4"), CancellationToken.None);

        await detector.ProcessPageObservationAsync(page2, "B",
            """{"identity":"content:2222222222222222222","media":["https://v3.douyinvod.com/b.mp4"],"album":false}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page2, "https://v3.douyinvod.com/b.mp4"), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal("2222222222222222222", last!.MediaId);
        Assert.Contains("b.mp4", last.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Douyin_Case3_Video_To_Album_Clears_Video()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/note/7682715203741568283");
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        detector.BeginSession(page, Guid.NewGuid());

        await detector.ProcessPageObservationAsync(page, "v",
            """{"identity":"content:7682715203741568283","media":["https://v3.douyinvod.com/v.mp4"],"album":false}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessPageObservationAsync(page, "album",
            """{"identity":"content:7682715203741568283","album":true,"images":["https://p3.douyinpic.com/tos-cn-i-xxx/img1.jpeg","https://p3.douyinpic.com/tos-cn-i-xxx/img2.jpeg"],"media":["https://sf3.douyinstatic.com/obj/ies-music/bgm.mp3"]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(MediaContentType.Album, last!.ContentType);
        Assert.Equal(2, last.Images.Count);
        Assert.Null(last.Video);
        Assert.NotNull(last.Audio);
    }

    [Fact]
    public async Task Douyin_Case4_Album_Order_And_Dedup()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/note/7682715203741568283");
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "album",
            """{"album":true,"images":["https://p3.douyinpic.com/a.jpeg?x=1","https://p3.douyinpic.com/b.jpeg","https://p3.douyinpic.com/a.jpeg?x=2"],"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.Equal(2, last!.Images.Count);
        Assert.Equal(0, last.Images[0].Index);
        Assert.Contains("a.jpeg", last.Images[0].Url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b.jpeg", last.Images[1].Url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Douyin_Case5_Excludes_Avatar_Logo()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/note/7682715203741568283");
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "album",
            """{"album":true,"images":["https://p3.douyinpic.com/avatar/user.jpeg","https://p3.douyinpic.com/logo/badge.png","https://p3.douyinpic.com/tos-cn-i-xxx/real1.jpeg","https://p3.douyinpic.com/tos-cn-i-xxx/real2.jpeg"],"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.Equal(2, last!.Images.Count);
        Assert.All(last.Images, i => Assert.DoesNotContain("avatar", i.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Douyin_Case6_Album_With_Bgm()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/note/7682715203741568283");
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "album",
            """{"album":true,"images":["https://p3.douyinpic.com/a.jpeg","https://p3.douyinpic.com/b.jpeg"],"media":["https://sf3.douyinstatic.com/obj/ies-music/bgm.mp3"]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last!.Audio);
        Assert.Equal(MediaContentType.Album, last.ContentType);
    }

    [Fact]
    public async Task Douyin_Case7_Album_Without_Bgm_Still_Succeeds()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        var page = new Uri("https://www.douyin.com/note/7682715203741568283");
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "album",
            """{"album":true,"images":["https://p3.douyinpic.com/a.jpeg","https://p3.douyinpic.com/b.jpeg"],"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.Null(last!.Audio);
        Assert.Equal(2, last.Images.Count);
    }

    [Fact]
    public async Task Douyin_Case8_Failure_Has_No_Generic_Fallback()
    {
        var routed = CreateRouted(out var unified);
        var probed = false;
        DetectedVideo? detected = null;
        routed.VideoDetected += (_, v) => detected = v;
        routed.PageProbed += (_, _) => probed = true;
        routed.Clear();
        var page = new Uri("https://www.douyin.com/video/7682715203741568283");
        await routed.ProbePageAsync(page, null, """{"identity":"content:7682715203741568283","media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await routed.CompleteDiscoveryAsync(CancellationToken.None);

        Assert.Null(detected);
        Assert.True(probed);
        Assert.Empty(unified.LastProbeDecisions);
    }

    [Fact]
    public async Task Douyin_Prefers_Muxed_Progressive_Over_Silent_MediaVideo_And_Gateway()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/?recommend=1");
        detector.BeginSession(page, Guid.NewGuid());

        // Late identity bind must not wipe the progressive candidate already captured.
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-web-prime.douyinvod.com/video/tos/cn/obj/progressive?mime_type=video_mp4&__vid=7680538459441859882") with
        {
            ResourceType = "Media", StatusCode = 206, ContentLength = 8_000_000,
            ResponseHeaders = new Dictionary<string, string> { ["content-range"] = "bytes 0-65535/8000000" }
        }, CancellationToken.None);

        await detector.ProcessPageObservationAsync(page, "作品",
            """{"identity":"content:7680538459441859882","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);

        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-web-prime.douyinvod.com/video/tos/cn/tos-cn-vd-0026/x/media-video-avc1/?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 206, ContentLength = 20_000_000,
            ResponseHeaders = new Dictionary<string, string> { ["content-range"] = "bytes 0-65535/20000000" }
        }, CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://www.douyin.com/aweme/v1/play/?video_id=v0200&is_play_url=1") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 50_000_000
        }, CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://pull-flv-l13.douyincdn.com/stage/stream-1.flv") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 300_000_000
        }, CancellationToken.None);

        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(MediaTrackKind.Combined, last!.Video!.Kind);
        Assert.Contains("progressive", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/aweme/v1/play", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".flv", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("media-video-", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Douyin_Rejects_Silent_MediaVideo_When_No_Progressive()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/video/7682715203741568283");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "v",
            """{"identity":"content:7682715203741568283","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3.douyinvod.com/video/tos/cn/x/media-video-hvc1/?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 50_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.Null(last);
        Assert.True(detector.Failed);
        Assert.Equal("douyin_mse_only", detector.FailureReason);
    }

    [Fact]
    public async Task Douyin_MediaVideo_With_Audio_Does_Not_Become_Ordinary_Download()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/video/7682715203741568283");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "v",
            """{"identity":"content:7682715203741568283","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3.douyinvod.com/video/tos/cn/x/media-video-avc1/?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 12_000_000
        }, CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3.douyinvod.com/video/tos/cn/x/media-audio-mp4a/?mime_type=audio_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 1_200_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.Null(last);
        Assert.True(detector.Failed);
        Assert.Equal("douyin_mse_only", detector.FailureReason);
    }

    [Fact]
    public async Task Douyin_Larger_MediaVideo_Does_Not_Beat_Smaller_Progressive()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=7680538459441859882");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "t",
            """{"identity":"content:7680538459441859882","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/obj/progressive.mp4?mime_type=video_mp4&__vid=7680538459441859882") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 35_000_000
        }, CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/x/media-video-hvc1/?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 206, ContentLength = 1_500_000,
            ResponseHeaders = new Dictionary<string, string>
            {
                ["content-range"] = "bytes 0-1499999/332000000"
            }
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Contains("progressive", last!.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("media-video-", last.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        var mapped = MediaDescriptorMapper.ToDetectedVideo(last, Guid.NewGuid());
        Assert.DoesNotContain(mapped.Variants, v =>
            v.Tracks.Any(t => MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl)));
        Assert.Equal(mapped.Variants[0].SourceUrl.AbsoluteUri, last.Video.SourceUrl.AbsoluteUri);
    }

    [Fact]
    public void Douyin_Incomplete_ContentRange_On_Mse_Is_Not_Entity_Length()
    {
        var e = Evt(new Uri("https://www.douyin.com/video/1"),
            "https://v3.douyinvod.com/video/tos/cn/x/media-video-hvc1/?mime_type=video_mp4") with
        {
            StatusCode = 206,
            ContentLength = 1_500_000,
            ResponseHeaders = new Dictionary<string, string>
            {
                ["content-range"] = "bytes 0-1499999/332000000"
            }
        };
        Assert.Null(DouyinPlayEvidence.GetEntityLength(e));
        Assert.True(MediaUrlNormalizer.IsByteDanceMseTrack(e.Url));
    }

    [Fact]
    public async Task Douyin_Album_Ignores_Stray_MediaVideo_Network()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/note/7276638706021240125");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "相册文案",
            """{"identity":"content:7276638706021240125","album":true,"caption":"相册文案","media":[],"images":["https://p3-pc-sign.douyinpic.com/tos-cn-i-0813c001/img1.jpeg","https://p3-pc-sign.douyinpic.com/tos-cn-i-0813c001/img2.jpeg"]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3.douyinvod.com/video/tos/cn/x/media-video-avc1/?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 206, ContentLength = 80_000
        }, CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://lf3-static.bytednsdoc.com/obj/ies-music/bgm.mp3") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 500_000, MimeType = "audio/mpeg"
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(MediaContentType.Album, last!.ContentType);
        Assert.Equal(2, last.Images.Count);
        Assert.Null(last.Video);
        var mapped = MediaDescriptorMapper.ToDetectedVideo(last, Guid.NewGuid());
        Assert.Equal("album", mapped.Variants[0].Container);
    }

    [Fact]
    public void MediaVariantRanking_Prefers_Progressive_Over_Larger_Mse()
    {
        var progressive = MediaVariant.FromCombinedTrack(
            "p", new Uri("https://v3.douyinvod.com/video/tos/progressive.mp4"),
            RequestContext.CreateEmpty(), contentLength: 35_000_000);
        var mse = MediaVariant.FromTracks(
            "m", null, null, null, "mp4",
            [
                new MediaTrack("v", MediaTrackKind.Video,
                    new Uri("https://v3.douyinvod.com/x/media-video-hvc1/"), null, "mp4", null, 332_000_000,
                    RequestContext.CreateEmpty())
                {
                    IsMseTrack = true
                }
            ]);
        var preferred = MediaVariantRanking.SelectPreferredVideo([mse, progressive]);
        Assert.NotNull(preferred);
        Assert.Equal(progressive.SourceUrl.AbsoluteUri, preferred!.SourceUrl.AbsoluteUri);
    }

    [Fact]
    public void DownloadEngine_Rejects_Mse_Track_Variant()
    {
        var variant = MediaVariant.FromTracks(
            "mse", null, null, null, "mkv",
            [
                new MediaTrack("v", MediaTrackKind.Video,
                    new Uri("https://v3.douyinvod.com/x/media-video-avc1/"), null, "mp4", null, 12_000_000,
                    RequestContext.CreateEmpty()) { IsMseTrack = true },
                new MediaTrack("a", MediaTrackKind.Audio,
                    new Uri("https://v3.douyinvod.com/x/media-audio-mp4a/"), null, "m4a", null, 1_000_000,
                    RequestContext.CreateEmpty()) { IsMseTrack = true }
            ]);
        var ex = Assert.Throws<DownloadException>(() =>
        {
            // Mirror the engine gate without spinning DI.
            if (variant.Tracks.Any(t => t.IsMseTrack || MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl)))
                throw new DownloadException(ErrorCodes.MseTrackNotDownloadable, "mse");
        });
        Assert.Equal(ErrorCodes.MseTrackNotDownloadable, ex.ErrorCode);
    }

    [Fact]
    public async Task Douyin_Unbound_Progressive_Belongs_To_Current_Work()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/jingxuan?modal_id=7680898097882840454");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "作品",
            """{"identity":"content:7680898097882840454","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        // CDN progressive URLs typically omit aweme id query — must still be selectable.
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/hash/video/tos/cn/tos-cn-ve-15/obj123?mime_type=video_mp4") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 35_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(MediaTrackKind.Combined, last!.Video!.Kind);
        Assert.Contains("zjcdn", last.Video.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.False(detector.Failed);
    }

    [Fact]
    public async Task Douyin_Rejects_Known_Tiny_Progressive_Shell()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/?recommend=1");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "空气都是香的",
            """{"identity":"content:7680819823879075706","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-web-prime.douyinvod.com/video/tos/cn/obj/tiny.mp4?mime_type=video_mp4&br=456&qs=12") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 204_801
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.Null(last);
        Assert.True(detector.Failed);
        Assert.Equal("douyin_no_media", detector.FailureReason);
    }

    [Fact]
    public async Task Douyin_Failed_Complete_Does_Not_Block_Later_Progressive()
    {
        var detector = new DouyinMediaDetector(NullLogger<DouyinMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.douyin.com/?recommend=1");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "t",
            """{"identity":"content:7680538459441859882","album":false,"media":[]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.True(detector.Failed);

        await detector.ProcessNetworkAsync(Evt(page,
            "https://v3-dy-o.zjcdn.com/video/tos/cn/obj/full.mp4?mime_type=video_mp4&__vid=7680538459441859882") with
        {
            ResourceType = "Media", StatusCode = 200, ContentLength = 8_000_000
        }, CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.False(detector.Failed);
        Assert.Contains("full.mp4", last!.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TikTok_Exclusive_Positive_Path()
    {
        var detector = new TikTokMediaDetector(NullLogger<TikTokMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.tiktok.com/@u/video/1234567890123456789");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessNetworkAsync(Evt(page, "https://v16.tiktokcdn.com/playAddr/obj.mp4"), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(SiteIds.TikTok, last!.Site);
        Assert.Equal(MediaContentType.Video, last.ContentType);
    }

    [Fact]
    public async Task YouTube_Exclusive_Positive_Path()
    {
        var detector = new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessNetworkAsync(Evt(page,
            "https://rr1---sn-abc.googlevideo.com/videoplayback?mime=video%2Fmp4&itag=137"), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(SiteIds.YouTube, last!.Site);
    }

    [Fact]
    public async Task Bilibili_Exclusive_Positive_Path()
    {
        var detector = new BilibiliMediaDetector(NullLogger<BilibiliMediaDetector>.Instance);
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();
        var page = new Uri("https://www.bilibili.com/video/BV1xx411c7mD");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "标题",
            """{"identity":"content:BV1xx411c7mD","caption":"标题","media":["https://upos-sz-mirrorcos.bilivideo.com/upos/v.m4s"]}""",
            RequestContext.CreateEmpty(), CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(SiteIds.Bilibili, last!.Site);
    }

    [Fact]
    public void MediaDescriptorMapper_Album_Uses_Slideshow_Container()
    {
        var descriptor = new MediaDescriptor(
            SiteIds.Douyin,
            new Uri("https://www.douyin.com/note/1"),
            "1",
            MediaContentType.Album,
            null,
            new MediaTrack("bgm", MediaTrackKind.Audio, new Uri("https://cdn.example/a.mp3"), null, "m4a", null, null, RequestContext.CreateEmpty()),
            [
                new AlbumImageItem(0, new Uri("https://cdn.example/1.jpg"), null, null, "jpeg", RequestContext.CreateEmpty()),
                new AlbumImageItem(1, new Uri("https://cdn.example/2.jpg"), null, null, "jpeg", RequestContext.CreateEmpty())
            ],
            RequestContext.CreateEmpty(),
            0.9,
            "相册");
        var video = MediaDescriptorMapper.ToDetectedVideo(descriptor, Guid.NewGuid());
        Assert.Equal("album", video.Variants[0].Container);
        Assert.Equal(2, video.Variants[0].Tracks.Count(t => t.Kind == MediaTrackKind.Image));
    }

    private static NormalizedNetworkEvent Evt(Uri page, string mediaUrl, Guid? sessionId = null) =>
        new(
            new Uri(mediaUrl),
            "GET",
            200,
            "video/mp4",
            2_000_000,
            "Media",
            null,
            page,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            RequestContext.CreateEmpty(),
            DateTimeOffset.UtcNow,
            NetworkEventSource.Cdp)
        {
            SessionId = sessionId ?? Guid.Empty
        };
}
