namespace VideoDownloader.Core.Naming;

/// <summary>
/// Maps a page URL to a stable save subfolder under the default download root.
/// Uses page site identity — never CDN / media hosts.
/// </summary>
public static class DownloadSiteFolder
{
    public const string Unknown = "unknown";

    public static string Resolve(Uri? pageUrl)
    {
        if (pageUrl is null || string.IsNullOrWhiteSpace(pageUrl.Host))
            return Unknown;

        var host = NormalizeHost(pageUrl.IdnHost);
        if (string.IsNullOrEmpty(host))
            return Unknown;

        // Local folder import: https://local.import/{folderName}/
        if (string.Equals(host, "local.import", StringComparison.OrdinalIgnoreCase))
        {
            var path = pageUrl.AbsolutePath.Trim('/');
            var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(segment))
                return SanitizeImportFolderName("导入");
            return SanitizeImportFolderName(Uri.UnescapeDataString(segment));
        }

        if (IsDouyinFamily(host))
            return "douyin.com";
        if (IsTikTokFamily(host))
            return "tiktok.com";
        if (IsBilibiliFamily(host))
            return "bilibili.com";
        if (IsYouTubeFamily(host))
            return "youtube.com";

        return SanitizeFolderName(ToRegistrableDomain(host));
    }

    public static string CombineSaveDirectory(string rootSaveDir, Uri? pageUrl)
    {
        var folder = Resolve(pageUrl);
        return Path.Combine(rootSaveDir, folder);
    }

    /// <summary>Builds the synthetic page URL used for imported local folders.</summary>
    public static Uri CreateImportPageUrl(string folderName)
    {
        var safe = SanitizeImportFolderName(folderName);
        return new Uri("https://local.import/" + Uri.EscapeDataString(safe) + "/");
    }

    /// <summary>Folder name for imports; empty/invalid falls back to 导入.</summary>
    public static string SanitizeImportFolderName(string? name)
    {
        var cleaned = SanitizeFolderName(string.IsNullOrWhiteSpace(name) ? "导入" : name.Trim());
        return string.Equals(cleaned, Unknown, StringComparison.OrdinalIgnoreCase) ? "导入" : cleaned;
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim().Trim('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        return host;
    }

    private static bool IsDouyinFamily(string host) =>
        host is "douyin.com" or "douyin.cn" or "iesdouyin.com" or "amemv.com" ||
        host.EndsWith(".douyin.com", StringComparison.Ordinal) ||
        host.EndsWith(".douyin.cn", StringComparison.Ordinal) ||
        host.EndsWith(".iesdouyin.com", StringComparison.Ordinal) ||
        host.EndsWith(".amemv.com", StringComparison.Ordinal);

    private static bool IsTikTokFamily(string host) =>
        host is "tiktok.com" or "tiktokv.com" ||
        host.EndsWith(".tiktok.com", StringComparison.Ordinal) ||
        host.EndsWith(".tiktokv.com", StringComparison.Ordinal) ||
        host.Contains("byteoversea", StringComparison.Ordinal);

    private static bool IsBilibiliFamily(string host) =>
        host is "bilibili.com" or "b23.tv" or "biliapi.com" ||
        host.EndsWith(".bilibili.com", StringComparison.Ordinal) ||
        host.EndsWith(".biliapi.com", StringComparison.Ordinal) ||
        host.EndsWith(".b23.tv", StringComparison.Ordinal);

    private static bool IsYouTubeFamily(string host) =>
        host is "youtube.com" or "youtu.be" or "youtube-nocookie.com" ||
        host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
        host.EndsWith(".youtu.be", StringComparison.Ordinal) ||
        host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);

    private static string ToRegistrableDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
            return parts[^2] + "." + parts[^1];
        return host;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
        cleaned = cleaned.Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? Unknown : cleaned;
    }
}
