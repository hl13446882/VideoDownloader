using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

public sealed record RawNetworkEvent(
    NetworkEventSource Source,
    string? RequestId,
    Uri Url,
    string Method,
    int? StatusCode,
    string? MimeType,
    long? ContentLength,
    string? ResourceType,
    string? Initiator,
    Uri? PageUrl,
    string? FrameId,
    IReadOnlyDictionary<string, string> RequestHeaders,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    DateTimeOffset Timestamp)
{
    public Guid SessionId { get; init; }
    public static RawNetworkEvent FromCdp(
        Uri url,
        string method,
        int? statusCode,
        string? mimeType,
        long? contentLength,
        string? resourceType,
        string? initiator,
        Uri? pageUrl,
        string? frameId,
        string? requestId,
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> responseHeaders)
    {
        return new RawNetworkEvent(
            NetworkEventSource.Cdp,
            requestId,
            url,
            method,
            statusCode,
            mimeType,
            contentLength,
            resourceType,
            initiator,
            pageUrl,
            frameId,
            requestHeaders,
            responseHeaders,
            DateTimeOffset.UtcNow);
    }

    public static RawNetworkEvent FromWebResource(
        Uri url,
        string method,
        int? statusCode,
        string? mimeType,
        long? contentLength,
        string? resourceType,
        Uri? pageUrl,
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> responseHeaders)
    {
        return new RawNetworkEvent(
            NetworkEventSource.WebResource,
            null,
            url,
            method,
            statusCode,
            mimeType,
            contentLength,
            resourceType,
            null,
            pageUrl,
            null,
            requestHeaders,
            responseHeaders,
            DateTimeOffset.UtcNow);
    }
}

public sealed record NormalizedNetworkEvent(
    Uri Url,
    string Method,
    int? StatusCode,
    string? MimeType,
    long? ContentLength,
    string? ResourceType,
    string? Initiator,
    Uri? PageUrl,
    string? FrameId,
    IReadOnlyDictionary<string, string> RequestHeaders,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    RequestContext RequestContext,
    DateTimeOffset Timestamp,
    NetworkEventSource PrimarySource)
{
    public Guid SessionId { get; init; }
}
