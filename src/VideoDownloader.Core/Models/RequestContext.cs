namespace VideoDownloader.Core.Models;

public sealed record RequestContext(
    Guid ContextId,
    int Version,
    string? Referer,
    string? Origin,
    string? UserAgent,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<BrowserCookie> Cookies,
    DateTimeOffset CapturedAt)
{
    public static RequestContext CreateEmpty() => new(
        Guid.NewGuid(),
        1,
        null,
        null,
        null,
        new Dictionary<string, string>(),
        Array.Empty<BrowserCookie>(),
        DateTimeOffset.UtcNow);
}
