using System.Text;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Naming;

/// <summary>Normalize author / uploader names from probe metadata and page URLs.</summary>
public static partial class AuthorNameResolver
{
    public const int MaxLength = 128;
    public const string UnknownLabel = "未知";

    private static readonly string[] MetadataKeys =
    [
        "author",
        "uploader",
        "channel",
        "creator",
        "artist"
    ];

    public static string? FromDetectedVideo(DetectedVideo video) =>
        FromMetadata(video.Metadata, video.PageUrl);

    public static string? FromMetadata(IReadOnlyDictionary<string, string>? metadata, Uri? pageUrl)
    {
        if (metadata is not null)
        {
            foreach (var key in MetadataKeys)
            {
                if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    var normalized = Normalize(value);
                    if (normalized is not null)
                        return normalized;
                }
            }
        }

        return FromPageUrl(pageUrl);
    }

    public static string? FromPageUrl(Uri? pageUrl)
    {
        if (pageUrl is null)
            return null;

        var path = pageUrl.AbsolutePath;
        var tikTok = TikTokHandle().Match(path);
        if (tikTok.Success)
            return Normalize(tikTok.Groups[1].Value);

        return null;
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        text = Regex.Replace(text, @"\s{2,}", " ");
        if (text.Length > MaxLength)
            text = text[..MaxLength].TrimEnd();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>UI label when author is missing from the database.</summary>
    public static string DisplayOrUnknown(string? author) =>
        string.IsNullOrWhiteSpace(author) ? UnknownLabel : author.Trim();

    [GeneratedRegex(@"/@([^/?#]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TikTokHandle();
}
