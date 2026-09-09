using System.Security.Cryptography;
using System.Text;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>Maps exclusive-detector MediaDescriptor to UI/download DetectedVideo.</summary>
public static class MediaDescriptorMapper
{
    public static DetectedVideo ToDetectedVideo(MediaDescriptor descriptor, Guid sessionId)
    {
        var variants = BuildVariants(descriptor);
        var family = descriptor.ContentType switch
        {
            MediaContentType.Album => MediaFamily.DirectMp4,
            _ when descriptor.Video?.Container is "hls" => MediaFamily.Hls,
            _ when descriptor.Video?.Container is "dash" => MediaFamily.Dash,
            _ => MediaFamily.DirectMp4
        };

        var idSeed = $"exclusive:{descriptor.Site}:{descriptor.MediaId ?? descriptor.PageUrl.AbsoluteUri}:{descriptor.ContentType}";
        var videoId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed)).AsSpan(0, 16));
        var title = string.IsNullOrWhiteSpace(descriptor.DisplayTitle)
            ? (descriptor.ContentType == MediaContentType.Album ? "相册" : "视频")
            : descriptor.DisplayTitle!;

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
            Metadata = descriptor.Author is null
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["author"] = descriptor.Author
                }
        };
    }

    private static IReadOnlyList<MediaVariant> BuildVariants(MediaDescriptor descriptor)
    {
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
                    RecoveryPageUrl = descriptor.PageUrl
                }
            ];
        }

        var list = new List<MediaVariant>();
        if (descriptor.Video is not null && descriptor.Audio is not null &&
            descriptor.Video.Kind != MediaTrackKind.Combined)
        {
            list.Add(MediaVariant.FromTracks(
                "视频",
                null,
                null,
                null,
                "mkv",
                [descriptor.Video, descriptor.Audio]) with
            {
                ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                RecoveryPageUrl = descriptor.PageUrl
            });
        }
        else if (descriptor.Video is not null)
        {
            var track = descriptor.Video.Kind is MediaTrackKind.Video or MediaTrackKind.Combined
                ? descriptor.Video
                : descriptor.Video with { Kind = MediaTrackKind.Combined };
            list.Add(MediaVariant.FromCombinedTrack(
                "视频",
                track.SourceUrl,
                track.RequestContext,
                container: track.Container ?? "mp4",
                contentLength: track.ContentLength) with
            {
                Tracks = [track with { Kind = MediaTrackKind.Combined }],
                ContentIdentity = descriptor.MediaId is null ? null : "id:" + descriptor.MediaId,
                RecoveryPageUrl = descriptor.PageUrl
            });

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
                    RecoveryPageUrl = descriptor.PageUrl
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
                RecoveryPageUrl = descriptor.PageUrl
            });
        }

        return list;
    }
}
