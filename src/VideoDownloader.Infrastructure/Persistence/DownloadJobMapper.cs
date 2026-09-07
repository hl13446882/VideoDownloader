using System.Text.Json;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Json;

namespace VideoDownloader.Infrastructure.Persistence;

internal static class DownloadJobMapper
{
    internal sealed record VariantMeta(
        string VariantId,
        int? Width,
        int? Height,
        long? Bandwidth,
        string? Container,
        Guid ContextId,
        int ContextVersion,
        string? Referer,
        string? Origin,
        string? UserAgent,
        List<TrackMeta> Tracks)
    {
        public string? ContentIdentity { get; init; }
        public Uri? RecoveryPageUrl { get; init; }
        public List<StoredAlternative>? Alternatives { get; init; }
    }

    internal sealed record StoredAlternative(string MetaJson, byte[]? Secret);

    internal sealed record TrackMeta(
        string TrackId,
        int Kind,
        string SourceUrl,
        string? Codec,
        string? Container,
        long? Bandwidth,
        long? ContentLength,
        RequestContext? Context = null,
        byte[]? Secret = null);

    public static (string MetaJson, byte[]? Secret) SerializeVariant(MediaVariant variant)
    {
        var primaryContext = variant.RequestContext;
        var stripped = RequestContextProtector.StripSecrets(primaryContext);

        var tracks = variant.Tracks.Select(t => new TrackMeta(
            t.TrackId,
            (int)t.Kind,
            t.SourceUrl.ToString(),
            t.Codec,
            t.Container,
            t.Bandwidth,
            t.ContentLength,
            RequestContextProtector.StripSecrets(t.RequestContext),
            RequestContextProtector.Protect(t.RequestContext))).ToList();

        var meta = JsonSerializer.Serialize(new VariantMeta(
            variant.VariantId,
            variant.Width,
            variant.Height,
            variant.Bandwidth,
            variant.Container,
            stripped.ContextId,
            stripped.Version,
            stripped.Referer,
            stripped.Origin,
            stripped.UserAgent,
            tracks)
        {
            ContentIdentity = variant.ContentIdentity,
            RecoveryPageUrl = variant.RecoveryPageUrl,
            Alternatives = variant.Alternatives.Take(4).Select(v =>
            {
                var saved = SerializeVariant(v with { Alternatives = [] });
                return new StoredAlternative(saved.MetaJson, saved.Secret);
            }).ToList()
        });

        var secret = RequestContextProtector.Protect(primaryContext);
        return (meta, secret);
    }

    public static MediaVariant DeserializeVariant(string sourceUrl, string metaJson, byte[]? secret) =>
        DeserializeVariant(sourceUrl, metaJson, secret, true);

    private static MediaVariant DeserializeVariant(string sourceUrl, string metaJson, byte[]? secret, bool includeAlternatives)
    {
        var meta = JsonSerializer.Deserialize<VariantMeta>(metaJson);
        if (meta is null)
            return LegacyDeserialize(sourceUrl, metaJson, secret);

        var context = RequestContext.CreateEmpty() with
        {
            ContextId = meta.ContextId,
            Version = meta.ContextVersion,
            Referer = meta.Referer,
            Origin = meta.Origin,
            UserAgent = meta.UserAgent
        };
        context = RequestContextProtector.Restore(context, secret);

        var tracks = (meta.Tracks ?? []).Select(t => new MediaTrack(
            t.TrackId,
            (MediaTrackKind)t.Kind,
            new Uri(t.SourceUrl),
            t.Codec,
            t.Container,
            t.Bandwidth,
            t.ContentLength,
            t.Context is null ? context : RequestContextProtector.Restore(t.Context, t.Secret))).ToList();

        if (tracks.Count == 0)
        {
            tracks.Add(new MediaTrack(
                "restored",
                MediaTrackKind.Combined,
                new Uri(sourceUrl),
                null,
                meta.Container,
                meta.Bandwidth,
                null,
                context));
        }

        return MediaVariant.FromTracks(
            meta.VariantId,
            meta.Width,
            meta.Height,
            meta.Bandwidth,
            meta.Container,
            tracks) with
        {
            ContentIdentity = meta.ContentIdentity,
            RecoveryPageUrl = meta.RecoveryPageUrl,
            Alternatives = !includeAlternatives ? [] : (meta.Alternatives ?? []).Take(4)
                .Select(a => DeserializeVariant(sourceUrl, a.MetaJson, a.Secret, false)).ToArray()
        };
    }

    private static MediaVariant LegacyDeserialize(string sourceUrl, string metaJson, byte[]? secret)
    {
        using var doc = JsonDocument.Parse(metaJson);
        var root = doc.RootElement;
        var context = RequestContext.CreateEmpty() with
        {
            ContextId = root.TryGetProperty("ContextId", out var cid) && Guid.TryParse(cid.GetString(), out var g) ? g : Guid.NewGuid(),
            Version = root.TryGetProperty("ContextVersion", out var cv) && JsonNumber.TryInt32(cv, out var cvi) ? cvi : 1,
            Referer = root.TryGetProperty("Referer", out var r) ? r.GetString() : null,
            Origin = root.TryGetProperty("Origin", out var o) ? o.GetString() : null,
            UserAgent = root.TryGetProperty("UserAgent", out var ua) ? ua.GetString() : null
        };
        context = RequestContextProtector.Restore(context, secret);

        return MediaVariant.FromCombinedTrack(
            root.TryGetProperty("VariantId", out var vid) ? vid.GetString() ?? "restored" : "restored",
            new Uri(sourceUrl),
            context,
            JsonNumber.TryInt32Prop(root, "Width", out var wi) ? wi : null,
            JsonNumber.TryInt32Prop(root, "Height", out var hi) ? hi : null,
            JsonNumber.TryInt64Prop(root, "Bandwidth", out var bi) ? bi : null,
            root.TryGetProperty("VideoCodec", out var vc) ? vc.GetString() : null,
            root.TryGetProperty("Container", out var c) ? c.GetString() : null);
    }
}
