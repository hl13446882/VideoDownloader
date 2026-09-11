using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;

namespace VideoDownloader.Core.Tests;

public class DownloadFileNameBuilderTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("mka")]
    public void Build_UsesCaptionAndMetaSuffix_TitleWithin30(string container)
    {
        foreach (var title in new[] { "短标题", new string('长', 80) })
        {
            var video = CreateVideo(SiteIds.Generic, null, title, new Uri("https://example.com/watch"), null,
                durationSec: 185, contentLength: 256 * 1024 * 1024);
            var variant = video.Variants[0] with { Container = container };
            var name = DownloadFileNameBuilder.Build(video, variant);
            Assert.EndsWith("_3分_1080P_256MB", name);
            Assert.DoesNotContain(container, name, StringComparison.OrdinalIgnoreCase);
            DownloadFileNameBuilder.TrySplitMetaSuffix(name, out var head, out _);
            Assert.True(new System.Globalization.StringInfo(head).LengthInTextElements
                        <= DownloadFileNameBuilder.MaxStemLength);
        }
    }

    [Fact]
    public void Build_UsesDetectedCopyAndMeta_MetaOutside30Budget()
    {
        var video = CreateVideo(
            SiteIds.YouTube,
            "abc123",
            "Useful Clip",
            new Uri("https://www.youtube.com/watch?v=abc123"),
            new Dictionary<string, string> { ["channel"] = "Creator Name" },
            durationSec: 90,
            contentLength: 50L * 1024 * 1024);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("CreatorName", name);
        Assert.DoesNotContain("abc123", name);
        Assert.Contains("UsefulClip", name);
        Assert.Equal("UsefulClip_2分_1080P_50MB", name);
    }

    [Fact]
    public void Build_StripsHashtags_AndHardCapsTitleAt30()
    {
        var video = CreateVideo(
            SiteIds.Douyin,
            "aweme42",
            "山歌追上云朵夫妻版 #舞蹈 #藏族舞 #月月舞蹈夫妇原创 #山歌追上云朵 #零基础学舞蹈",
            new Uri("https://www.douyin.com/video/aweme42"),
            new Dictionary<string, string> { ["author"] = "发布者" },
            durationSec: 60,
            contentLength: 12L * 1024 * 1024);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("#", name);
        Assert.DoesNotContain("发布者", name);
        Assert.Contains("山歌追上云朵夫妻版", name);
        Assert.DoesNotContain("舞蹈", name); // topics still stripped when caption remains
        Assert.EndsWith("_1分_1080P_12MB", name);
        DownloadFileNameBuilder.TrySplitMetaSuffix(name, out var head, out _);
        Assert.True(new System.Globalization.StringInfo(head).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
    }

    [Fact]
    public void Build_HashtagOnlyCaption_UsesTopicTextAsStem()
    {
        var video = CreateVideo(
            SiteIds.Douyin,
            "aweme-topic-only",
            "#舞蹈 #藏族舞 #月月舞蹈夫妇原创",
            new Uri("https://www.douyin.com/video/aweme-topic-only"),
            null,
            durationSec: 30,
            contentLength: 8L * 1024 * 1024);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("#", name);
        Assert.Contains("舞蹈", name);
        Assert.DoesNotContain("douyin.com", name, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("_1分_1080P_8MB", name);
    }

    [Fact]
    public void Build_StripsGluedDetectionMeta_BeforeClamp()
    {
        var video = CreateVideo(
            SiteIds.Douyin,
            "aweme1",
            "蓝天白云下听蒙语鸿雁太治愈了 9.8MB mp4",
            new Uri("https://www.douyin.com/video/1"),
            null,
            durationSec: 120,
            contentLength: 10L * 1024 * 1024);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("9.8MB", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mp4", name, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("_2分_1080P_10MB", name);
    }

    [Fact]
    public void Build_CleansIllegalCharacters_AndElidesMiddle()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "Before:Illegal*Characters/Are?RemovedAndTheTailRemains",
            new Uri("http://localhost:5088/"),
            null,
            durationSec: 600,
            contentLength: 2L * 1024 * 1024 * 1024);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('/', name);
        Assert.Contains("\uFF0A", name);
        Assert.EndsWith("_10分_1080P_2GB", name);
    }

    [Fact]
    public void Build_Generic_FallsBackToHostDate_WhenTitleIsTransportName()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "public.mp4",
            new Uri("https://ally.trytcrae.cc/archives/274269/"),
            null,
            durationSec: 240,
            contentLength: 100L * 1024 * 1024);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
        var day = DateTimeOffset.Now.ToString("yyyyMMdd");

        Assert.Contains(day, name);
        Assert.EndsWith("_4分_1080P_100MB", name);
        Assert.DoesNotContain("archives", name);
        Assert.DoesNotContain("public.mp4", name);
        Assert.DoesNotContain(".mp4", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_Generic_UsesPageTitle_WhenPresent()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "凤凰娇探 - 在线观看",
            new Uri("https://www.xmfyy.com/index.php/vod/play/id/1.html"),
            null,
            durationSec: null,
            contentLength: null,
            height: 720);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
        Assert.Contains("凤凰娇探", name);
        Assert.EndsWith("_720P", name);
        Assert.DoesNotContain("xmfyy", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_OmitsMissingMetaParts()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "仅标题",
            new Uri("https://example.com/a"),
            null,
            durationSec: 45,
            contentLength: null,
            height: null);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
        Assert.Equal("仅标题_1分", name);
    }

    [Fact]
    public void BuildHostDateResolutionFallback_UsesHostWithoutPath()
    {
        var stamp = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.FromHours(8));
        var name = DownloadFileNameBuilder.BuildHostDateResolutionFallback(
            new Uri("https://www.example.com/a/b?x=1"),
            height: 720,
            now: stamp);
        Assert.Equal("example.com_20260908", name);
    }

    [Fact]
    public void WithSequenceSuffix_PreservesMetaOutsideTitleBudget()
    {
        var name = DownloadFileNameBuilder.WithSequenceSuffix("短标题_3分_1080P_256MB", 2);
        Assert.Equal("短标题_2_3分_1080P_256MB", name);
    }

    [Fact]
    public void WithCollisionSuffix_PreservesMeta()
    {
        var name = DownloadFileNameBuilder.WithCollisionSuffix("山歌追上云朵夫妻版_1080P_12MB", "abcd1234");
        Assert.EndsWith("_abcd1234_1080P_12MB", name);
        DownloadFileNameBuilder.TrySplitMetaSuffix(name, out var head, out var meta);
        Assert.Equal("_1080P_12MB", meta);
        Assert.True(new System.Globalization.StringInfo(head).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
    }

    [Fact]
    public void WithSequenceSuffix_IsReadableAndStaysWithin30ForTitle()
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
    public void FinalizeEnqueueStem_DoesNotFoldMetaInto30()
    {
        var longTitle = new string('长', 40) + "_5分_1080P_1.5GB";
        var name = DownloadFileNameBuilder.FinalizeEnqueueStem(longTitle);
        Assert.EndsWith("_5分_1080P_1.5GB", name);
        DownloadFileNameBuilder.TrySplitMetaSuffix(name, out var head, out _);
        Assert.True(new System.Globalization.StringInfo(head).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
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

    [Theory]
    [InlineData(100L * 1024 * 1024, "100MB")]
    [InlineData(1536L * 1024 * 1024, "1.5GB")]
    [InlineData(10L * 1024 * 1024 * 1024, "10GB")]
    public void FormatSizeLabel_UsesMbBelow1Gb(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadFileNameBuilder.FormatSizeLabel(bytes));
    }

    private static DetectedVideo CreateVideo(
        string siteId,
        string? contentId,
        string title,
        Uri pageUrl,
        IReadOnlyDictionary<string, string>? metadata,
        double? durationSec = 180,
        long? contentLength = 100,
        int? height = 1080)
    {
        var variant = MediaVariant.FromCombinedTrack(
            height is > 0 ? $"{height}p" : "default",
            new Uri("https://cdn.example.test/video.mp4"),
            RequestContext.CreateEmpty(),
            height: height,
            container: "mp4",
            contentLength: contentLength);

        return new DetectedVideo(
            Guid.NewGuid(),
            siteId,
            contentId,
            title,
            pageUrl,
            MediaFamily.DirectMp4,
            [variant],
            false,
            Metadata: metadata)
        {
            DurationSec = durationSec
        };
    }
}
