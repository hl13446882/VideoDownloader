using System.Text.Json;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Persistence;

internal static class RequestContextProtector
{
    private sealed record SecretPayload(
        IReadOnlyList<BrowserCookie> Cookies,
        IReadOnlyDictionary<string, string> SensitiveHeaders);

    public static byte[]? Protect(RequestContext context)
    {
        var sensitiveHeaders = context.Headers
            .Where(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

        if (context.Cookies.Count == 0 && sensitiveHeaders.Count == 0)
            return null;

        var json = JsonSerializer.Serialize(new SecretPayload(context.Cookies, sensitiveHeaders));
        return Browser.ProtectedDataHelper.Protect(json);
    }

    public static RequestContext Restore(RequestContext meta, byte[]? secret)
    {
        if (secret is null || secret.Length == 0)
            return meta;

        try
        {
            var json = Browser.ProtectedDataHelper.Unprotect(secret);
            var payload = JsonSerializer.Deserialize<SecretPayload>(json);
            if (payload is null)
                return meta;

            var headers = new Dictionary<string, string>(meta.Headers, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in payload.SensitiveHeaders)
                headers[key] = value;

            return meta with
            {
                Headers = headers,
                Cookies = payload.Cookies
            };
        }
        catch
        {
            return meta;
        }
    }

    public static RequestContext StripSecrets(RequestContext context)
    {
        var headers = context.Headers
            .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

        return context with
        {
            Headers = headers,
            Cookies = Array.Empty<BrowserCookie>()
        };
    }
}
