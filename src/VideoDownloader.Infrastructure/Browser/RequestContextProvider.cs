using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Browser;

public sealed class RequestContextProvider : IRequestContextProvider
{
    private readonly BrowserHostLocator _locator;

    public RequestContextProvider(BrowserHostLocator locator) => _locator = locator;

    public RequestContext CaptureCurrentContext(Uri? pageUrl, Uri resourceUrl)
    {
        var host = _locator.Active;
        return host?.CaptureCurrentContext(pageUrl, resourceUrl)
               ?? RequestContext.CreateEmpty();
    }

    public async Task<RequestContext> RefreshContextAsync(
        Uri? pageUrl,
        Uri resourceUrl,
        RequestContext? previousContext,
        CancellationToken ct,
        bool forceCookies = false)
    {
        var host = _locator.Active;
        if (host is null || host.CurrentPageUrl != pageUrl)
            return previousContext ?? RequestContext.CreateEmpty();

        await host.RefreshContextSnapshotAsync(ct, forceCookies);
        var fresh = host.CaptureCurrentContext(pageUrl, resourceUrl);

        if (previousContext is null)
            return fresh;

        return fresh with
        {
            ContextId = previousContext.ContextId,
            Version = previousContext.Version + 1
        };
    }
}

public static class ProtectedDataHelper
{
    public static byte[] Protect(string plainText)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return System.Security.Cryptography.ProtectedData.Protect(
            bytes,
            null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
    }

    public static string Unprotect(byte[] protectedBytes)
    {
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
            protectedBytes,
            null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
