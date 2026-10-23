namespace VideoDownloader.Core.Models;

/// <summary>The result delivered by the common page probe, after its publications have been applied.</summary>
public sealed record PageProbeResult(
    Guid SessionId,
    IReadOnlyList<DetectedVideo> Videos,
    string? Error = null)
{
    public IReadOnlyList<PublishedVideoAddress> Addresses { get; init; } = [];
}

/// <summary>The address selected by the common publisher, ready for a consumer to enqueue.</summary>
public sealed record PublishedVideoAddress(DetectedVideo Video, MediaVariant Variant);
