using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Tests;

public class BilibiliCdnPreferenceTests
{
    [Fact]
    public void Score_Prefers_Domestic_Upos_Over_Akamai()
    {
        var akam = new Uri("https://upos-hz-mirrorakam.akamaized.net/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?os=akam");
        var bos = new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?os=bos");
        var cos = new Uri("https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?os=cos");

        var cosov = new Uri("https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?os=cosovbv");

        Assert.True(BilibiliCdnPreference.IsFragile(akam));
        Assert.True(BilibiliCdnPreference.IsFragile(cosov));
        Assert.False(BilibiliCdnPreference.IsFragile(bos));
        Assert.False(BilibiliCdnPreference.IsFragile(cos));
        Assert.True(BilibiliCdnPreference.Score(bos) > BilibiliCdnPreference.Score(akam));
        Assert.True(BilibiliCdnPreference.Score(bos) > BilibiliCdnPreference.Score(cos));
        Assert.True(BilibiliCdnPreference.Score(cos) > BilibiliCdnPreference.Score(cosov));
        Assert.True(BilibiliCdnPreference.Score(bos) > BilibiliCdnPreference.Score(cosov));
        Assert.Equal("41747222317-1-30080.m4s", BilibiliCdnPreference.ObjectKey(akam));
        Assert.Equal(BilibiliCdnPreference.ObjectKey(akam), BilibiliCdnPreference.ObjectKey(bos));
        Assert.True(BilibiliCdnPreference.CanKeepAlignedResume(akam, 323_944_781, 323_940_056));
        Assert.False(BilibiliCdnPreference.CanKeepAlignedResume(akam, 323_944_781, 200_000_000));
        Assert.False(BilibiliCdnPreference.CanKeepAlignedResume(
            new Uri("https://rr1---sn-npoeen66.googlevideo.com/videoplayback"),
            323_944_781,
            323_940_056));
    }

    [Fact]
    public void SameDashObjects_True_WhenOnlySignatureAndCdnChange()
    {
        var expired = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://upos-hz-mirrorakam.akamaized.net/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?os=akam&deadline=1"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var fresh = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?os=bos&deadline=999"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var otherQn = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/17/23/41747222317/41747222317-1-30064.m4s?os=bos"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        Assert.True(BilibiliCdnPreference.SameDashObjects(expired, fresh));
        Assert.False(BilibiliCdnPreference.SameDashObjects(expired, otherQn));
        Assert.False(BilibiliCdnPreference.SameDashObjects(
            expired,
            MediaVariant.FromCombinedTrack(
                "v1",
                new Uri("https://rr1---sn-npoeen66.googlevideo.com/videoplayback"),
                RequestContext.CreateEmpty(),
                container: "mp4")));
    }

