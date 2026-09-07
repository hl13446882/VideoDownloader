using System.Text.Json;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Json;

namespace VideoDownloader.Infrastructure.Sites;

public sealed class BilibiliSiteAdapter : ISiteAdapter
{
    private readonly IExternalSiteResolver? _external;

    public BilibiliSiteAdapter(IEnumerable<IExternalSiteResolver> externals)
    {
        _external = (externals ?? []).FirstOrDefault(e => e.SupportsSite(SiteIds.Bilibili));
    }

    public string SiteId => SiteIds.Bilibili;
    public int Priority => 100;

    public bool CanHandle(Uri pageUrl) =>
        pageUrl.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase);

    public async Task<SiteProbeResult> ProbeAsync(SiteProbeContext context, CancellationToken ct)
    {
        var contentId = SiteNetworkHelper.ExtractBilibiliContentId(context.PageUrl);
        var title = context.PageTitle ?? contentId ?? "Bilibili Video";

        if (_external is { IsAvailable: true })
        {
            var external = await _external.ResolveAsync(context.PageUrl, context.RequestContext, ct);
            if (external.Count > 0)
            {
                return new SiteProbeResult(
                    SiteProbeStatus.Success,
                    SiteIds.Bilibili,
                    external,
                    AllowGenericFallback: true,
                    ErrorCode: null);
            }
        }

        var variants = new List<MediaVariant>();

        TryParsePlayInfo(context.PageScriptJson, context, variants);
        TryParseNetworkTracks(context, variants);

        if (variants.Count == 0)
        {
            return new SiteProbeResult(
                SiteProbeStatus.RecoverableFailure,
                SiteIds.Bilibili,
                [],
                AllowGenericFallback: true,
                ErrorCode: "BILIBILI_NO_STREAM");
        }

        var grouped = GroupByQuality(variants);
        var metadata = ExtractMetadata(context);
        var video = SiteVideoBuilder.Build(
            SiteIds.Bilibili,
            contentId,
            title,
            context.PageUrl,
            MediaFamily.Dash,
            grouped,
            false,
            ProbeSource.SiteAdapter,
            metadata: metadata);

        return new SiteProbeResult(
            SiteProbeStatus.Success,
            SiteIds.Bilibili,
            [video],
            AllowGenericFallback: true,
            ErrorCode: null);
    }

    private static void TryParsePlayInfo(
        string? scriptJson,
        SiteProbeContext context,
        List<MediaVariant> variants)
    {
        using var doc = SiteNetworkHelper.TryParseScriptJson(scriptJson);
        if (doc is null)
            return;

        if (!doc.RootElement.TryGetProperty("playinfo", out var playinfo))
            return;

        if (!playinfo.TryGetProperty("dash", out var dash))
            return;

        MediaTrack? bestVideo = null;
        MediaTrack? bestAudio = null;

        if (dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in videos.EnumerateArray())
            {
                string? url = null;
                if (v.TryGetProperty("baseUrl", out var baseUrlEl))
                    url = baseUrlEl.GetString();
                else if (v.TryGetProperty("base_url", out var baseUrlSnake))
                    url = baseUrlSnake.GetString();

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var height = JsonNumber.TryInt32Prop(v, "height", out var hv) ? hv : (int?)null;
                var bw = JsonNumber.TryInt64Prop(v, "bandwidth", out var bv) ? bv : (long?)null;
                var codec = v.TryGetProperty("codecs", out var c) ? c.GetString() : null;

                var track = new MediaTrack(
                    $"v{height ?? 0}",
                    MediaTrackKind.Video,
                    new Uri(url),
                    codec,
                    "dash",
                    bw,
                    null,
                    context.RequestContext);

                if (bestVideo is null || (height ?? 0) > (ParseHeight(bestVideo.TrackId) ?? 0))
                    bestVideo = track;
            }
        }

        if (dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in audios.EnumerateArray())
            {
                string? url = null;
                if (a.TryGetProperty("baseUrl", out var baseUrlEl))
                    url = baseUrlEl.GetString();
                else if (a.TryGetProperty("base_url", out var baseUrlSnake))
                    url = baseUrlSnake.GetString();

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var bw = JsonNumber.TryInt64Prop(a, "bandwidth", out var bv) ? bv : (long?)null;
                var codec = a.TryGetProperty("codecs", out var c) ? c.GetString() : null;

                var track = new MediaTrack(
                    "audio",
                    MediaTrackKind.Audio,
                    new Uri(url),
                    codec,
                    "dash",
                    bw,
                    null,
                    context.RequestContext);

                if (bestAudio is null || (bw ?? 0) > (bestAudio.Bandwidth ?? 0))
                    bestAudio = track;
            }
        }

        if (bestVideo is not null && bestAudio is not null)
        {
            var height = ParseHeight(bestVideo.TrackId);
            variants.Add(MediaVariant.FromTracks(
                height is not null ? $"{height}p" : "default",
                null,
                height,
                (bestVideo.Bandwidth ?? 0) + (bestAudio.Bandwidth ?? 0),
                "dash",
                [bestVideo, bestAudio]));
        }
    }

    private static void TryParseNetworkTracks(SiteProbeContext context, List<MediaVariant> variants)
    {
        if (variants.Count > 0)
            return;

        var videoUrls = context.RecentNetworkEvents
            .Where(e => SiteNetworkHelper.IsBilibiliHost(e.Url) &&
                        e.Url.AbsolutePath.Contains(".m4s", StringComparison.OrdinalIgnoreCase) &&
                        !e.Url.AbsolutePath.Contains("-30216", StringComparison.Ordinal))
            .OrderByDescending(e => e.ContentLength ?? 0)
            .Take(3)
            .ToList();

        var audioUrls = context.RecentNetworkEvents
            .Where(e => SiteNetworkHelper.IsBilibiliHost(e.Url) &&
                        e.Url.AbsolutePath.Contains("30216", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ContentLength ?? 0)
            .Take(1)
            .ToList();

        if (videoUrls.Count == 0)
            return;

        var videoTrack = new MediaTrack(
            "video",
            MediaTrackKind.Video,
            videoUrls[0].Url,
            null,
            "dash",
            null,
            videoUrls[0].ContentLength,
            context.RequestContext);

        var tracks = new List<MediaTrack> { videoTrack };
        if (audioUrls.Count > 0)
        {
            tracks.Add(new MediaTrack(
                "audio",
                MediaTrackKind.Audio,
                audioUrls[0].Url,
                null,
                "dash",
                null,
                audioUrls[0].ContentLength,
                context.RequestContext));
        }

        variants.Add(MediaVariant.FromTracks(
            "network",
            null,
            null,
            null,
            "dash",
            tracks));
    }

    private static List<MediaVariant> GroupByQuality(List<MediaVariant> variants) =>
        variants.GroupBy(v => v.VariantId).Select(g => g.First()).ToList();

    private static int? ParseHeight(string trackId)
    {
        var m = System.Text.RegularExpressions.Regex.Match(trackId, @"v(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var h) ? h : null;
    }

    private static IReadOnlyDictionary<string, string> ExtractMetadata(SiteProbeContext context)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = SiteNetworkHelper.TryParseScriptJson(context.PageScriptJson);
        if (doc is not null)
        {
            AddString(metadata, doc.RootElement, "author");
            AddString(metadata, doc.RootElement, "description");
            AddString(metadata, doc.RootElement, "ogTitle");
        }

        return metadata;
    }

    private static void AddString(Dictionary<string, string> metadata, JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            metadata[key] = value.GetString()!;
        }
    }
}
