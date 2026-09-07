using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Sites;

public sealed class TikTokSiteAdapter : ISiteAdapter
{
    private readonly IExternalSiteResolver? _external;

    public TikTokSiteAdapter(IEnumerable<IExternalSiteResolver> externals)
    {
        _external = (externals ?? []).FirstOrDefault(e => e.SupportsSite(SiteIds.TikTok));
    }

    public string SiteId => SiteIds.TikTok;
    public int Priority => 100;

    public bool CanHandle(Uri pageUrl) =>
        pageUrl.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);

    public async Task<SiteProbeResult> ProbeAsync(SiteProbeContext context, CancellationToken ct)
    {
        var metadata = ExtractMetadata(context);
        var title = ResolveTitle(context, metadata);

        if (_external is { IsAvailable: true })
        {
            var external = await _external.ResolveAsync(context.PageUrl, context.RequestContext, ct);
            if (external.Count > 0)
            {
                return new SiteProbeResult(
                    SiteProbeStatus.Success,
                    SiteIds.TikTok,
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
                SiteIds.TikTok,
                [],
                AllowGenericFallback: true,
                ErrorCode: "TIKTOK_NO_STREAM");
        }

        var video = SiteVideoBuilder.Build(
            SiteIds.TikTok,
            ExtractTikTokContentId(context.PageUrl),
            title,
            context.PageUrl,
            MediaFamily.DirectMp4,
            variants,
            false,
            ProbeSource.SiteAdapter,
            metadata: metadata);

        return new SiteProbeResult(
            SiteProbeStatus.Success,
            SiteIds.TikTok,
            [video],
            AllowGenericFallback: true,
            ErrorCode: null);
    }

    private static List<MediaVariant> BuildFromNetwork(SiteProbeContext context)
    {
        var media = context.RecentNetworkEvents
            .Where(e => SiteNetworkHelper.IsTikTokHost(e.Url) &&
                        ((e.ContentLength ?? 0) > 64 * 1024 ||
                         e.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(e => e.ContentLength ?? 0)
            .ToList();

        var best = media.FirstOrDefault();
        if (best is null)
            return [];

        return
        [
            MediaVariant.FromCombinedTrack(
                "default",
                best.Url,
                context.RequestContext,
                container: "mp4",
                contentLength: best.ContentLength)
        ];
    }

    private static string ResolveTitle(
        SiteProbeContext context,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
            return description;

        if (metadata.TryGetValue("ogTitle", out var ogTitle) && !string.IsNullOrWhiteSpace(ogTitle))
            return ogTitle;

        return context.PageTitle ?? "TikTok Video";
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

        var user = System.Text.RegularExpressions.Regex.Match(
            context.PageUrl.AbsolutePath,
            @"/@([^/]+)/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (user.Success && !metadata.ContainsKey("uploader"))
            metadata["uploader"] = user.Groups[1].Value;

        return metadata;
    }

    private static string? ExtractTikTokContentId(Uri pageUrl)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath,
            @"/video/([^/?#]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void AddString(Dictionary<string, string> metadata, System.Text.Json.JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            metadata[key] = value.GetString()!;
        }
    }
}
