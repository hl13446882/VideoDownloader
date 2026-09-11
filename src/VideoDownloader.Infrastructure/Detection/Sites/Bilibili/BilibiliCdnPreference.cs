namespace VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

/// <summary>
/// Bilibili-only CDN ranking. Domestic upos/bilivideo hosts are preferred over Akamai
/// (<c>os=akam</c> / <c>akamaized.net</c>), which frequently reset large m4s transfers.
/// Not used by other exclusive detectors.
/// </summary>
internal static class BilibiliCdnPreference
{
    public static bool IsFragile(Uri url)
    {
        var host = url.Host;
        if (host.Contains("akamai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("mirrorakam", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasQueryToken(url, "os=akam");
    }

    public static int Score(Uri url)
    {
        if (IsFragile(url))
            return -100;

        var host = url.Host;
        if (host.Contains("bilivideo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (host.Contains("mirrorbos", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=bos"))
                return 90;
            if (host.Contains("mirrorcos", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=cos"))
                return 85;
            if (host.Contains("mirrorhw", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=hw"))
                return 80;
            if (host.Contains("mirrorws", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=ws"))
                return 75;
            if (host.Contains("mirrorali", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=ali"))
                return 70;
            return 60;
        }

        if (host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase))
            return 40;
        if (host.Contains("upos", StringComparison.OrdinalIgnoreCase))
            return 20;
        return 0;
    }

    /// <summary>Same DASH object across CDNs (e.g. <c>41747222317-1-30080.m4s</c>).</summary>
    public static string ObjectKey(Uri url)
    {
        var name = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? url.AbsolutePath : name;
    }

    public static bool IsMediaHost(Uri url)
    {
        var host = url.Host;
        return host.Contains("bilivideo", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("akamaized.net", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("upos", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bilibili CDNs report Content-Range totals that jitter by a few KB for the same m4s.
    /// Aligned 206 resumes must keep that prefix instead of restarting from zero.
    /// </summary>
    public static bool CanKeepAlignedResume(Uri url, long priorTotal, long newTotal)
    {
        if (!IsMediaHost(url))
            return false;
        if (priorTotal <= 0 || newTotal <= 0)
            return false;
        return Math.Abs(priorTotal - newTotal) <= 64 * 1024;
    }

    private static bool HasQueryToken(Uri url, string token) =>
        url.Query.Contains(token, StringComparison.OrdinalIgnoreCase);
}
