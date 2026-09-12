using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>Maps exclusive-detector MediaDescriptor to UI/download DetectedVideo.</summary>
public static class MediaDescriptorMapper
{
    private static Uri ResolveRecoveryPage(MediaDescriptor descriptor)
    {
        var page = descriptor.PageUrl;
        if (MediaAddressRenewal.HasStableContentAddress(page))
            return page;
        if (descriptor.MediaId is { Length: > 0 } id)
            return MediaAddressRenewal.RecoveryAddress(page, "id:" + id) ?? page;
        return page;
    }

    public static DetectedVideo ToDetectedVideo(MediaDescriptor descriptor, Guid sessionId)
    {
        var variants = BuildVariants(descriptor);
        if (descriptor.VideoHeight is > 0)
        {
            variants = variants
                .Select(v => v.Height is > 0 ? v : v with { Height = descriptor.VideoHeight })
                .ToArray();
        }
        var family = descriptor.ContentType switch
        {
            MediaContentType.Album => MediaFamily.DirectMp4,
            _ when variants.Any(v => v.Container is "hls") => MediaFamily.Hls,
            _ when variants.Any(v => v.Container is "dash") => MediaFamily.Dash,
            _ => MediaFamily.DirectMp4
        };

        var idSeed = $"exclusive:{descriptor.Site}:{descriptor.MediaId ?? descriptor.PageUrl.AbsoluteUri}:{descriptor.ContentType}";
        var videoId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed)).AsSpan(0, 16));
        var baseTitle = string.IsNullOrWhiteSpace(descriptor.DisplayTitle)
            ? (descriptor.ContentType == MediaContentType.Album ? "相册" : "视频")
            : descriptor.DisplayTitle!;
        var preferred = variants
            .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
            .Where(v => !v.Tracks.Any(t => t.IsMseTrack || MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl)))
            .OrderByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .ThenByDescending(v => v.Height ?? 0)
            .FirstOrDefault()
            ?? variants.FirstOrDefault(v =>
                !v.Tracks.Any(t => t.IsMseTrack || MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl)))
            ?? variants.FirstOrDefault();
        var title = AppendDetectedMeta(baseTitle, preferred);

        return new DetectedVideo(
            videoId,
            descriptor.Site,
            descriptor.MediaId,
            title,
            descriptor.PageUrl,
            family,
            variants,
            false,
            ProbeSource.SiteAdapter)
        {
            SessionId = sessionId,
            Availability = variants.Count > 0
                ? MediaAvailabilityKind.Complete
                : MediaAvailabilityKind.Unavailable,
            DurationSec = descriptor.DurationSec is > 0 ? descriptor.DurationSec : null,
            Metadata = descriptor.Author is null
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["author"] = descriptor.Author
                }
        };
    }

    /// <summary>UI caption may show resolution only — size/format belong to variant details, not the title.</summary>
    public static string AppendDetectedMeta(string title, MediaVariant? variant)
    {
        if (variant is null || string.IsNullOrWhiteSpace(title))
            return title;

        if (variant.Height is not > 0)
            return title;

        var bit = variant.Height.Value.ToString(CultureInfo.InvariantCulture) + "p";
        if (title.Contains(bit, StringComparison.OrdinalIgnoreCase))
            return title;
        return title.TrimEnd() + " " + bit;
    }

    private static IReadOnlyList<MediaVariant> BuildVariants(MediaDescriptor descriptor)
    {
        if (descriptor.Formats.Count > 0)
        {
            var recovery = ResolveRecoveryPage(descriptor);
            // Drop illegal "Combined + separate audio remux" duplicates; keep ladder + audio-only modes.
            // Never surface ByteDance MSE adaptive tracks as ordinary download variants.
            var ladder = descriptor.Formats
                .Where(v => v.Tracks.Count > 0)
                .Where(v => !v.Tracks.Any(t => t.IsMseTrack || MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl)))
                .Where(v => !(v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined) &&
                              v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio && t.TrackId != "audio-extract")))
                .GroupBy(v => string.Join("|", v.Tracks.Select(t => t.SourceUrl.AbsoluteUri)), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var best = g.OrderByDescending(x => x.TotalContentLength ?? x.Bandwidth ?? 0).First();
                    var identity = best.ContentIdentity
                        ?? (descriptor.MediaId is null ? null : "id:" + descriptor.MediaId);
                    return best with
                    {
                        ContentIdentity = identity,
                        RecoveryPageUrl = best.RecoveryPageUrl is not null &&
                                          MediaAddressRenewal.HasStableContentAddress(best.RecoveryPageUrl)
                            ? best.RecoveryPageUrl
                            : recovery
                    };
                })
                // Prefer durable CDNs (zjcdn) over fragile signed hosts (web-prime) as the default pick.
                .OrderByDescending(MediaAddressRenewal.DurableHostScore)
                .ThenByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .ToArray();

            // Attach sibling formats so HTTP 403 recovery can switch hosts/qualities without rediscovery.
            return ladder
                .Select(primary => MediaVariantAlternatives.WithLadder(primary, ladder))
                .ToArray();
        }

        if (descriptor.ContentType == MediaContentType.Album && descriptor.Images.Count > 0)
        {
            var ctx = descriptor.RequestContext;
            var images = descriptor.Images
                .OrderBy(i => i.Index)
                .Select((img, i) => new MediaTrack(
                    $"album-img-{i}",
                    MediaTrackKind.Image,
                    img.Url,
                    null,
                    img.Format ?? "image",
                    null,
                    null,
                    img.RequestContext.Cookies.Count > 0 ? img.RequestContext : ctx)
                {
                    IsValidated = true,
                    ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                    Evidence = MediaEvidence.DomObserved
                })
                .ToList();

            if (descriptor.Audio is not null)
                images.Add(descriptor.Audio with
                {
                    Kind = MediaTrackKind.Audio,
                    ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId
                });

            return
            [
                MediaVariant.FromTracks(
                    $"相册视频（{descriptor.Images.Count} 张）",
                    null,
                    null,
                    null,
                    "album",
                    images) with
                {
                    ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                    RecoveryPageUrl = ResolveRecoveryPage(descriptor)
                }
            ];
        }

        var list = new List<MediaVariant>();
        var videoIsMse = descriptor.Video is not null &&
                         (descriptor.Video.IsMseTrack ||
                          MediaUrlNormalizer.IsByteDanceMseVideoTrack(descriptor.Video.SourceUrl));

        // MSE video (+ optional audio) must NOT become an ordinary remux DownloadVariant.
        if (videoIsMse)
            return list;

        if (descriptor.Video is not null && descriptor.Audio is not null &&
            descriptor.Video.Kind != MediaTrackKind.Combined)
        {
            list.Add(MediaVariant.FromTracks(
                "视频",
                null,
                descriptor.VideoHeight is > 0 ? descriptor.VideoHeight : null,
                null,
                "mkv",
                [descriptor.Video, descriptor.Audio]) with
            {
                ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                RecoveryPageUrl = ResolveRecoveryPage(descriptor)
            });
        }
        else if (descriptor.Video is not null)
        {
            var track = descriptor.Video.Kind is MediaTrackKind.Video or MediaTrackKind.Combined
                ? descriptor.Video
                : descriptor.Video with { Kind = MediaTrackKind.Combined };
            // Combined already embeds audio — never pair a second audio track for remux/download.
            list.Add(MediaVariant.FromCombinedTrack(
                "视频",
                track.SourceUrl,
                track.RequestContext,
                height: descriptor.VideoHeight is > 0 ? descriptor.VideoHeight : null,
                container: track.Container ?? "mp4",
                contentLength: track.ContentLength) with
            {
                Tracks = [track with { Kind = MediaTrackKind.Combined }],
                ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                RecoveryPageUrl = ResolveRecoveryPage(descriptor)
            });

            // Audio-only mode may extract from the same Combined URL; not a simultaneous A+V download.
            if (track.Kind == MediaTrackKind.Combined || descriptor.Audio is null)
            {
                list.Add(MediaVariant.FromTracks(
                    "音轨",
                    null,
                    null,
                    track.Bandwidth,
                    "mka",
                    [track with { Kind = MediaTrackKind.Audio, TrackId = "audio-extract", Codec = null }]) with
                {
                    ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                    RecoveryPageUrl = ResolveRecoveryPage(descriptor)
                });
            }
        }
        else if (descriptor.Audio is not null)
        {
            list.Add(MediaVariant.FromTracks(
                "音轨",
                null,
                null,
                descriptor.Audio.Bandwidth,
                descriptor.Audio.Container ?? "m4a",
                [descriptor.Audio]) with
            {
                ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                RecoveryPageUrl = ResolveRecoveryPage(descriptor)
            });
        }

        return list;
    }
}
