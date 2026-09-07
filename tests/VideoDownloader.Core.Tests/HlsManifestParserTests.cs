using VideoDownloader.Core.Manifests;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class HlsManifestParserTests
{
    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
        1080p/playlist.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2"
        720p/playlist.m3u8
        """;

    [Fact]
    public void Parse_MasterPlaylist_ReturnsTwoVariants()
    {
        var context = RequestContext.CreateEmpty();
        var result = HlsManifestParser.Parse(
            MasterPlaylist,
            new Uri("http://localhost/hls/master.m3u8"),
            context);

        Assert.True(result.IsMasterPlaylist);
        Assert.Equal(2, result.Variants.Count);
        Assert.Contains(result.Variants, v => v.Height == 1080);
        Assert.Contains(result.Variants, v => v.Height == 720);
        Assert.False(result.IsDrmProtected);
    }

    [Fact]
    public void Parse_WidevineKeyLine_MarksDrmProtected()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-KEY:METHOD=SAMPLE-AES,KEYFORMAT="com.widevine",URI="https://example.com/key"
            #EXTINF:6.0,
            seg001.ts
            """;

        var result = HlsManifestParser.Parse(
            playlist,
            new Uri("http://localhost/hls/media.m3u8"),
            RequestContext.CreateEmpty());

        Assert.True(result.IsDrmProtected);
    }

    [Fact]
    public void Parse_Aes128Only_IsNotDrmProtected()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="https://example.com/key.bin"
            #EXTINF:6.0,
            seg001.ts
            """;

        var result = HlsManifestParser.Parse(
            playlist,
            new Uri("http://localhost/hls/media.m3u8"),
            RequestContext.CreateEmpty());

        Assert.False(result.IsDrmProtected);
    }
}

public class ManifestParserUtilTests
{
    [Fact]
    public void GetAttribute_ParsesQuotedValues()
    {
        const string line = "#EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=\"1280x720\",CODECS=\"avc1.64001f,mp4a.40.2\"";
        Assert.Equal("1280x720", ManifestParserUtil.GetAttribute(line, "RESOLUTION"));
        Assert.Equal("2800000", ManifestParserUtil.GetAttribute(line, "BANDWIDTH"));
    }

    [Fact]
    public void CombineUrl_ResolvesRelativePath()
    {
        var combined = ManifestParserUtil.CombineUrl(
            "http://localhost/hls/master.m3u8",
            "720p/playlist.m3u8");
        Assert.Equal("http://localhost/hls/720p/playlist.m3u8", combined);
    }
}
