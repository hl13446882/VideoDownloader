using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class MediaUrlNormalizerTests
{
    [Fact]
    public void Normalize_StripsVolatileQuery_KeepsStableParts()
    {
        var a = new Uri("https://cdn.example.com/v/abc/media?mime=video/mp4&range=0-1000&rn=1&id=42");
        var b = new Uri("https://cdn.example.com/v/abc/media?id=42&mime=video/mp4&range=2000-3000&rn=9");
        Assert.Equal(MediaUrlNormalizer.Normalize(a), MediaUrlNormalizer.Normalize(b));
        Assert.True(MediaUrlNormalizer.IsSameMedia(a, b));
    }

    [Fact]
    public void Normalize_DifferentPaths_AreDifferentSessions()
    {
        var a = new Uri("https://cdn.example.com/v/aaa/play");
        var b = new Uri("https://cdn.example.com/v/bbb/play");
        Assert.False(MediaUrlNormalizer.IsSameMedia(a, b));
    }

    [Fact]
    public void SessionKey_IgnoresQuery_SamePathIsSameSession()
    {
        var a = new Uri("https://cdn.example.com/videoplayback?id=1&range=0-1&itag=137");
        var b = new Uri("https://cdn.example.com/videoplayback?id=2&itag=140&range=0-9");
        Assert.True(MediaUrlNormalizer.IsSameSession(a, b));
    }

    [Fact]
    public void SessionKey_DifferentPaths_AreDifferentSessions()
    {
        var a = new Uri("https://cdn.example.com/v/aaa/index.m3u8");
        var b = new Uri("https://cdn.example.com/v/bbb/index.m3u8");
        Assert.False(MediaUrlNormalizer.IsSameSession(a, b));
    }

    [Fact]
    public void Normalize_KeepsItag_SoAudioAndVideoStayDistinct()
    {
        var video = new Uri("https://cdn.example.com/videoplayback?id=1&itag=137&range=0-1");
        var audio = new Uri("https://cdn.example.com/videoplayback?id=1&itag=140&range=0-1");
        Assert.False(MediaUrlNormalizer.IsSameMedia(video, audio));
        Assert.True(MediaUrlNormalizer.IsSameSession(video, audio));
    }

    [Fact]
    public void IsLikelySegment_DoesNotTreatDashBaseUrlAsSegment()
    {
        Assert.False(MediaUrlNormalizer.IsLikelySegment(
            new Uri("https://cdn.example.com/upos/encode/item-1080.m4s")));
        Assert.True(MediaUrlNormalizer.IsLikelySegment(
            new Uri("https://cdn.example.com/dash/segment/12.m4s")));
        Assert.True(MediaUrlNormalizer.IsLikelySegment(
            new Uri("https://cdn.example.com/x/init-000.m4s")));
    }
}
