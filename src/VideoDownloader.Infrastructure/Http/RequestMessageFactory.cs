using System.Net.Http.Headers;
using System.Net.Http;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using Microsoft.Extensions.Options;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Http;

public sealed class RequestMessageFactory : IRequestMessageFactory
{
    private readonly AppOptions _options;
    public RequestMessageFactory(IOptions<AppOptions>? options = null) => _options = options?.Value ?? new AppOptions();
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection"
    };

    public HttpRequestMessage Create(MediaResource resource, HttpMethod method) =>
        CreateInternal(resource.Url, resource.RequestContext, method);

    public HttpRequestMessage Create(MediaVariant variant, HttpMethod method, Uri url) =>
        CreateInternal(url, variant.RequestContext, method);

    private HttpRequestMessage CreateInternal(Uri url, RequestContext ctx, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, url);
        string? Header(string name) => ctx.Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var userAgent = !string.IsNullOrWhiteSpace(ctx.UserAgent) ? ctx.UserAgent : Header("User-Agent");
        var origin = !string.IsNullOrWhiteSpace(ctx.Origin) ? ctx.Origin : Header("Origin");
        var referrer = !string.IsNullOrWhiteSpace(ctx.Referer) ? ctx.Referer : Header("Referer");

        if (!string.IsNullOrWhiteSpace(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        if (!string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var referer))
            request.Headers.Referrer = referer;

        if (!string.IsNullOrWhiteSpace(origin))
            request.Headers.TryAddWithoutValidation("Origin", origin);

        foreach (var (name, value) in FilterSafeHeaders(ctx.Headers))
            request.Headers.TryAddWithoutValidation(name, value);

        var cookie = _options.Browser.CaptureCookies ? BuildCookieHeader(ctx.Cookies, url) : null;
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
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return header;
        }
    }

    private static string BuildCookieHeader(IReadOnlyList<BrowserCookie> cookies, Uri url)
    {
        if (cookies.Count == 0)
            return string.Empty;

        var host = url.Host;
        var matching = cookies.Where(c => IsCookieDomainMatch(host, c.Domain) &&
            (!c.Secure || url.Scheme == "https") &&
            (c.Expires is null || c.Expires > DateTimeOffset.UtcNow) &&
            (url.AbsolutePath == c.Path || url.AbsolutePath.StartsWith((c.Path ?? "/").TrimEnd('/') + "/", StringComparison.Ordinal))).ToList();

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
