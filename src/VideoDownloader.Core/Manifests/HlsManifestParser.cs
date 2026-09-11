using System.Globalization;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Manifests;

public sealed record HlsParseResult(
    IReadOnlyList<MediaVariant> Variants,
    bool IsDrmProtected,
    bool IsMasterPlaylist,
    double? DurationSec = null,
    Uri? FirstSegmentUrl = null);

public static class HlsManifestParser
{
    private const string ExtM3u = "#EXTM3U";
    private const string ExtStreamInf = "#EXT-X-STREAM-INF";
    private const string ExtMedia = "#EXT-X-MEDIA";
    private const string ExtKey = "#EXT-X-KEY:";
    private const string ExtInf = "#EXTINF:";
    private const string ExtMediaSequence = "#EXT-X-MEDIA-SEQUENCE:";

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

        var (variant, durationSec, firstSegment) = ParseMediaPlaylistVariant(content, manifestUrl, context, isDrm);
        return new HlsParseResult([variant], isDrm, false, durationSec, firstSegment);
    }

    private static (MediaVariant Variant, double? DurationSec, Uri? FirstSegment) ParseMediaPlaylistVariant(
        string content,
        Uri manifestUrl,
        RequestContext context,
        bool isDrm)
    {
        // Encryption metadata is only materialized for clear-key AES and never for license DRM.
        // Plain media playlists (no EXT-X-KEY) are still Combined — AES-128 is encryption, not a
        // separate media type; both download via N_m3u8DL-RE once the playlist is reachable.
        var hls = TryParseClearKeyMedia(content, manifestUrl, context, isDrm);
        var hasSegments = content.Contains(ExtInf, StringComparison.OrdinalIgnoreCase);
        var kind = !isDrm && (hls is not null || hasSegments)
            ? MediaTrackKind.Combined
            : MediaTrackKind.Unknown;
        var (durationSec, firstSegment) = SummarizeMediaSegments(content, manifestUrl);
        var track = new MediaTrack(
            "hls-media",
            kind,
            manifestUrl,
            null,
            "hls",
            null,
            null,
            context)
        {
            Hls = hls,
            IsValidated = kind == MediaTrackKind.Combined
        };
        return (MediaVariant.FromTracks("media", null, null, null, "hls", [track]), durationSec, firstSegment);
    }

    /// <summary>Sums EXTINF durations and returns the first media segment URI when present.</summary>
    internal static (double? DurationSec, Uri? FirstSegment) SummarizeMediaSegments(string content, Uri manifestUrl)
    {
        double total = 0;
        var sawDuration = false;
        Uri? first = null;
        var expectSegment = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith(ExtInf, StringComparison.OrdinalIgnoreCase))
            {
                expectSegment = true;
                var payload = line[ExtInf.Length..];
                var comma = payload.IndexOf(',');
                var number = comma >= 0 ? payload[..comma] : payload;
                if (double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds > 0 &&
                    double.IsFinite(seconds))
                {
                    total += seconds;
                    sawDuration = true;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                expectSegment = false;
                continue;
            }

            if (!expectSegment)
                continue;

            first ??= new Uri(ManifestParserUtil.CombineUrl(manifestUrl.ToString(), line));
            expectSegment = false;
        }

        return (sawDuration && total > 0.5 ? total : null, first);
    }

    /// <summary>
    /// Builds <see cref="HlsMedia"/> only when the playlist uses clear-key AES.
    /// Plain / DRM playlists return null so other download paths stay unchanged.
    /// </summary>
    internal static HlsMedia? TryParseClearKeyMedia(
        string content,
        Uri manifestUrl,
        RequestContext context,
        bool isDrm)
    {
        if (isDrm)
            return null;

        HlsEncryption? encryption = null;
        var segments = new List<Uri>();
        long mediaSequence = 0;
        var expectSegment = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith(ExtMediaSequence, StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(line[ExtMediaSequence.Length..].Trim(), out mediaSequence);
                continue;
            }

            if (line.StartsWith(ExtKey, StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the last clear-key line (HLS allows key rotation).
                var parsed = TryParseClearKeyLine(line, manifestUrl);
                if (parsed is not null)
                    encryption = parsed;
                continue;
            }

            if (line.StartsWith(ExtInf, StringComparison.OrdinalIgnoreCase))
            {
                expectSegment = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                expectSegment = false;
                continue;
            }

            if (!expectSegment)
                continue;

            segments.Add(new Uri(ManifestParserUtil.CombineUrl(manifestUrl.ToString(), line)));
            expectSegment = false;
        }

        if (encryption is not { KeyUri: not null } enc ||
            !HlsMedia.IsClearKeyMethod(enc.Method) ||
            segments.Count == 0)
            return null;

        return new HlsMedia(manifestUrl, segments, enc, mediaSequence, context);
    }

    private static HlsEncryption? TryParseClearKeyLine(string keyLine, Uri playlistUrl)
    {
        if (DrmAnalyzer.IsDrmKeyLine(keyLine))
            return null;

        var method = ManifestParserUtil.GetAttribute(keyLine, "METHOD");
        if (!HlsMedia.IsClearKeyMethod(method))
            return null;

        var uriText = ManifestParserUtil.GetAttribute(keyLine, "URI");
        if (string.IsNullOrWhiteSpace(uriText))
            return null;

        // Strip optional quotes left by unquoted/odd playlists.
        uriText = uriText.Trim().Trim('"');
        var keyUri = new Uri(ManifestParserUtil.CombineUrl(playlistUrl.ToString(), uriText));
        var iv = ManifestParserUtil.GetAttribute(keyLine, "IV")?.Trim();
        if (iv is { Length: > 2 } && iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            iv = iv[2..];

        return new HlsEncryption(method!, keyUri, iv);
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
