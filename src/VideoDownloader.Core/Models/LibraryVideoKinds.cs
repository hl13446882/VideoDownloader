namespace VideoDownloader.Core.Models;

/// <summary>User-facing library categories for completed downloads.</summary>
public static class LibraryVideoKinds
{
    public const string Unspecified = "";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("movie", "电影"),
        ("series", "电视剧"),
        ("song", "歌曲"),
        ("short", "小视频"),
        ("variety", "综艺")
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Unspecified;

        var v = value.Trim().ToLowerInvariant();
        return All.Any(x => x.Value == v) ? v : Unspecified;
    }

    public static string LabelOf(string? value)
    {
        var v = Normalize(value);
        if (v == Unspecified)
            return "未分类";
        return All.FirstOrDefault(x => x.Value == v).Label ?? "未分类";
    }

    public static bool IsKnown(string? value) =>
        string.IsNullOrWhiteSpace(value) || All.Any(x => x.Value == Normalize(value));
}
