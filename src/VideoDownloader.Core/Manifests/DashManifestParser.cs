using System.Xml.Linq;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Manifests;

public sealed record DashParseResult(
    IReadOnlyList<MediaVariant> Variants,
    bool IsDrmProtected);

public static class DashManifestParser
{
    public static DashParseResult Parse(string content, Uri manifestUrl, RequestContext context)
    {
        var isDrm = DrmAnalyzer.IsDashDrmProtected(content);
        var doc = XDocument.Parse(content);
        var mpd = doc.Root ?? throw new InvalidOperationException("Invalid DASH MPD: missing root.");

        var baseUrl = manifestUrl.ToString();
        var rootBase = mpd.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value;
        if (!string.IsNullOrWhiteSpace(rootBase))
            baseUrl = ManifestParserUtil.CombineUrl(baseUrl, rootBase);

        var variants = new List<MediaVariant>();
        var index = 0;

        foreach (var period in mpd.Elements().Where(e => e.Name.LocalName == "Period"))
        {
            var periodVariants = new List<MediaVariant>();
            var periodBase = ExtendBaseUrl(period, baseUrl);
            foreach (var adaptationSet in period.Elements().Where(e => e.Name.LocalName == "AdaptationSet"))
            {
                var setBase = ExtendBaseUrl(adaptationSet, periodBase);
                var mimeType = adaptationSet.Attribute("mimeType")?.Value
                               ?? adaptationSet.Attribute("contentType")?.Value;
                var isVideo = mimeType?.StartsWith("video", StringComparison.OrdinalIgnoreCase) == true;
                var isAudio = mimeType?.StartsWith("audio", StringComparison.OrdinalIgnoreCase) == true;

                foreach (var representation in adaptationSet.Elements().Where(e => e.Name.LocalName == "Representation"))
                {
                    var repMime = representation.Attribute("mimeType")?.Value ?? mimeType;
                    var kind = repMime?.StartsWith("audio",StringComparison.OrdinalIgnoreCase)==true ? MediaTrackKind.Audio :
                        repMime?.StartsWith("video",StringComparison.OrdinalIgnoreCase)==true ? MediaTrackKind.Video : MediaTrackKind.Combined;
                    var bandwidth = representation.Attribute("bandwidth")?.Value;
                    var width = ParseInt(representation.Attribute("width")?.Value);
                    var height = ParseInt(representation.Attribute("height")?.Value);
                    var codecs = representation.Attribute("codecs")?.Value
                                 ?? adaptationSet.Attribute("codecs")?.Value;
                    var (videoCodec, audioCodec) = ManifestParserUtil.ParseCodecs(codecs);

                    var repId = representation.Attribute("id")?.Value ?? index.ToString();
                    var role = isVideo ? "video" : isAudio ? "audio" : "rep";
                    var label = height is not null ? $"{height}p-{role}" : $"{role}-{repId}";

                    periodVariants.Add(MediaVariant.FromTracks(
                        label,
                        width,
                        height,
                        long.TryParse(bandwidth, out var bw) ? bw : null,
                        "dash",
                        [new MediaTrack("dash:" + repId,kind,manifestUrl,
                            kind == MediaTrackKind.Audio ? audioCodec : videoCodec,"dash",bw > 0 ? bw : null,null,context)]));

                    index++;
                }
            }
            var audios = periodVariants.Where(v=>v.Tracks[0].Kind==MediaTrackKind.Audio).ToArray();
            foreach (var video in periodVariants.Where(v=>v.Tracks[0].Kind!=MediaTrackKind.Audio))
            {
                if (audios.Length==0) variants.Add(video);
                else foreach(var audio in audios)
                    variants.Add(video with {VariantId=video.VariantId+"+"+audio.VariantId,Tracks=[video.Tracks[0],audio.Tracks[0]]});
            }
            variants.AddRange(audios);
        }

        if (variants.Count == 0)
        {
            variants.Add(MediaVariant.FromCombinedTrack(
                "dash-default",
                manifestUrl,
                context,
                container: "dash"));
        }

        return new DashParseResult(
            variants.OrderByDescending(v => v.Height ?? 0).ToList(),
            isDrm);
    }

    private static string ExtendBaseUrl(XElement element, string currentBase)
    {
        var nested = element.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value;
        return string.IsNullOrWhiteSpace(nested) ? currentBase : ManifestParserUtil.CombineUrl(currentBase, nested);
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;
}
