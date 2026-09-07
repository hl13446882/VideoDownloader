namespace VideoDownloader.Core.Models;

public sealed record MediaVariant(
    string VariantId,
    int? Width,
    int? Height,
    long? Bandwidth,
    string? Container,
    IReadOnlyList<MediaTrack> Tracks)
{
    public string? ContentIdentity { get; init; }
    public Uri? RecoveryPageUrl { get; init; }
    public IReadOnlyList<MediaVariant> Alternatives { get; init; } = [];
    public Uri SourceUrl => PrimaryTrack.SourceUrl;

    public RequestContext RequestContext => PrimaryTrack.RequestContext;

    public string? VideoCodec =>
        Tracks.FirstOrDefault(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)?.Codec;

    public string? AudioCodec =>
        Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio)?.Codec;

    public long? TotalContentLength
    {
        get
        {
            if (Tracks.Any(t => t.ContentLength is null))
                return null;

            return Tracks.Sum(t => t.ContentLength ?? 0);
        }
    }

    private MediaTrack PrimaryTrack =>
        Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Combined)
        ?? Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Video)
        ?? Tracks[0];

    public static MediaVariant FromCombinedTrack(
        string variantId,
        Uri sourceUrl,
        RequestContext context,
        int? width = null,
        int? height = null,
        long? bandwidth = null,
        string? codec = null,
        string? container = null,
        long? contentLength = null) =>
        new(
            variantId,
            width,
            height,
            bandwidth,
            container,
            [
                new MediaTrack(
                    "combined",
                    MediaTrackKind.Combined,
                    sourceUrl,
                    codec,
                    container,
                    bandwidth,
                    contentLength,
                    context)
            ]);

    public static MediaVariant FromTracks(
        string variantId,
        int? width,
        int? height,
        long? bandwidth,
        string? container,
        IReadOnlyList<MediaTrack> tracks) =>
        new(variantId, width, height, bandwidth, container, tracks);

    public MediaVariant WithRequestContext(RequestContext context) =>
        this with
        {
            Tracks = Tracks
                .Select(t => t with { RequestContext = context })
                .ToArray()
        };
}
