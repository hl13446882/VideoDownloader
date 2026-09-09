using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;

namespace VideoDownloader.Core.Tests;

public class DownloadFileNameBuilderTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("mka")]
    public void Build_UsesCaptionAndResolution_Only_NoSizeOrContainer(string container)
    {
        foreach (var title in new[] { "短标题", new string('长', 80) })
        {
            var video = CreateVideo(SiteIds.Generic, null, title, new Uri("https://example.com/watch"), null);
            var variant = video.Variants[0] with { Container = container };
            var name = DownloadFileNameBuilder.Build(video, variant);
            Assert.EndsWith("_1080p", name);
            Assert.DoesNotContain("100B", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(container, name, StringComparison.OrdinalIgnoreCase);
            Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements <= DownloadFileNameBuilder.MaxStemLength);
        }
    }

    [Fact]
    public void Build_UsesDetectedCopyAndResolution_Within30Chars()
    {
        var video = CreateVideo(
            SiteIds.YouTube,
            "abc123",
            "Useful Clip",
            new Uri("https://www.youtube.com/watch?v=abc123"),
            new Dictionary<string, string> { ["channel"] = "Creator Name" });

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("CreatorName", name);
        Assert.DoesNotContain("abc123", name);
        Assert.Contains("UsefulClip", name);
        Assert.EndsWith("_1080p", name);
        Assert.DoesNotContain("mp4", name, StringComparison.OrdinalIgnoreCase);
        Assert.True(name.Length <= DownloadFileNameBuilder.MaxStemLength);
    }

    [Fact]
    public void Build_StripsHashtags_AndHardCapsAt30()
    {
        var video = CreateVideo(
            SiteIds.Douyin,
            "aweme42",
            "山歌追上云朵夫妻版 #舞蹈 #藏族舞 #月月舞蹈夫妇原创 #山歌追上云朵 #零基础学舞蹈",
            new Uri("https://www.douyin.com/video/aweme42"),
            new Dictionary<string, string> { ["author"] = "发布者" });

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("#", name);
        Assert.DoesNotContain("发布者", name);
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
        Assert.EndsWith("_1080p", name);
    }

    [Fact]
    public void Build_StripsGluedDetectionMeta_BeforeClamp()
    {
        var video = CreateVideo(
            SiteIds.Douyin,
            "aweme1",
            "蓝天白云下听蒙语鸿雁太治愈了 9.8MB mp4",
            new Uri("https://www.douyin.com/video/1"),
            null);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("9.8MB", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mp4", name, StringComparison.OrdinalIgnoreCase);
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
        Assert.EndsWith("_1080p", name);
    }

    [Fact]
    public void Build_CleansIllegalCharacters_AndElidesMiddle()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "Before:Illegal*Characters/Are?RemovedAndTheTailRemains",
            new Uri("http://localhost:5088/"),
            null);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('/', name);
        Assert.Contains("\uFF0A", name);
        Assert.EndsWith("_1080p", name);
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
    }

    [Fact]
    public void Build_Generic_FallsBackToHostDate_WhenTitleIsTransportName()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "public.mp4",
            new Uri("https://ally.trytcrae.cc/archives/274269/"),
            null);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
        var day = DateTimeOffset.Now.ToString("yyyyMMdd");

        Assert.Contains(day, name);
        Assert.EndsWith("_1080p", name);
        Assert.DoesNotContain("archives", name);
        Assert.DoesNotContain("public.mp4", name);
        Assert.DoesNotContain("mp4", name, StringComparison.OrdinalIgnoreCase);
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
    }

    [Fact]
    public void Build_Generic_UsesPageTitle_WhenPresent()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "凤凰娇探 - 在线观看",
            new Uri("https://www.xmfyy.com/index.php/vod/play/id/1.html"),
            null);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
        Assert.Contains("凤凰娇探", name);
        Assert.EndsWith("_1080p", name);
        Assert.DoesNotContain("xmfyy", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildHostDateResolutionFallback_UsesHostWithoutPath()
    {
        var stamp = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.FromHours(8));
        var name = DownloadFileNameBuilder.BuildHostDateResolutionFallback(
            new Uri("https://www.example.com/a/b?x=1"),
            height: 720,
            now: stamp);
        Assert.Equal("example.com_20260908_720p", name);
    }

    [Fact]
    public void WithCollisionSuffix_StaysWithin30()
    {
        var name = DownloadFileNameBuilder.WithCollisionSuffix("山歌追上云朵夫妻版_1080p", "abcd1234");
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
        Assert.EndsWith("abcd1234", name);
    }

    [Fact]
    public void WithSequenceSuffix_IsReadableAndStaysWithin30()
    {
        var name = DownloadFileNameBuilder.WithSequenceSuffix("A creator title that is deliberately long", 12);
        Assert.EndsWith("_12", name);
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(30)]
    [InlineData(31)]
    public void ClampStem_PreservesUpTo30Characters(int length)
    {
        var input = new string('a', length);
        var name = DownloadFileNameBuilder.ClampStem(input);
        Assert.Equal(Math.Min(length, 30), name.Length);
        if (length <= 30) Assert.Equal(input, name);
        else Assert.Contains("\uFF0A", name);
    }

    [Fact]
    public void FilterLiveInput_StripsIllegalCharacters()
    {
        var filtered = DownloadFileNameBuilder.FilterLiveInput(@"a<>:""/\|?*b");
        Assert.Equal("ab", filtered);
    }

    [Fact]
    public void NormalizeRenameStem_StripsCurrentExtensionAndReservedNames()
    {
        Assert.Equal("clip", DownloadFileNameBuilder.NormalizeRenameStem("clip.mp4", ".mp4"));
        Assert.Equal("CON_file", DownloadFileNameBuilder.NormalizeRenameStem("CON", ".mp4"));
    }

    private static DetectedVideo CreateVideo(
        string siteId,
        string? contentId,
        string title,
        Uri pageUrl,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var variant = MediaVariant.FromCombinedTrack(
            "1080p",
            new Uri("https://cdn.example.test/video.mp4"),
            RequestContext.CreateEmpty(),
            height: 1080,
            container: "mp4",
            contentLength: 100);

        return new DetectedVideo(
            Guid.NewGuid(),
            siteId,
            contentId,
            title,
            pageUrl,
            MediaFamily.DirectMp4,
            [variant],
            false,
            Metadata: metadata);
    }
}
