using VideoDownloader.Core.Manifests;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class ManifestCoverageTests
{
    [Fact]
    public void Hls_PreservesAudioGroupsAndBothQualities()
    {
        var result=HlsManifestParser.Parse("""
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="original",URI="audio.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720,AUDIO="a"
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,AUDIO="a"
            high.m3u8
            """,new Uri("https://cdn.test/master.m3u8"),RequestContext.CreateEmpty());
        Assert.Contains(result.Variants,v=>v.Height==1080 && v.Tracks.Count==2);
        Assert.Contains(result.Variants,v=>v.Height==720 && v.Tracks.Count==2);
        Assert.Contains(result.Variants,v=>v.Tracks.All(t=>t.Kind==MediaTrackKind.Audio));
    }

    [Fact]
    public void Dash_PreservesRepresentationIdsAndAudioSelection()
    {
        var result=DashManifestParser.Parse("""
            <MPD><Period><AdaptationSet mimeType="video/mp4">
            <Representation id="v720" height="720" bandwidth="1000000"/>
            <Representation id="v1080" height="1080" bandwidth="3000000"/>
            </AdaptationSet><AdaptationSet mimeType="audio/mp4"><Representation id="audio" bandwidth="128000"/></AdaptationSet></Period></MPD>
            """,new Uri("https://cdn.test/master.mpd"),RequestContext.CreateEmpty());
        Assert.Contains(result.Variants,v=>v.Height==1080 && v.Tracks[0].TrackId=="dash:v1080" && v.Tracks[1].Kind==MediaTrackKind.Audio);
        Assert.Contains(result.Variants,v=>v.Tracks.All(t=>t.Kind==MediaTrackKind.Audio));
    }
}
