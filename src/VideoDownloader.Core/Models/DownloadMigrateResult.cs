namespace VideoDownloader.Core.Models;

/// <summary>Outcome of migrating completed downloads to a new save root.</summary>
public sealed record DownloadMigrateResult(
    int Moved,
    int Skipped,
    int Failed,
    string? BlockReason = null);
