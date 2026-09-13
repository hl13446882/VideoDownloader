using System.Globalization;

namespace VideoDownloader.Infrastructure.LocalLibrary;

internal static class LocalLibraryOrdering
{
    /// <summary>Empty authors sort last in ascending author order.</summary>
    public static string AuthorSortKey(string? author) =>
        string.IsNullOrWhiteSpace(author)
            ? "\uFFFF"
            : author.Trim();

    public static IOrderedEnumerable<T> OrderByAuthorThenDownloadedAtDesc<T>(
        IEnumerable<T> items,
        Func<T, string?> author,
        Func<T, DateTimeOffset> downloadedAt)
    {
        return items
            .OrderBy(i => AuthorSortKey(author(i)), StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(i => downloadedAt(i));
    }
}
