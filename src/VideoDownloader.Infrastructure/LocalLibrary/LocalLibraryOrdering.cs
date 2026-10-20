using System.Globalization;

namespace VideoDownloader.Infrastructure.LocalLibrary;

internal static class LocalLibraryOrdering
{
    // Numeric collation compares digit runs without integer parsing/overflow (2 before 10).
    internal static readonly StringComparer EpisodeNameComparer = StringComparer.Create(
        CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public static IEnumerable<T> ForAuthor<T>(IEnumerable<T> items, string author,
        Func<T, string?> authorOf, Func<T, string> nameOf) =>
        items.Where(i => string.Equals(authorOf(i)?.Trim() ?? "", author.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(nameOf, EpisodeNameComparer)
            .ThenBy(nameOf, StringComparer.Ordinal);

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
