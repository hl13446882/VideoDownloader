namespace VideoDownloader.Core.Models;

public sealed class DownloadJob
{
    public Guid Id { get; init; }
    public required string DisplayName { get; set; }
    public MediaVariant Variant { get; internal set; } = null!;
    public required string TargetPath { get; set; }
    public Uri? PageUrl { get; init; }
    public DownloadStatus Status { get; internal set; }
    public long DownloadedBytes { get; internal set; }
    public long? TotalBytes { get; internal set; }
    public string? ETag { get; internal set; }
    public string? LastModified { get; internal set; }
    public string? LastErrorCode { get; internal set; }
    public DateTimeOffset UpdatedAt { get; internal set; }
    public DateTimeOffset CreatedAt { get; init; }
}
