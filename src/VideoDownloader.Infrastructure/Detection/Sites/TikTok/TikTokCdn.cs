namespace VideoDownloader.Infrastructure.Detection.Sites.TikTok;

/// <summary>
/// TikTok-only CDN / work identity helpers. Not used by Douyin, Bilibili, or YouTube detectors.
/// </summary>
internal static class TikTokCdn
{
    public static bool IsSignedProgressiveHost(Uri url)
    {
        var host = url.Host;
        return host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("web-prime", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("tiktokv.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("muscdn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Browser-session web-prime URLs 403 from HttpClient once the play signature expires.</summary>
    public static bool IsFragilePlayHost(Uri url)
    {
        var host = url.Host;
        return host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("web-prime", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDurablePlayHost(Uri url)
    {
        if (IsFragilePlayHost(url))
            return false;
        var host = url.Host;
        return host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("muscdn", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("tiktokv.com", StringComparison.OrdinalIgnoreCase);
    }

    public static int PlayHostScore(Uri url)
    {
        if (IsDurablePlayHost(url)) return 500;
        if (IsFragilePlayHost(url)) return -400;
        return 0;
    }

    public static int PlayHostScore(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return 0;
        return PlayHostScore(parsed);
    }

    /// <summary>
    /// The WebView tab is still TikTok even when 403 recovery rewrites the page to /@i/video/{id}.
    /// </summary>
    public static bool IsLiveCookieTab(Uri? current, Uri requested)
    {
        if (current is null)
            return false;
        if (!current.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
            !requested.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(current.AbsoluteUri, requested.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsSameWork(current, requested))
            return true;
        return IsFeedShell(current);
    }

    public static bool IsFeedShell(Uri page)
    {
        if (!page.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = page.AbsolutePath.TrimEnd('/');
        return path.Length == 0 ||
               path.Equals("/foryou", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/following", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/explore", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/live", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// For You shells share one exclusive run even when the query token changes.
    /// Detail pages compare the numeric video id.
    /// </summary>
    public static bool IsSameWork(Uri previous, Uri next)
    {
        var left = ExtractVideoId(previous);
        var right = ExtractVideoId(next);
        if (left is not null && right is not null)
            return string.Equals(left, right, StringComparison.Ordinal);
        if (IsFeedShell(previous) && IsFeedShell(next))
            return true;
        return string.Equals(previous.AbsoluteUri, next.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractVideoId(Uri page)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            page.AbsolutePath,
            @"/@[^/]+/video/(?<id>\d{10,})|/video/(?<id>\d{10,})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : null;
    }
}
