namespace VideoDownloader.Infrastructure.Detection.Sites.YouTube;

/// <summary>Session retries within one ranked address method; never changes method order.</summary>
internal sealed class YouTubeSessionConditions
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _lastSuccessUsedCookies = new(StringComparer.Ordinal);

    internal IReadOnlyList<string?> Order(string method, string? cookieFile)
    {
        if (cookieFile is null) return [null];
        lock (_gate)
        {
            // Public videos work anonymously; avoid waiting on a stale authenticated session first.
            return _lastSuccessUsedCookies.TryGetValue(method, out var cookies) && cookies
                ? [cookieFile, null] : [null, cookieFile];
        }
    }

    internal void RecordSuccess(string method, bool usedCookies)
    {
        lock (_gate) _lastSuccessUsedCookies[method] = usedCookies;
    }
}
