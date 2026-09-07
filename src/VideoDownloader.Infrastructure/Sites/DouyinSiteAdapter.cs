using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Sites;

public sealed class DouyinSiteAdapter : ISiteAdapter
{
    public string SiteId => SiteIds.Douyin;
    public int Priority => 100;

    public bool CanHandle(Uri pageUrl) =>
        pageUrl.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase);

    public Task<SiteProbeResult> ProbeAsync(SiteProbeContext context, CancellationToken ct)
    {
        var metadata = ExtractMetadata(context);
        var title = ResolveTitle(context, metadata);
        var videos = BuildFromNetwork(context, title, metadata);

        if (videos.Count == 0)
        {
            return Task.FromResult(new SiteProbeResult(
                SiteProbeStatus.RecoverableFailure,
                SiteIds.Douyin,
                [],
                AllowGenericFallback: true,
                ErrorCode: "DOUYIN_NO_STREAM"));
        }

        return Task.FromResult(new SiteProbeResult(
            SiteProbeStatus.Success,
            SiteIds.Douyin,
            videos,
            AllowGenericFallback: true,
            ErrorCode: null));
    }

    private static List<DetectedVideo> BuildFromNetwork(
        SiteProbeContext context,
        string title,
        IReadOnlyDictionary<string, string> metadata)
    {
        var media = context.RecentNetworkEvents
            .Where(e => SiteNetworkHelper.IsDouyinHost(e.Url) &&
                        ((e.ContentLength ?? 0) > 64 * 1024 ||
                         e.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true))
            .GroupBy(e => e.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.ContentLength ?? 0).First())
            .OrderByDescending(e => e.ContentLength ?? 0)
            .Take(12)
            .ToList();

        var videos = new List<DetectedVideo>();
        for (var i = 0; i < media.Count; i++)
        {
            var item = media[i];
            var variant = MediaVariant.FromCombinedTrack(
                "default",
                item.Url,
                context.RequestContext,
                container: "mp4",
                contentLength: item.ContentLength);

            videos.Add(new DetectedVideo(
                DeterministicGuid($"douyin:{item.Url.AbsoluteUri}"),
                SiteIds.Douyin,
                ShortHash(item.Url.AbsoluteUri),
                media.Count == 1 ? title : $"{title} #{i + 1}",
                context.PageUrl,
                MediaFamily.DirectMp4,
                [variant],
                false,
                ProbeSource.SiteAdapter,
                Metadata: metadata));
        }

        return videos;
    }

    private static string ResolveTitle(
        SiteProbeContext context,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
            return description;

        if (metadata.TryGetValue("ogTitle", out var ogTitle) && !string.IsNullOrWhiteSpace(ogTitle))
            return ogTitle;

        return context.PageTitle ?? "Douyin Video";
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

        if (!metadata.ContainsKey("author") && !string.IsNullOrWhiteSpace(context.PageUrl.UserInfo))
            metadata["author"] = context.PageUrl.UserInfo;

        return metadata;
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

    private static string ShortHash(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
