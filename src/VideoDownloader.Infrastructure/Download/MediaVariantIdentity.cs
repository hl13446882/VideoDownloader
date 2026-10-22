using System.Globalization;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// DASH representations share one manifest URL, so the URL is not a variant identity.
/// </summary>
internal static class MediaVariantIdentity
{
    public static bool IsDashVariant(MediaVariant variant) =>
        string.Equals(MediaContainer.Normalize(variant.Container, variant.SourceUrl), "dash", StringComparison.Ordinal) ||
        (variant.Tracks.Count > 0 &&
         variant.Tracks.All(t =>
             string.Equals(MediaContainer.Normalize(t.Container, t.SourceUrl), "dash", StringComparison.Ordinal)));

    public static string DedupKey(MediaVariant variant)
    {
        if (!IsDashVariant(variant))
            return "url:" + string.Join("|", variant.Tracks.Select(t => t.SourceUrl.AbsoluteUri));

        var video = variant.Tracks.FirstOrDefault(t =>
            t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined);
        if (video is not null)
        {
            return string.Join("|",
                "dash-video",
                video.SourceUrl.AbsoluteUri,
                video.Kind.ToString(),
                video.TrackId,
                video.Codec ?? string.Empty,
                Number(variant.Width),
                Number(variant.Height),
                Number(video.Bandwidth ?? variant.Bandwidth));
        }

        var audio = variant.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio);
        return string.Join("|",
            "dash-audio",
            audio?.SourceUrl.AbsoluteUri ?? variant.SourceUrl.AbsoluteUri,
            audio?.TrackId ?? string.Empty,
            audio?.Codec ?? string.Empty,
            Number(audio?.Bandwidth ?? variant.Bandwidth));
    }

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
