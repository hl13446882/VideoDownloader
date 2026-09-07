namespace VideoDownloader.Core.Models;

public sealed record MediaResource(
    Guid ResourceId,
    Uri Url,
    MediaType MediaType,
    string? MimeType,
    string Method,
    int? StatusCode,
    long? ContentLength,
    string? ResourceType,
    string? Initiator,
    Uri? PageUrl,
    string? FrameId,
    RequestContext RequestContext,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    DateTimeOffset DetectedAt);
