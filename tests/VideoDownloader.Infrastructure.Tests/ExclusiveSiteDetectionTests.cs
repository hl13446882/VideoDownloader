using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
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
    [Theory]
    [InlineData("bytes 0-4613733/52428800", 52428800L)]
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
        await detector.ProcessNetworkAsync(Evt(page, "https://v3.douyinvod.com/current.mp4") with
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
            "https://v3-web-prime.douyinvod.com/video/tos/cn/obj/progressive?mime_type=video_mp4") with
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
    public async Task Douyin_MediaVideo_Pairs_With_MediaAudio()
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

        Assert.NotNull(last);
        Assert.Equal(MediaTrackKind.Video, last!.Video!.Kind);
        Assert.NotNull(last.Audio);
        Assert.Equal(MediaTrackKind.Audio, last.Audio!.Kind);
        Assert.Contains("media-audio-", last.Audio.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
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
