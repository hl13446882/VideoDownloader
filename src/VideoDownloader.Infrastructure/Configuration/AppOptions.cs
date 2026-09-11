using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Configuration;

public sealed class AppOptions
{
    public DownloadOptions Download { get; set; } = new();
    public BrowserOptions Browser { get; set; } = new();
    public FfmpegOptions Ffmpeg { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public DetectionOptions Detection { get; set; } = new();
    public SitesOptions Sites { get; set; } = new();
    public ExternalResolversOptions ExternalResolvers { get; set; } = new();
    public LicenseOptions License { get; set; } = new();
    public UiOptions Ui { get; set; } = new();
}

public sealed class UiOptions
{
    /// <summary>zh-CN or en</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>When true, the download queue is grouped by site-folder domain.</summary>
    public bool QueueGrouped { get; set; }
}

public sealed class LicenseOptions
{
    public string Endpoint { get; set; } = "http://141.164.40.70:11111";
    public string PublicKeyPem { get; set; } = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEH+2DS0Oz5OMAw8SsBCFx9g2F/Yno
RWLmEZkhTmsrwhdih93oT35psBdn+I4NkmvGxEuwsAv8vhZEsGfaiG2Qbw==
-----END PUBLIC KEY-----
""";
    public int DemoMaxBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class DownloadOptions
{
    public string DefaultSavePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public int MaxConcurrentDownloads { get; set; } = 3;
    public int RetryCount { get; set; } = 3;
    public bool AutoRecoverDownloads { get; set; } = false;

    /// <summary>
    /// Seconds between automatic Resume attempts for Failed queue items. 0 disables.
    /// </summary>
    public int FailedRetryIntervalSeconds { get; set; } = 10;
}

public sealed class BrowserOptions
{
    public string UserDataFolder { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "WebView2Data");

    // Kept opt-in: normal detection does not need to read browser session cookies.
    public bool CaptureCookies { get; set; } = false;
}

public sealed class FfmpegOptions
{
    public string ExecutablePath { get; set; } = "%APPDIR%\\ffmpeg\\ffmpeg.exe";
}

public sealed class DatabaseOptions
{
    public string Path { get; set; } = DefaultPath;

    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "data",
            "downloads.db");
}

public sealed class LoggingOptions
{
    /// <summary>When false, Serilog file sink and HangProbe disk writes are off.</summary>
    public bool Enabled { get; set; }

    public string MinimumLevel { get; set; } = "Information";
    public string LogPath { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "logs",
            "app.log");
}

public sealed class DetectionOptions
{
    public int ChannelCapacity { get; set; } = 2048;
    public int DedupCacheMaxEntries { get; set; } = 5000;
    public int DedupCacheTtlMinutes { get; set; } = 10;
    public long MinDisplayBytes { get; set; } = 64 * 1024;
}

public sealed class SitesOptions
{
    public bool PreferSiteAdapters { get; set; } = true;
    public bool FallbackToGeneric { get; set; } = true;
    public SiteToggleOptions YouTube { get; set; } = new() { Enabled = true, ExternalResolver = "yt-dlp" };
    public SiteToggleOptions Bilibili { get; set; } = new() { Enabled = true };
    public SiteToggleOptions Douyin { get; set; } = new() { Enabled = true };
    public SiteToggleOptions TikTok { get; set; } = new() { Enabled = true, ExternalResolver = "yt-dlp" };

    public bool GetSiteEnabled(string siteId) => siteId switch
    {
        SiteIds.YouTube => YouTube.Enabled,
        SiteIds.Bilibili => Bilibili.Enabled,
        SiteIds.Douyin => Douyin.Enabled,
        SiteIds.TikTok => TikTok.Enabled,
        _ => true
    };
}

public sealed class SiteToggleOptions
{
    public bool Enabled { get; set; } = true;
    public string? ExternalResolver { get; set; }
}

public sealed class ExternalResolversOptions
{
    public bool Enabled { get; set; } = true;
    // Only enable this together with Browser:CaptureCookies when a site explicitly needs it.
    public bool UseBrowserCookies { get; set; } = false;
    public string YtDlpPath { get; set; } = "%APPDIR%\\tools\\yt-dlp.exe";
}

public static class PathExpander
{
    public static string Expand(string path)
    {
        return path
            .Replace("%APPDIR%", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);
    }
}
