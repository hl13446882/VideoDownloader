namespace VideoDownloader.Core.Models;

public sealed class DownloadJob
{
    public Guid Id { get; init; }
    public required string DisplayName { get; set; }

    /// <summary>Full caption / title at enqueue (not filename-clamped).</summary>
    public string? Caption { get; set; }

    /// <summary>Uploader / channel / nickname at enqueue (immutable after create).</summary>
    public string? Author { get; set; }

    /// <summary>Optional library category: movie / series / song / short / variety.</summary>
    public string? VideoKind { get; set; }

    /// <summary>Content duration in seconds when known at enqueue or later probe.</summary>
    public double? DurationSec { get; set; }

    public MediaVariant Variant { get; internal set; } = null!;
    public required string TargetPath { get; set; }
    public Uri? PageUrl { get; init; }
    public DownloadStatus Status { get; internal set; }
    public long DownloadedBytes { get; internal set; }
    public long? TotalBytes { get; internal set; }

    /// <summary>
    /// Trust anchor for resume-after-URL-renew: first confirmed object size.
    /// Session <see cref="TotalBytes"/> may jitter with CDN Content-Range.
    /// </summary>
    public long? ExpectedTotalBytes { get; internal set; }

    /// <summary>Optional prefix hash (<c>sha256:hex</c>) of the first N downloaded bytes.</summary>
    public string? ContentPrefixHash { get; internal set; }

    public string? ETag { get; internal set; }
    public string? LastModified { get; internal set; }
    public string? LastErrorCode { get; internal set; }
    public DateTimeOffset UpdatedAt { get; internal set; }

    /// <summary>
    /// Library download time. Set on first complete; refreshed when enqueue dedupes to this job
    /// (same durable object already on disk) so time-sort moves it to now.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Last library/queue rename or metadata edit; does not affect download-time sort.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>True when sequential subtitle recognition has finished for this completed file.</summary>
    public bool SubtitleRecognized { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
