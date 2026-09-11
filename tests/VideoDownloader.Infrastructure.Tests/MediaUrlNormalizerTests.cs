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
    public void IsLikelySegment_RecognizesAbrPrefixedTsSlices()
    {
        Assert.True(MediaUrlNormalizer.IsLikelySegment(
            new Uri("https://cdn.example.com/20250311/eQ/1000k_00000.ts")));
        Assert.True(MediaUrlNormalizer.IsLikelySegment(
            new Uri("https://cdn.example.com/hls/seg-12.ts")));
        Assert.False(MediaUrlNormalizer.IsLikelySegment(
            new Uri("https://cdn.example.com/20250311/eQ/index.m3u8")));
    }

    [Fact]
    public void TryUnwrapEmbeddedMediaUrl_ExtractsPlayGatewayM3u8()
    {
        var shell = new Uri(
            "https://jiexi.example.com/play/?url=" +
            Uri.EscapeDataString("https://vv.example.com/20250311/eQ/index.m3u8"));
        Assert.True(MediaUrlNormalizer.TryUnwrapEmbeddedMediaUrl(shell, out var media));
        Assert.Equal("https://vv.example.com/20250311/eQ/index.m3u8", media.AbsoluteUri);
    }

    [Fact]
    public void TryUnwrapEmbeddedMediaUrl_IgnoresAlreadyManifestUrls()
    {
        var manifest = new Uri("https://vv.example.com/a/index.m3u8?token=1");
        Assert.False(MediaUrlNormalizer.TryUnwrapEmbeddedMediaUrl(manifest, out _));
    }
}
