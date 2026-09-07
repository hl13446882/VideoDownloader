using System.Net.Http.Headers;
using System.Net.Http;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Http;

public sealed class RequestMessageFactory : IRequestMessageFactory
{
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection"
    };

    public HttpRequestMessage Create(MediaResource resource, HttpMethod method) =>
        CreateInternal(resource.Url, resource.RequestContext, method);

    public HttpRequestMessage Create(MediaVariant variant, HttpMethod method, Uri url) =>
        CreateInternal(url, variant.RequestContext, method);

    private static HttpRequestMessage CreateInternal(Uri url, RequestContext ctx, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(ctx.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent);

        if (!string.IsNullOrWhiteSpace(ctx.Referer) && Uri.TryCreate(ctx.Referer, UriKind.Absolute, out var referer))
            request.Headers.Referrer = referer;

        if (!string.IsNullOrWhiteSpace(ctx.Origin))
            request.Headers.TryAddWithoutValidation("Origin", ctx.Origin);

        foreach (var (name, value) in FilterSafeHeaders(ctx.Headers))
            request.Headers.TryAddWithoutValidation(name, value);

        var cookie = BuildCookieHeader(ctx.Cookies, url);
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

        return request;
    }

    private static IEnumerable<KeyValuePair<string, string>> FilterSafeHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (BlockedHeaders.Contains(header.Key))
                continue;
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return header;
        }
    }

    private static string BuildCookieHeader(IReadOnlyList<BrowserCookie> cookies, Uri url)
    {
        if (cookies.Count == 0)
            return string.Empty;

        var host = url.Host;
        var matching = cookies.Where(c => IsCookieDomainMatch(host, c.Domain)).ToList();
        // Page-scoped jars often contain first-party cookies that CDN hosts won't match
        // (e.g. media on a different host). Fall back to the full page cookie set.
        if (matching.Count == 0)
            matching = cookies.ToList();

        return string.Join("; ", matching.Select(c => $"{c.Name}={c.Value}"));
    }

    private static bool IsCookieDomainMatch(string host, string domain)
    {
        var normalized = domain.TrimStart('.');
        return host.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyIfRange(HttpRequestMessage request, string? etag, string? lastModified)
    {
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.IfRange = new RangeConditionHeaderValue('"' + etag.Trim('"') + '"');
        else if (!string.IsNullOrWhiteSpace(lastModified) &&
                 DateTimeOffset.TryParse(lastModified, out var dt))
            request.Headers.IfRange = new RangeConditionHeaderValue(dt);
    }
}
