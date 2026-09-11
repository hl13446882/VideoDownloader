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

        Assert.True(BilibiliCdnPreference.IsFragile(akam));
        Assert.False(BilibiliCdnPreference.IsFragile(bos));
        Assert.True(BilibiliCdnPreference.Score(bos) > BilibiliCdnPreference.Score(akam));
        Assert.True(BilibiliCdnPreference.Score(bos) > BilibiliCdnPreference.Score(cos));
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
    public void SameYoutubePlayback_True_WhenIdAndItagMatch()
    {
        var expired = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr1---sn-npoeen66.googlevideo.com/videoplayback?id=o-abc&itag=137&expire=1"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var fresh = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr2---sn-npoe7ne7.googlevideo.com/videoplayback?id=o-abc&itag=137&expire=999"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        var otherItag = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://rr2---sn-npoe7ne7.googlevideo.com/videoplayback?id=o-abc&itag=136&expire=999"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        Assert.True(MediaAddressRenewal.SameYoutubePlayback(expired, fresh));
        Assert.False(MediaAddressRenewal.SameYoutubePlayback(expired, otherItag));
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
