namespace VideoDownloader.Core.Errors;

public static class ErrorCodes
{
    public const string NetTimeout = "NET_TIMEOUT";
    public const string Http403 = "HTTP_403";
    public const string Http404 = "HTTP_404";
    public const string RangeMismatch = "RANGE_MISMATCH";
    public const string DiskFull = "DISK_FULL";
    public const string DrmProtected = "DRM_PROTECTED";
    public const string FfmpegNotFound = "FFMPEG_NOT_FOUND";
    public const string FfmpegFailed = "FFMPEG_FAILED";
    public const string MuxInterrupted = "MUX_INTERRUPTED";
    public const string Cancelled = "CANCELLED";
    public const string ContextExpired = "CONTEXT_EXPIRED";
    public const string Range416 = "RANGE_416";
    public const string InvalidSavePath = "INVALID_SAVE_PATH";
    public const string LicenseLimit = "LICENSE_DEMO_LIMIT";
    public const string FileIo = "FILE_IO";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string InvalidFormat = "INVALID_FORMAT";
    public const string Unexpected = "UNEXPECTED_ERROR";
    public const string HumanVerification = "HUMAN_VERIFICATION";
    public const string VideoDenied = "VIDEO_DENIED";
    public const string AudioOnly = "AUDIO_ONLY";
    /// <summary>Ordinary downloader rejected an MSE/partial track that is not a complete progressive source.</summary>
    public const string MseTrackNotDownloadable = "MSE_TRACK_NOT_DOWNLOADABLE";
    /// <summary>HTTP finished but payload is shorter than Content-Range / too small to be a real VOD file.</summary>
    public const string IncompleteDownload = "INCOMPLETE_DOWNLOAD";
}
