namespace VideoDownloader.Core.Sites;

/// <summary>Host matching by exact domain or dot-delimited subdomain, never a bare substring.</summary>
public static class SiteHosts
{
    public static bool Matches(Uri? pageUrl, string domain) =>
        pageUrl is not null && Matches(pageUrl.Host, domain);

    public static bool Matches(string? host, string domain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(domain))
            return false;

        var suffix = domain.Trim().TrimStart('.');
        if (suffix.Length == 0 || host.Length < suffix.Length)
            return false;
        if (host.Length == suffix.Length)
            return host.Equals(suffix, StringComparison.OrdinalIgnoreCase);

        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               host[host.Length - suffix.Length - 1] == '.';
    }

    public static bool MatchesAny(Uri? pageUrl, params string[] domains)
    {
        foreach (var domain in domains)
        {
            if (Matches(pageUrl, domain))
                return true;
        }

        return false;
    }

    public static bool MatchesAny(string? host, params string[] domains)
    {
        foreach (var domain in domains)
        {
            if (Matches(host, domain))
                return true;
        }

        return false;
    }
}
