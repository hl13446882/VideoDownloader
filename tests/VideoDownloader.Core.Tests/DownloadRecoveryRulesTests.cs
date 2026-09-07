using VideoDownloader.Core.Download;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class DownloadRecoveryRulesTests
{
    [Theory]
    [InlineData(DownloadStatus.Pending, DownloadStatus.Pending)]
    [InlineData(DownloadStatus.Preparing, DownloadStatus.Pending)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Muxing, DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Completed, DownloadStatus.Completed)]
    public void ResolveStartupStatus_MapsCorrectly(DownloadStatus input, DownloadStatus expected)
    {
        Assert.Equal(expected, DownloadRecoveryRules.ResolveStartupStatus(input));
    }

    [Fact]
    public void ResolveStartupError_Muxing_ReturnsMuxInterrupted()
    {
        Assert.Equal(
            VideoDownloader.Core.Errors.ErrorCodes.MuxInterrupted,
            DownloadRecoveryRules.ResolveStartupError(DownloadStatus.Muxing));
    }
}

public class DashManifestParserTests
{
    private const string CleanMpd = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period>
            <AdaptationSet mimeType="video/mp4">
              <Representation id="1080" bandwidth="5000000" width="1920" height="1080" codecs="avc1.640028"/>
            </AdaptationSet>
            <AdaptationSet mimeType="audio/mp4">
              <Representation id="audio" bandwidth="128000" codecs="mp4a.40.2"/>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    private const string DrmMpd = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" type="static">
          <Period>
            <AdaptationSet mimeType="video/mp4">
              <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
              <Representation id="1080" bandwidth="5000000" width="1920" height="1080"/>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Parse_CleanMpd_ReturnsVideoAndAudioVariants()
    {
        var manifestUrl = new Uri("http://localhost/dash/manifest.mpd");
        var result = VideoDownloader.Core.Manifests.DashManifestParser.Parse(
            CleanMpd, manifestUrl, RequestContext.CreateEmpty());

        Assert.False(result.IsDrmProtected);
        Assert.Equal(2, result.Variants.Count);
        Assert.All(result.Variants, v => Assert.Equal(manifestUrl, v.SourceUrl));
    }

    [Fact]
    public void Parse_DrmMpd_IsDrmProtected()
    {
        var result = VideoDownloader.Core.Manifests.DashManifestParser.Parse(
            DrmMpd,
            new Uri("http://localhost/dash/manifest-drm.mpd"),
            RequestContext.CreateEmpty());

        Assert.True(result.IsDrmProtected);
    }
}