    [Fact]
    public void SameDashObjects_Allows_VideoOnly_Against_VideoPlusAudio_Renewal()
    {
        var ctx = RequestContext.CreateEmpty();
        var videoOnly = MediaVariant.FromTracks(
            "视频",
            null,
            null,
            null,
            "mp4",
            [
                new MediaTrack(
                    "video",
                    MediaTrackKind.Video,
                    new Uri("https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-100026.m4s?os=cosovbv"),
                    null,
                    "mp4",
                    null,
                    445_523_331,
                    ctx)
            ]);
        var renewed = MediaVariant.FromTracks(
            "1080p",
            null,
            1080,
            null,
            "mp4",
            [
                new MediaTrack(
                    "video",
                    MediaTrackKind.Video,
                    new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-100026.m4s?os=bos"),
                    null,
                    "mp4",
                    null,
                    445_523_331,
                    ctx),
                new MediaTrack(
                    "audio",
                    MediaTrackKind.Audio,
                    new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-30280.m4s?os=bos"),
                    null,
                    "m4a",
                    null,
                    4_000_000,
                    ctx)
            ]);

        Assert.True(BilibiliCdnPreference.SameDashObjects(videoOnly, renewed));
        var aligned = BilibiliCdnPreference.AlignDashRenewal(videoOnly, renewed);
        Assert.NotNull(aligned);
        Assert.Single(aligned!.Tracks);
        Assert.Equal(MediaTrackKind.Video, aligned.Tracks[0].Kind);
        Assert.Contains("mirrorbos", aligned.Tracks[0].SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("41791391545-1-100026.m4s", BilibiliCdnPreference.ObjectKey(aligned.Tracks[0].SourceUrl));
    }

    [Fact]
    public async Task Detector_Picks_Bilivideo_Over_Akamai_For_Same_Object()
    {
        var detector = new BilibiliMediaDetector(
            NullLogger<BilibiliMediaDetector>.Instance,
            new BilibiliYtDlpExtractor(
                Options.Create(new AppOptions { ExternalResolvers = new() { Enabled = false } }),
                NullLogger<BilibiliYtDlpExtractor>.Instance),
            Substitute.For<IProbeMethodStats>());
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();

        var page = new Uri("https://www.bilibili.com/video/BV1xx411c7mD");
        detector.BeginSession(page, Guid.NewGuid());

        await detector.ProcessNetworkAsync(
            Evt(page, "https://upos-hz-mirrorakam.akamaized.net/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=akam", 323_940_056, observed: true),
            CancellationToken.None);
        await detector.ProcessNetworkAsync(
            Evt(page, "https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=bos", 323_940_056, observed: false),
            CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(SiteIds.Bilibili, last!.Site);
        Assert.Contains("bilivideo.com", last.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("akamai", last.Video.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detector_Picks_Domestic_Upos_Over_Overseas_Cos()
    {
        var detector = new BilibiliMediaDetector(
            NullLogger<BilibiliMediaDetector>.Instance,
            new BilibiliYtDlpExtractor(
                Options.Create(new AppOptions { ExternalResolvers = new() { Enabled = false } }),
                NullLogger<BilibiliYtDlpExtractor>.Instance),
            Substitute.For<IProbeMethodStats>());
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();

        var page = new Uri("https://www.bilibili.com/video/BV1xx411c7mD");
        detector.BeginSession(page, Guid.NewGuid());

        await detector.ProcessNetworkAsync(
            Evt(page, "https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=cosovbv", 799_648_171, observed: true),
            CancellationToken.None);
        await detector.ProcessNetworkAsync(
            Evt(page, "https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=bos", 799_648_171, observed: false),
            CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Contains("mirrorbos", last!.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cosov", last.Video.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detector_Observation_Playinfo_Picks_Bilivideo_Over_Akamai()
    {
        var detector = new BilibiliMediaDetector(
            NullLogger<BilibiliMediaDetector>.Instance,
            new BilibiliYtDlpExtractor(
                Options.Create(new AppOptions { ExternalResolvers = new() { Enabled = false } }),
                NullLogger<BilibiliYtDlpExtractor>.Instance),
            Substitute.For<IProbeMethodStats>());
        MediaDescriptor? last = null;
        detector.DescriptorsReady += (_, list) => last = list.FirstOrDefault();

        var page = new Uri("https://www.bilibili.com/video/BV1xx411c7mD");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(
            page,
            "标题",
            """{"identity":"content:BV1xx411c7mD","caption":"标题","media":["https://upos-hz-mirrorakam.akamaized.net/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=akam","https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=bos"]}""",
            RequestContext.CreateEmpty(),
            CancellationToken.None);
        await detector.CompleteAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Contains("bilivideo.com", last!.Video!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("akamai", last.Video.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsStaleForAutoRecover_False_ForBilibili_EvenWhenHoursOld()
    {
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "bili",
            TargetPath = Path.Combine(Path.GetTempPath(), "bili.mp4"),
            Variant = MediaVariant.FromCombinedTrack(
                "v1",
                new Uri("https://upos-hz-mirrorakam.akamaized.net/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s?deadline=1"),
                RequestContext.CreateEmpty(),
                container: "mp4"),
            Status = DownloadStatus.Paused,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };

        Assert.False(DownloadEngine.IsStaleForAutoRecover(job));
    }

    [Fact]
    public void IsStaleForAutoRecover_True_ForYouTube_WhenExpireElapsed()
    {
        var expire = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "yt",
            TargetPath = Path.Combine(Path.GetTempPath(), "yt.mp4"),
            Variant = MediaVariant.FromCombinedTrack(
                "v1",
                new Uri($"https://rr1---sn-npoeen66.googlevideo.com/videoplayback?expire={expire}"),
                RequestContext.CreateEmpty(),
                container: "mp4"),
            Status = DownloadStatus.Paused,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Assert.False(DownloadEngine.IsStaleForAutoRecover(job));
    }

    [Fact]
    public void SameDetectedContent_Matches_BilibiliBvJob_ToYtDlpAid()
    {
        var page = new Uri("https://www.bilibili.com/video/BV1toYN6LETy/?spm_id_from=333.1007");
        var previous = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://upos-hz-mirrorakam.akamaized.net/upgcxcode/17/23/41747222317/41747222317-1-30080.m4s"),
            RequestContext.CreateEmpty(),
            container: "mp4") with
        {
            ContentIdentity = "id:BV1toYN6LETy",
            RecoveryPageUrl = page
        };
        var video = new DetectedVideo(
            Guid.NewGuid(),
            SiteIds.Bilibili,
            "11347222317",
            "title",
            page,
            MediaFamily.Dash,
            [previous],
            false);

        Assert.True(MediaAddressRenewal.SameDetectedContent(previous, video, page));
    }

    [Fact]
    public void SameYoutubePlayback_True_WhenItagMatches_EvenIfSessionIdChanges()
    {
        var expired = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr1---sn-npoeen66.googlevideo.com/videoplayback?id=o-old&itag=137&clen=1673422477&expire=1"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var fresh = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr2---sn-npoe7ne7.googlevideo.com/videoplayback?id=o-new&itag=137&clen=1673422477&expire=999"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var otherItag = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr2---sn-npoe7ne7.googlevideo.com/videoplayback?id=o-new&itag=136&clen=900000000&expire=999"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var otherSize = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr2---sn-npoe7ne7.googlevideo.com/videoplayback?id=o-new&itag=137&clen=1000000&expire=999"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        Assert.True(MediaAddressRenewal.SameYoutubePlayback(expired, fresh));
        Assert.False(MediaAddressRenewal.SameYoutubePlayback(expired, otherItag));
        Assert.False(MediaAddressRenewal.SameYoutubePlayback(expired, otherSize));
    }

    [Fact]
    public void AlignDashRenewalBySize_MapsCombined_OntoMatchingAv1Object()
    {
        var previous = MediaVariant.FromCombinedTrack(
            "视频",
            new Uri("https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-100026.m4s?os=cosovbv"),
            RequestContext.CreateEmpty(),
            container: "mp4",
            contentLength: 6722) with // stale tiny length; job total is the anchor
        {
            ContentIdentity = "id:BV1y6Y76yE4Q"
        };

        var next = MediaVariant.FromTracks(
            "1080p",
            null,
            1080,
            null,
            "mp4",
            [
                new MediaTrack(
                    "video",
                    MediaTrackKind.Video,
                    new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-100026.m4s?os=bos"),
                    "av01",
                    "mp4",
                    null,
                    445_523_164,
                    RequestContext.CreateEmpty()),
                new MediaTrack(
                    "audio",
                    MediaTrackKind.Audio,
                    new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-30280.m4s?os=bos"),
                    "mp4a",
                    "m4a",
                    null,
                    51_761_933,
                    RequestContext.CreateEmpty())
            ]);

        Assert.True(BilibiliCdnPreference.SameDashObjects(previous, next));
        var aligned = BilibiliCdnPreference.AlignDashRenewal(previous, next);
        Assert.NotNull(aligned);
        Assert.Single(aligned!.Tracks);
        Assert.Equal(MediaTrackKind.Combined, aligned.Tracks[0].Kind);
        Assert.Contains("100026.m4s", aligned.SourceUrl.AbsolutePath);
        Assert.Contains("mirrorbos", aligned.SourceUrl.Host);
        Assert.Equal(445_523_164, aligned.Tracks[0].ContentLength);
    }

    [Fact]
    public void WithLadder_StoresAllResolutionSiblings()
    {
        var low = MediaVariant.FromCombinedTrack(
            "360p",
            new Uri("https://upos-sz-mirrorbos.bilivideo.com/a/41791391545-1-100022.m4s"),
            RequestContext.CreateEmpty(),
            height: 360,
            contentLength: 64_207_138);
        var mid = MediaVariant.FromCombinedTrack(
            "720p",
            new Uri("https://upos-sz-mirrorbos.bilivideo.com/a/41791391545-1-100024.m4s"),
            RequestContext.CreateEmpty(),
            height: 720,
            contentLength: 181_260_197);
        var hi = MediaVariant.FromCombinedTrack(
            "1080p",
            new Uri("https://upos-sz-mirrorbos.bilivideo.com/a/41791391545-1-100026.m4s"),
            RequestContext.CreateEmpty(),
            height: 1080,
            contentLength: 445_523_164);

        var packed = MediaVariantAlternatives.WithLadder(hi, [low, mid, hi]);
        Assert.Equal(2, packed.Alternatives.Count);
        Assert.Contains(packed.Alternatives, a => a.Height == 360);
        Assert.Contains(packed.Alternatives, a => a.Height == 720);
    }

    [Fact]
    public void ParseJson_KeepsAv1_100026_WhenLargerAvcExists()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bilibili-BV1y6Y76yE4Q.json");
        if (!File.Exists(jsonPath))
            jsonPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "bilibili-BV1y6Y76yE4Q.json"));
        var json = File.ReadAllText(jsonPath);
        var extractor = new BilibiliYtDlpExtractor(
            Options.Create(new AppOptions()),
            NullLogger<BilibiliYtDlpExtractor>.Instance);
        var videos = extractor.ParseJson(
            json,
            new Uri("https://www.bilibili.com/video/BV1y6Y76yE4Q/"),
            RequestContext.CreateEmpty(),
            SiteIds.Bilibili);
        Assert.NotEmpty(videos);
        var keys = videos[0].Variants
            .SelectMany(v => v.Tracks)
            .Select(t => BilibiliCdnPreference.ObjectKey(t.SourceUrl))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("41791391545-1-100026.m4s", keys);
        Assert.Contains("41791391545-1-30080.m4s", keys);

        var previous = MediaVariant.FromCombinedTrack(
            "视频",
            new Uri("https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/45/15/41791391545/41791391545-1-100026.m4s"),
            RequestContext.CreateEmpty(),
            contentLength: 445_523_331) with
        {
            ContentIdentity = "id:BV1y6Y76yE4Q",
            RecoveryPageUrl = new Uri("https://www.bilibili.com/video/BV1y6Y76yE4Q/")
        };
        Assert.Contains(videos[0].Variants, v => BilibiliCdnPreference.SameDashObjects(previous, v));
    }

    private static NormalizedNetworkEvent Evt(Uri page, string mediaUrl, long length, bool observed) =>
        new(
            new Uri(mediaUrl),
            "GET",
            206,
            "video/mp4",
            length,
            observed ? "Media" : "Other",
            null,
            page,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            RequestContext.CreateEmpty(),
            DateTimeOffset.UtcNow,
            NetworkEventSource.Cdp);
}
