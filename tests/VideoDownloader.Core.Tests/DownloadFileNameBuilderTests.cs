using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;

namespace VideoDownloader.Core.Tests;

public class DownloadFileNameBuilderTests
{
    [Fact]
    public void Build_UsesDetectedCopyAndQuality_Within30Chars()
    {
        var video = CreateVideo(
            SiteIds.YouTube,
            "abc123",
            "Useful Clip",
            new Uri("https://www.youtube.com/watch?v=abc123"),
            new Dictionary<string, string> { ["channel"] = "Creator Name" });

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.DoesNotContain("CreatorName", name);
        Assert.Contains("UsefulClip", name);
        Assert.EndsWith("_1080p", name);
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
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements
                    <= DownloadFileNameBuilder.MaxStemLength);
        Assert.Contains("1080p", name);
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
    public void Build_Generic_FallsBackToCleanTitle()
    {
        var video = CreateVideo(
            SiteIds.Generic,
            null,
            "public.mp4",
            new Uri("http://localhost:5088/"),
            null);

        var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);

        Assert.Equal("public.mp4_1080p", name);
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
