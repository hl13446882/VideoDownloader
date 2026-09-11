using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

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
