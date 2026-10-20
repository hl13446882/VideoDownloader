using Microsoft.Extensions.Logging.Abstractions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class ProbeMethodRankingTests
{
    private static ProbeMethodStatsStore Store() => new(NullLogger.Instance,
        Path.Combine(Path.GetTempPath(), "vd-probe-speed-" + Guid.NewGuid().ToString("N"), "stats.json"));

    [Fact]
    public void LastWin_DoesNotOverrideHigherHistoricalRate()
    {
        var stats = Store();
        for (var i = 0; i < 10; i++) stats.Record(SiteIds.YouTube, ProbeMethods.YtDlpDefault, true);
        for (var i = 0; i < 9; i++) stats.Record(SiteIds.YouTube, ProbeMethods.YtDlpWebEmbedded, false);
        stats.Record(SiteIds.YouTube, ProbeMethods.YtDlpWebEmbedded, true);
        Assert.Equal(ProbeMethods.YtDlpWebEmbedded, stats.LastWinningMethod(SiteIds.YouTube));
        Assert.Equal(ProbeMethods.YtDlpDefault, stats.OrderedMethods(SiteIds.YouTube)[0]);
        Assert.Equal(0.1, stats.SuccessRate(SiteIds.YouTube, ProbeMethods.YtDlpWebEmbedded), 5);
    }

    [Fact]
    public void UntriedClient_CanBeExploredAfterRepeatedSlowFailures()
    {
        var stats = Store();
        for (var i = 0; i < 30; i++)
            stats.RecordTimed(SiteIds.YouTube, ProbeMethods.YtDlpDefault, false, TimeSpan.FromSeconds(15));
        Assert.Equal(ProbeMethods.YtDlpPotMweb, stats.OrderedMethods(SiteIds.YouTube)[0]);
    }

    [Fact]
    public void HighFailureCount_LowersPriorityDespiteMoreLifetimeSuccesses()
    {
        var stats = Store();
        for (var i = 0; i < 30; i++) stats.RecordTimed(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, true, TimeSpan.FromSeconds(1));
        for (var i = 0; i < 70; i++) stats.RecordTimed(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, false, TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++) stats.RecordTimed(SiteIds.Bilibili, ProbeMethods.YtDlp, true, TimeSpan.FromSeconds(1));
        Assert.Equal(ProbeMethods.YtDlp, stats.OrderedMethods(SiteIds.Bilibili)[0]);
    }

    [Fact]
    public void HigherSuccessRate_OutranksFasterMethod()
    {
        var stats = Store();
        for (var i = 0; i < 20; i++)
        {
            stats.RecordTimed(SiteIds.YouTube, ProbeMethods.YtDlpDefault, true, TimeSpan.FromSeconds(15));
            stats.RecordTimed(SiteIds.YouTube, ProbeMethods.YtDlpWebEmbedded, i % 5 != 0, TimeSpan.FromSeconds(2));
        }
        Assert.Equal(ProbeMethods.YtDlpDefault, stats.OrderedMethods(SiteIds.YouTube)[0]);
    }

    [Fact]
    public void LifetimeSuccessRate_IsNotOverriddenByRecentWins()
    {
        var stats = Store();
        for (var i = 0; i < 100; i++) stats.Record(SiteIds.Bilibili, ProbeMethods.YtDlp, false);
        for (var i = 0; i < 30; i++) stats.RecordTimed(SiteIds.Bilibili, ProbeMethods.YtDlp, true, TimeSpan.FromSeconds(1));
        stats.RecordTimed(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, true, TimeSpan.FromSeconds(5));
        Assert.Equal(ProbeMethods.NetworkPlayurl, stats.OrderedMethods(SiteIds.Bilibili)[0]);
    }

    [Fact]
    public void GenericDomains_DoNotShareSchedulingHistory()
    {
        var stats = Store();
        var first = ProbeMethodRanking.Scope(SiteIds.Generic, new Uri("https://a.test/watch"));
        var second = ProbeMethodRanking.Scope(SiteIds.Generic, new Uri("https://b.test/watch"));
        stats.RecordTimed(first, ProbeMethods.DomMorphology, true, TimeSpan.FromMilliseconds(200));
        Assert.Equal(ProbeMethods.DomMorphology, stats.OrderedMethods(first)[0]);
        Assert.Equal(ProbeMethods.YtDlp, stats.OrderedMethods(second)[0]);
        Assert.Equal(SiteIds.YouTube, ProbeMethodRanking.Scope(SiteIds.YouTube, new Uri("https://youtu.be/id")));
    }

    [Fact]
    public void MethodNotice_ReportsItsOwnSuccessRate_NotItsShareOfAllSuccesses()
    {
        var stats = Store();
        stats.Record(SiteIds.Bilibili, ProbeMethods.YtDlp, true);
        stats.Record(SiteIds.Bilibili, ProbeMethods.YtDlp, false);
        for (var i = 0; i < 20; i++) stats.Record(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, true);
        ProbeMethodNotice? notice = null;
        stats.MethodStarted += (_, value) => notice = value;
        stats.ReportMethod(SiteIds.Bilibili, ProbeMethods.YtDlp);
        Assert.Equal(50, notice!.SuccessPercent);
    }

    [Fact]
    public void ColdStart_KeepsCatalogOrder_AndSkipsUnimplementedPlayurl()
    {
        var bilibili = ProbeMethodRanking.ColdStart(SiteIds.Bilibili);
        Assert.Equal([ProbeMethods.YtDlp, ProbeMethods.NetworkPlayurl], bilibili);
        Assert.DoesNotContain(ProbeMethods.PlayurlApi, bilibili);
        Assert.DoesNotContain(ProbeMethods.Album, ProbeMethodRanking.ColdStart(SiteIds.Douyin));
        Assert.Equal(ProbeMethods.NetworkMedia, ProbeMethodRanking.ColdStart(SiteIds.YouTube)[^1]);
    }

    [Fact]
    public async Task SuccessCount_ReordersWithinOneDetector_AndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "vd-probe-rank-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stats = new ProbeMethodStatsStore(NullLogger.Instance, path);
            stats.Record(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, true);
            stats.Record(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, true);
            stats.Record(SiteIds.Bilibili, ProbeMethods.YtDlp, true);
            stats.Record(SiteIds.YouTube, ProbeMethods.YtDlpDefault, true);

            var ordered = stats.OrderedMethods(SiteIds.Bilibili);
            Assert.Equal(ProbeMethods.YtDlp, ordered[0]);
            Assert.Equal(ProbeMethods.NetworkPlayurl, ordered[1]);
            var youtube = stats.OrderedMethods(SiteIds.YouTube);
            Assert.Equal(ProbeMethods.YtDlpDefault, youtube[0]);
            Assert.Contains(ProbeMethods.NetworkMedia, youtube);
            Assert.True(stats.PreferYtdlpFirst(SiteIds.YouTube));
            Assert.True(stats.PreferYtdlpFirst(SiteIds.Bilibili));

            await Task.Delay(800);
            var reloaded = new ProbeMethodStatsStore(NullLogger.Instance, path);
            Assert.Equal(67, stats.MethodSharePercent(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl));
            Assert.Equal(33, stats.MethodSharePercent(SiteIds.Bilibili, ProbeMethods.YtDlp));
            Assert.Equal(0, stats.MethodSharePercent(SiteIds.Douyin, ProbeMethods.AwemeDetail));
            Assert.Equal(2, reloaded.Successes(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl));
            Assert.Equal(1, reloaded.Successes(SiteIds.YouTube, ProbeMethods.YtDlpDefault));
            Assert.Equal(0, reloaded.Successes(SiteIds.Bilibili, ProbeMethods.YtDlpDefault));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
