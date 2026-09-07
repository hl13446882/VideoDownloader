using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

public sealed class NetworkEventNormalizer : INetworkEventNormalizer
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dedupCache = new();
    private readonly int _maxCacheEntries;
    private readonly TimeSpan _cacheTtl;

    public NetworkEventNormalizer(int maxCacheEntries = 5000, TimeSpan? cacheTtl = null)
    {
        _maxCacheEntries = maxCacheEntries;
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(10);
    }

    public void Clear() => _dedupCache.Clear();

    public ValueTask<NormalizedNetworkEvent?> NormalizeAsync(
        RawNetworkEvent input,
        CancellationToken cancellationToken)
    {
        if (input.Url.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<NormalizedNetworkEvent?>(null);

        EvictExpiredEntries();

        var fingerprint = BuildFingerprint(input);
        var now = DateTimeOffset.UtcNow;
        if (_dedupCache.TryGetValue(fingerprint, out var seenAt) && now - seenAt < _cacheTtl)
            return ValueTask.FromResult<NormalizedNetworkEvent?>(null);

        _dedupCache[fingerprint] = now;
        if (_dedupCache.Count > _maxCacheEntries)
            EvictOldest();

        var requestHeaders = NormalizeHeaders(input.RequestHeaders);
        var responseHeaders = NormalizeHeaders(input.ResponseHeaders);
        var context = BuildRequestContext(input, requestHeaders);

        var normalized = new NormalizedNetworkEvent(
            input.Url,
            input.Method.ToUpperInvariant(),
            input.StatusCode,
            input.MimeType,
            input.ContentLength,
            input.ResourceType,
            input.Initiator,
            input.PageUrl,
            input.FrameId,
            requestHeaders,
            responseHeaders,
            context,
            input.Timestamp,
            input.Source) { SessionId = input.SessionId };

        return ValueTask.FromResult<NormalizedNetworkEvent?>(normalized);
    }

    private static RequestContext BuildRequestContext(
        RawNetworkEvent input,
        IReadOnlyDictionary<string, string> requestHeaders)
    {
        requestHeaders.TryGetValue("referer", out var referer);
        requestHeaders.TryGetValue("origin", out var origin);
        requestHeaders.TryGetValue("user-agent", out var userAgent);

        var safeHeaders = requestHeaders
            .Where(h => !IsSensitiveHeader(h.Key))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

        return new RequestContext(
            Guid.NewGuid(),
            1,
            referer,
            origin,
            userAgent,
            safeHeaders,
            Array.Empty<BrowserCookie>(),
            DateTimeOffset.UtcNow);
    }

    private static bool IsSensitiveHeader(string name) =>
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    private static string BuildFingerprint(RawNetworkEvent input)
    {
        var sb = new StringBuilder();
        sb.Append(input.SessionId);
        sb.Append(NormalizeUrl(input.Url));
        sb.Append('|');
        sb.Append(input.Method.ToUpperInvariant());
        sb.Append('|');
        sb.Append(input.FrameId ?? string.Empty);
        sb.Append('|');
        sb.Append(input.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        sb.Append('|');
        sb.Append(input.Source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string NormalizeUrl(Uri url)
    {
        var builder = new UriBuilder(url) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static Dictionary<string, string> NormalizeHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        return headers
            .GroupBy(h => h.Key.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
    }

    private void EvictExpiredEntries()
    {
        var cutoff = DateTimeOffset.UtcNow - _cacheTtl;
        foreach (var key in _dedupCache.Where(p => p.Value < cutoff).Select(p => p.Key).ToList())
            _dedupCache.TryRemove(key, out _);
    }

    private void EvictOldest()
    {
        foreach (var key in _dedupCache.OrderBy(p => p.Value).Take(_maxCacheEntries / 10).Select(p => p.Key))
            _dedupCache.TryRemove(key, out _);
    }
}
