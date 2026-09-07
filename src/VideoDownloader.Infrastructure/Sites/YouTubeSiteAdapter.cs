using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Sites;

public sealed class YouTubeSiteAdapter : ISiteAdapter
{
    private readonly IExternalSiteResolver? _external;

    public YouTubeSiteAdapter(IEnumerable<IExternalSiteResolver> externals)
    {
        _external = (externals ?? []).FirstOrDefault(e => e.SupportsSite(SiteIds.YouTube));
    }

    public string SiteId => SiteIds.YouTube;
    public int Priority => 100;

    public bool CanHandle(Uri pageUrl) =>
        pageUrl.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        pageUrl.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    public async Task<SiteProbeResult> ProbeAsync(SiteProbeContext context, CancellationToken ct)
    {
        var contentId = SiteNetworkHelper.ExtractYouTubeVideoId(context.PageUrl);
        var title = context.PageTitle ?? contentId ?? "YouTube Video";

        if (_external is { IsAvailable: true })
        {
            var external = await _external.ResolveAsync(context.PageUrl, context.RequestContext, ct);
            if (external.Count > 0)
            {
                return new SiteProbeResult(
                    SiteProbeStatus.Success,
                    SiteIds.YouTube,
                    external,
                    AllowGenericFallback: true,
                    ErrorCode: null);
            }
        }

        var variants = BuildFromNetwork(context);
        if (variants.Count == 0)
        {
            return new SiteProbeResult(
                SiteProbeStatus.RecoverableFailure,
                SiteIds.YouTube,
                [],
                AllowGenericFallback: true,
                ErrorCode: "YOUTUBE_NO_STREAM");
        }

        var video = SiteVideoBuilder.Build(
            SiteIds.YouTube,
            contentId,
            title,
            context.PageUrl,
            MediaFamily.DirectMp4,
            variants,
            false,
            ProbeSource.SiteAdapter);

        return new SiteProbeResult(
            SiteProbeStatus.Success,
            SiteIds.YouTube,
            [video],
            AllowGenericFallback: true,
            ErrorCode: null);
    }

    private static List<MediaVariant> BuildFromNetwork(SiteProbeContext context)
    {
        var playback = context.RecentNetworkEvents
            .Where(e => e.Url.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
                        e.Url.AbsolutePath.Contains("videoplayback", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var videoEvents = playback
            .Where(e => e.Url.Query.Contains("mime=video", StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Query.Contains("itag=137", StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Query.Contains("itag=248", StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Query.Contains("itag=399", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ContentLength ?? 0)
            .ToList();

        var audioEvents = playback
            .Where(e => e.Url.Query.Contains("mime=audio", StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Query.Contains("itag=140", StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Query.Contains("itag=251", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ContentLength ?? 0)
            .ToList();

        if (videoEvents.Count == 0 && playback.Count > 0)
        {
            var combined = playback
                .Where(e => (e.ContentLength ?? 0) > 256 * 1024)
                .OrderByDescending(e => e.ContentLength ?? 0)
                .FirstOrDefault();

            if (combined is not null)
            {
                return
                [
                    MediaVariant.FromCombinedTrack(
                        "default",
                        combined.Url,
                        context.RequestContext,
                        container: "mp4",
                        contentLength: combined.ContentLength)
                ];
            }
        }

        if (videoEvents.Count == 0)
            return [];

        var bestVideo = videoEvents[0];
        var tracks = new List<MediaTrack>
        {
            new(
                "video",
                MediaTrackKind.Video,
                bestVideo.Url,
                null,
                "mp4",
                null,
                bestVideo.ContentLength,
                context.RequestContext)
        };

        if (audioEvents.Count > 0)
        {
            tracks.Add(new MediaTrack(
                "audio",
                MediaTrackKind.Audio,
                audioEvents[0].Url,
                null,
                "mp4",
                null,
                audioEvents[0].ContentLength,
                context.RequestContext));
        }

        return tracks.Count == 1
            ? [MediaVariant.FromCombinedTrack("default", tracks[0].SourceUrl, context.RequestContext, container: "mp4", contentLength: tracks[0].ContentLength)]
            : [MediaVariant.FromTracks("1080p", null, 1080, null, "mp4", tracks)];
    }
}
