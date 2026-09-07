using System.Text.RegularExpressions;

namespace VideoDownloader.Infrastructure.Logging;

public static partial class SanitizedLogger
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie", "Authorization", "Proxy-Authorization", "Set-Cookie"
    };

    private static readonly string[] SensitiveQueryKeys =
    [
        "token", "sign", "signature", "auth", "key", "session", "access_token", "api_key"
    ];

    public static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var result = message;
        foreach (var header in SensitiveHeaderNames)
        {
            result = Regex.Replace(
                result,
                $@"({Regex.Escape(header)}\s*[:=]\s*)([^\r\n]+)",
                "$1[REDACTED]",
                RegexOptions.IgnoreCase);
        }

        result = SensitiveQueryRegex().Replace(result, m =>
        {
            var key = m.Groups[1].Value;
            return $"{key}=[REDACTED]";
        });

        return result;
    }

    public static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = ParseQuery(uri.Query);
        var changed = false;
        foreach (var key in query.Keys.ToList())
        {
            if (SensitiveQueryKeys.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                query[key] = "[REDACTED]";
                changed = true;
            }
        }

        if (!changed)
            return url;

        var builder = new UriBuilder(uri) { Query = string.Join("&", query.Select(p => $"{p.Key}={p.Value}")) };
        return builder.Uri.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return result;

        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0)
                result[part] = string.Empty;
            else
                result[part[..idx]] = part[(idx + 1)..];
        }

        return result;
    }

    [GeneratedRegex(@"([?&](?:token|sign|signature|auth|key|session|access_token|api_key)=)([^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveQueryRegex();
}
