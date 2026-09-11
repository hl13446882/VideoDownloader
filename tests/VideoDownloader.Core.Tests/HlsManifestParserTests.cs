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
            #EXT-X-MEDIA-SEQUENCE:0
            #EXT-X-KEY:METHOD=AES-128,URI="https://example.com/key.bin",IV=0x00000000000000000000000000000000
            #EXTINF:6.0,
            seg001.ts
            """;

        var result = HlsManifestParser.Parse(
            playlist,
            new Uri("http://localhost/hls/media.m3u8"),
            RequestContext.CreateEmpty());

        Assert.False(result.IsDrmProtected);
        var track = Assert.Single(Assert.Single(result.Variants).Tracks);
        Assert.Equal(MediaTrackKind.Combined, track.Kind);
        Assert.NotNull(track.Hls);
        Assert.True(track.Hls!.HasClearKeyEncryption);
        Assert.Equal("AES-128", track.Hls.Encryption!.Method);
        Assert.Equal(new Uri("https://example.com/key.bin"), track.Hls.Encryption.KeyUri);
        Assert.Equal("00000000000000000000000000000000", track.Hls.Encryption.IvHex);
        Assert.Equal(new Uri("http://localhost/hls/seg001.ts"), Assert.Single(track.Hls.Segments));
    }

    [Fact]
    public void Parse_Aes128RelativeKey_ResolvesAgainstPlaylist()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="enc.key",IV=0x00000000000000000000000000000000
            #EXTINF:6.0,
            seg0.ts
            """;

        var result = HlsManifestParser.Parse(
            playlist,
            new Uri("https://vv.example.com/play/abc/index.m3u8"),
            RequestContext.CreateEmpty());

        var hls = Assert.Single(Assert.Single(result.Variants).Tracks).Hls;
        Assert.NotNull(hls);
        Assert.Equal(new Uri("https://vv.example.com/play/abc/enc.key"), hls!.Encryption!.KeyUri);
    }

    [Fact]
    public void Parse_PlainMediaPlaylist_DoesNotAttachHlsMedia()
    {
        const string playlist = """
            #EXTM3U
            #EXTINF:6.0,
            seg001.ts
            #EXT-X-ENDLIST
            """;

        var result = HlsManifestParser.Parse(
            playlist,
            new Uri("http://localhost/hls/media.m3u8"),
            RequestContext.CreateEmpty());

        var track = Assert.Single(Assert.Single(result.Variants).Tracks);
        Assert.Equal(MediaTrackKind.Combined, track.Kind);
        Assert.True(track.IsValidated);
        Assert.Null(track.Hls);
        Assert.Equal(6.0, result.DurationSec);
        Assert.Equal(new Uri("http://localhost/hls/seg001.ts"), result.FirstSegmentUrl);
    }

    [Fact]
    public void Parse_MasterPlaylist_ExposesResolutionAndBandwidth()
    {
        var result = HlsManifestParser.Parse(
            MasterPlaylist,
            new Uri("http://localhost/hls/master.m3u8"),
            RequestContext.CreateEmpty());

        var best = Assert.Single(result.Variants, v => v.Height == 1080);
        Assert.Equal(5_000_000, best.Bandwidth);
        Assert.Equal(5_000_000, Assert.Single(best.Tracks).Bandwidth);
        Assert.Null(result.DurationSec);
        Assert.Null(result.FirstSegmentUrl);
    }

    [Fact]
    public void Parse_Widevine_DoesNotAttachClearKeyHlsMedia()
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
        Assert.Null(Assert.Single(Assert.Single(result.Variants).Tracks).Hls);
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
