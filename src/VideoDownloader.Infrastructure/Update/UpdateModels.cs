using System.Text.Json.Serialization;
using VideoDownloader.Infrastructure.Licensing;

namespace VideoDownloader.Infrastructure.Update;

public sealed class UpdateManifest
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "beta";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("release_notes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("files")]
    public List<UpdateFileEntry> Files { get; set; } = [];
}

public sealed class UpdateFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class UpdateManifestRequest
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "beta";

    [JsonPropertyName("license")]
    public LicenseDocument License { get; set; } = null!;
}

public sealed class UpdateDeltaRequest
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "beta";

    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];

    [JsonPropertyName("license")]
    public LicenseDocument License { get; set; } = null!;
}

public sealed class PendingUpdate
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("zipPath")]
    public string ZipPath { get; set; } = string.Empty;

    [JsonPropertyName("installRoot")]
    public string InstallRoot { get; set; } = string.Empty;

    [JsonPropertyName("changed")]
    public List<string> Changed { get; set; } = [];

    [JsonPropertyName("deletes")]
    public List<string> Deletes { get; set; } = [];
}

public enum UpdateCheckOutcome
{
    SkippedNotLicensed,
    SkippedDisabled,
    UpToDate,
    ReadyToApply,
    Downloaded,
    Forbidden,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? RemoteVersion = null,
    string? Message = null,
    PendingUpdate? Pending = null);
