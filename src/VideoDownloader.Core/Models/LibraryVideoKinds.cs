namespace VideoDownloader.Core.Models;

/// <summary>
/// Built-in library category seeds shipped with the app.
/// Runtime source of truth is the local DB (<c>library_kinds</c>); seeds are INSERT OR IGNORE only.
/// </summary>
public static class LibraryVideoKinds
{
    public const string Unspecified = "";

    /// <summary>UI sentinel for “add category”; never persisted.</summary>
    public const string NewKindSentinel = "__new__";

    public const string UnspecifiedLabel = "未分类";

    /// <summary>Shipped seed rows. Do not treat as the live catalog.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Seed =
    [
        ("movie", "电影"),
        ("series", "电视剧"),
        ("song", "歌曲"),
        ("short", "小视频"),
        ("variety", "综艺")
    ];
}

public sealed record LibraryKindEntry(string Value, string Label, int SortOrder, bool IsSeed);
