namespace VideoDownloader.Core.Models;

public sealed record BrowserCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTimeOffset? Expires,
    bool Secure,
    bool HttpOnly);
