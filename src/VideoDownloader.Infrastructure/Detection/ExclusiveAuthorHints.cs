using System.Text.Json;
using VideoDownloader.Core.Naming;

namespace VideoDownloader.Infrastructure.Detection;

internal static class ExclusiveAuthorHints
{
    public static string? ReadFromObservationJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("author", out var author) &&
                author.ValueKind == JsonValueKind.String)
                return AuthorNameResolver.Normalize(author.GetString());
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    public static string? Merge(string? target, string? incoming) =>
        string.IsNullOrWhiteSpace(incoming) ? target : incoming;

    public static string? MergeFromMetadata(string? target, IReadOnlyDictionary<string, string>? metadata, Uri? pageUrl) =>
        Merge(target, AuthorNameResolver.FromMetadata(metadata, pageUrl));
}
