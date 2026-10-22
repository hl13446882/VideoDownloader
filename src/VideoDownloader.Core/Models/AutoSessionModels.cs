namespace VideoDownloader.Core.Models;

public enum AutoSessionStatus
{
    Running = 0,
    Paused = 1,
    Stopped = 2,
    Completed = 3,
    Failed = 4
}

public enum AutoQueueItemStatus
{
    Queued = 0,
    Enqueued = 1,
    Downloading = 2,
    Completed = 3,
    Failed = 4,
    Skipped = 5,
    Cancelled = 6
}

public sealed class AutoSession
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public AutoSessionStatus Status { get; set; }
    public string? Site { get; set; }
    public int MaxVideos { get; set; }
    public int IdleMinutes { get; set; }
    public int EnqueuedCount { get; set; }
    public string? TabHint { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AutoQueueItem
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public int Seq { get; set; }
    public required string PageUrl { get; set; }
    public string? ContentId { get; set; }
    public string? Caption { get; set; }
    public string? Author { get; set; }
    public required string SourceUrl { get; set; }
    public required string VariantMetaJson { get; set; }
    public byte[]? VariantSecret { get; set; }
    public string? SelectedVariantId { get; set; }
    public string? Fingerprint { get; set; }
    public AutoQueueItemStatus Status { get; set; }
    public Guid? DownloadJobId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset ProbedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
