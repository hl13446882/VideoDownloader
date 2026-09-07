using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Manifests;

public sealed record HlsParseResult(
    IReadOnlyList<MediaVariant> Variants,
    bool IsDrmProtected,
    bool IsMasterPlaylist);

public static class HlsManifestParser
{
    private const string ExtM3u = "#EXTM3U";
    private const string ExtStreamInf = "#EXT-X-STREAM-INF";
    private const string ExtMedia = "#EXT-X-MEDIA";

    public static HlsParseResult Parse(string content, Uri manifestUrl, RequestContext context)
    {
        content = content.Trim();
        if (!content.StartsWith(ExtM3u, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid HLS manifest: missing #EXTM3U.");

        var baseUrl = manifestUrl.ToString();
        var isDrm = DrmAnalyzer.IsHlsDrmProtected(content);

        if (content.Contains(ExtStreamInf, StringComparison.OrdinalIgnoreCase))
        {
            var variants = ParseMasterPlaylist(content, baseUrl, context);
            return new HlsParseResult(variants, isDrm, true);
        }

        return new HlsParseResult(
            [MediaVariant.FromTracks("media", null, null, null, "hls",
                [new MediaTrack("hls-media", MediaTrackKind.Unknown, manifestUrl, null, "hls", null, null, context)])],
            isDrm,
            false);
    }

    private static List<MediaVariant> ParseMasterPlaylist(
        string content,
        string baseUrl,
        RequestContext context)
    {
        var variants = new List<MediaVariant>();
        var audioGroups = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith(ExtMedia + ":", StringComparison.OrdinalIgnoreCase) &&
                ManifestParserUtil.GetAttribute(l,"TYPE") == "AUDIO" && ManifestParserUtil.GetAttribute(l,"URI") is not null)
            .Select(l => new {
                Group = ManifestParserUtil.GetAttribute(l,"GROUP-ID"),
                Track = new MediaTrack("hls-audio:" + ManifestParserUtil.GetAttribute(l,"NAME"), MediaTrackKind.Audio,
                    new Uri(ManifestParserUtil.CombineUrl(baseUrl,ManifestParserUtil.GetAttribute(l,"URI")!)),null,"hls",null,null,context)
            }).ToArray();
        using var reader = new StringReader(content);
        string? line;
        var expectPlaylist = false;
        string? bandwidth = null;
        string? resolution = null;
        string? codecs = null;
        string? audioGroup = null;
        var index = 0;

        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith(ExtStreamInf, StringComparison.OrdinalIgnoreCase))
            {
                bandwidth = ManifestParserUtil.GetAttribute(line, "AVERAGE-BANDWIDTH")
                            ?? ManifestParserUtil.GetAttribute(line, "BANDWIDTH");
                resolution = ManifestParserUtil.GetAttribute(line, "RESOLUTION");
                codecs = ManifestParserUtil.GetAttribute(line, "CODECS");
                audioGroup = ManifestParserUtil.GetAttribute(line,"AUDIO");
                expectPlaylist = true;
                continue;
            }

            if (line.StartsWith(ExtMedia, StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith('#'))
                continue;

            if (!expectPlaylist)
                continue;

            var playlistUrl = new Uri(ManifestParserUtil.CombineUrl(baseUrl, line.Trim()));
            var (width, height) = ManifestParserUtil.ParseResolution(resolution);
            var (videoCodec, audioCodec) = ManifestParserUtil.ParseCodecs(codecs);
            long? bw = long.TryParse(bandwidth, out var b) ? b : null;

            var paired = audioGroups.Where(a=>a.Group==audioGroup).ToArray();
            if (paired.Length > 0)
            {
                foreach (var audio in paired)
                    variants.Add(MediaVariant.FromTracks($"{height}p {audio.Track.TrackId}",width,height,bw,"hls",
                        [new MediaTrack("hls-video",MediaTrackKind.Video,playlistUrl,videoCodec,"hls",bw,null,context),audio.Track]));
            }
            else
            {
                var kind = videoCodec is not null && audioCodec is not null ? MediaTrackKind.Combined :
                    videoCodec is not null ? MediaTrackKind.Video : audioCodec is not null ? MediaTrackKind.Audio : MediaTrackKind.Unknown;
                variants.Add(MediaVariant.FromTracks(height is not null ? $"{height}p" : $"variant-{index}",width,height,bw,"hls",
                    [new MediaTrack("hls-media",kind,playlistUrl,videoCodec ?? audioCodec,"hls",bw,null,context)]));
            }

            expectPlaylist = false;
            index++;
        }

        variants.AddRange(audioGroups.Select(a=>MediaVariant.FromTracks(a.Track.TrackId,null,null,null,"hls",[a.Track])));

        return variants
            .OrderByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.Bandwidth ?? 0)
            .ToList();
    }
}
