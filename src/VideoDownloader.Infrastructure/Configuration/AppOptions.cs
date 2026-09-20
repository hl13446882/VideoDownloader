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
    public UpdateOptions Update { get; set; } = new();
    public UiOptions Ui { get; set; } = new();
    public SubtitleOptions Subtitles { get; set; } = new();
}

public sealed class UiOptions
{
    /// <summary>zh-CN or en</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>When true, the download queue is grouped by site-folder domain.</summary>
    public bool QueueGrouped { get; set; }

    /// <summary>Last used auto-mode session duration in minutes (positive integer). Fixed wall clock from Start.</summary>
    public int AutoModeIdleMinutes { get; set; } = 5;
}

public sealed class UpdateOptions
{
    /// <summary>Defaults to the licensing web host (same server as license check).</summary>
    public string Endpoint { get; set; } = "http://141.164.40.70:11111";
    public string Channel { get; set; } = "beta";
    public bool Enabled { get; set; } = true;
    public bool RequireFullLicense { get; set; } = true;
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

    /// <summary>Max simultaneous download jobs (UI allows 1–5).</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int RetryCount { get; set; } = 3;
    /// <summary>On startup only: auto-resume interrupted/paused downloads up to MaxConcurrentDownloads.</summary>
    public bool AutoRecoverDownloads { get; set; } = false;

    /// <summary>
    /// Seconds between automatic Resume attempts for Failed queue items. 0 disables.
    /// </summary>
    public int FailedRetryIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Parallel Range connections for YouTube/googlevideo progressive objects. 1 disables.
    /// </summary>
    public int ParallelConnections { get; set; } = 8;

    /// <summary>Minimum object size before multi-connection Range download kicks in.</summary>
    public long ParallelMinBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>When true, multi-track FFmpeg jobs download video and audio tracks concurrently.</summary>
    public bool ParallelAudioVideoTracks { get; set; } = true;

    /// <summary>
    /// When true and both sides have <c>content_prefix_hash</c>, URL renew requires a hash match
    /// (mismatch forces restart). Default off — size + content identity is the primary trust path.
    /// </summary>
    public bool VerifyPrefixHash { get; set; }

    /// <summary>Bytes hashed into <c>content_prefix_hash</c> when prefix hashing is enabled.</summary>
    public int PrefixHashBytes { get; set; } = 64 * 1024;
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
    /// <summary>
    /// Manifest / package paths are always relative to the install root, e.g.
    /// <c>VideoBrowser.exe</c>, <c>app/main/VideoDownloader.exe</c>, <c>app/ffmpeg/ffmpeg.exe</c>.
    /// InstallRoot and APPDIR must be derived by matching these relative anchors — never by
    /// guessing parents of <see cref="AppContext.BaseDirectory"/> (single-file extracts live under %TEMP%\.net).
    /// </summary>
    public static readonly string AppDirectoryRelative = "app";

    /// <summary>Longest-first host / launcher files relative to install root.</summary>
    public static readonly string[] PackageExecutableRelativePaths =
    [
        "app/main/VideoDownloader.exe",
        "app/VideoDownloader.exe",
        "VideoBrowser.exe",
        "VideoDownloader.exe"
    ];

    /// <summary>Directory suffixes (under install root) that may appear as ProcessDir / BaseDirectory.</summary>
    public static readonly string[] PackageDirectoryRelativePaths =
    [
        "app/main",
        "app"
    ];

    public static string Expand(string path)
    {
        var appDir = ResolveAppDirectory();
        return path
            .Replace("%APPDIR%", appDir, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>&lt;installRoot&gt;\app</c> — ffmpeg, yt-dlp, M3u8, and (legacy) flat host live here.
    /// Side-by-side host DLLs live under <c>app\main\</c>; that folder is never APPDIR.
    /// </summary>
    public static string ResolveAppDirectory()
    {
        var root = ResolveInstallRoot();
        if (IsPackageInstallRoot(root))
            return TrimDir(Path.Combine(root, AppDirectoryRelative));

        // Unpackaged / unit-test host: tools may sit beside the content root.
        if (LooksLikeAppInstallDirectory(root))
            return TrimDir(root);

        var app = Path.Combine(root, AppDirectoryRelative);
        if (Directory.Exists(app))
            return TrimDir(app);

        return TrimDir(root);
    }

    /// <summary>
    /// Package root containing <c>VideoBrowser.exe</c> and the <c>app\</c> tree.
    /// Resolved only via relative package anchors from the real process path.
    /// </summary>
    public static string ResolveInstallRoot()
    {
        if (TryResolveInstallRootFromAbsolutePath(Environment.ProcessPath, out var fromProcess))
            return fromProcess;

        // BaseDirectory may be app\main\ (side-by-side) — still strip relative suffix.
        // Never accept a bare %TEMP%\.net extract as the root.
        if (TryResolveInstallRootFromAbsolutePath(AppContext.BaseDirectory, out var fromBase) &&
            !IsSingleFileExtractDirectory(fromBase))
            return fromBase;

        // Walk ancestors of ProcessPath / BaseDirectory and accept only dirs that own relative package files.
        foreach (var start in EnumerateProbeStarts())
        {
            if (TryFindInstallRootByWalkingRelativeAnchors(start, out var walked))
                return walked;
        }

        // Last resort: still refuse .net extract; prefer parent-of-app when BaseDirectory ends with \app or \app\main.
        if (TryStripKnownRelativeDirectory(AppContext.BaseDirectory, out var stripped) &&
            !IsSingleFileExtractDirectory(stripped) &&
            IsPackageInstallRoot(stripped))
            return stripped;

        // Unpackaged / unit-test host: keep Expand() working, but IsPackageInstallRoot will be false
        // so UpdateService must refuse to Diff against this path.
        var fallback = TrimDir(AppContext.BaseDirectory);
        if (!IsSingleFileExtractDirectory(fallback))
            return fallback;

        throw new InvalidOperationException(
            "Cannot resolve install root: process is under a single-file extract directory (%TEMP%\\.net) " +
            "and no package-relative anchor (VideoBrowser.exe / app/main/VideoDownloader.exe) was found.");
    }

    /// <summary>
    /// Test / diagnostic entry: resolve install root from an absolute exe or directory path
    /// using only package-relative suffixes and relative layout checks.
    /// </summary>
    public static bool TryResolveInstallRootFromAbsolutePath(string? absolutePath, out string installRoot)
    {
        installRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(absolutePath);
        }
        catch
        {
            return false;
        }

        if (IsSingleFileExtractDirectory(full))
            return false;

        if (File.Exists(full))
        {
            if (TryStripKnownRelativeFile(full, out var root) && IsPackageInstallRoot(root))
            {
                installRoot = root;
                return true;
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir) &&
                TryFindInstallRootByWalkingRelativeAnchors(dir, out root))
            {
                installRoot = root;
                return true;
            }

            return false;
        }

        if (Directory.Exists(full) || full.EndsWith(Path.DirectorySeparatorChar) ||
            full.EndsWith(Path.AltDirectorySeparatorChar))
        {
            var dir = TrimDir(full);
            if (TryStripKnownRelativeDirectory(dir, out var root) && IsPackageInstallRoot(root))
            {
                installRoot = root;
                return true;
            }

            if (IsPackageInstallRoot(dir))
            {
                installRoot = TrimDir(dir);
                return true;
            }

            if (TryFindInstallRootByWalkingRelativeAnchors(dir, out root))
            {
                installRoot = root;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateProbeStarts()
    {
        string? processPath = null;
        try { processPath = Environment.ProcessPath; } catch { /* ignore */ }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string full;
            try { full = Path.GetFullPath(processPath); }
            catch { full = processPath; }

            if (!IsSingleFileExtractDirectory(full))
            {
                var dir = File.Exists(full) ? Path.GetDirectoryName(full) : TrimDir(full);
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return dir!;
            }
        }

        var baseDir = TrimDir(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(baseDir) && !IsSingleFileExtractDirectory(baseDir))
            yield return baseDir;
    }

    private static bool TryStripKnownRelativeFile(string absoluteFile, out string installRoot)
    {
        installRoot = string.Empty;
        var normalized = NormalizeSeparators(absoluteFile);
        foreach (var relative in PackageExecutableRelativePaths)
        {
            var suffix = NormalizeSeparators(relative);
            if (!normalized.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Require a directory separator before the relative suffix (unless path == suffix).
            if (normalized.Length > suffix.Length)
            {
                var before = normalized[normalized.Length - suffix.Length - 1];
                if (before != Path.DirectorySeparatorChar)
                    continue;
            }

            var root = normalized[..^suffix.Length].TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root))
                continue;

            installRoot = TrimDir(root);
            return true;
        }

        return false;
    }

    private static bool TryStripKnownRelativeDirectory(string absoluteDir, out string installRoot)
    {
        installRoot = string.Empty;
        var normalized = TrimDir(NormalizeSeparators(absoluteDir));
        foreach (var relative in PackageDirectoryRelativePaths)
        {
            var suffix = NormalizeSeparators(relative);
            if (!normalized.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalized, suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (normalized.Length > suffix.Length)
            {
                var before = normalized[normalized.Length - suffix.Length - 1];
                if (before != Path.DirectorySeparatorChar)
                    continue;
            }

            var root = normalized[..^suffix.Length].TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root))
                continue;

            installRoot = TrimDir(root);
            return true;
        }

        return false;
    }

    private static bool TryFindInstallRootByWalkingRelativeAnchors(string startDirectory, out string installRoot)
    {
        installRoot = string.Empty;
        DirectoryInfo? walk;
        try { walk = new DirectoryInfo(Path.GetFullPath(startDirectory)); }
        catch { return false; }

        for (var i = 0; i < 6 && walk is not null; i++, walk = walk.Parent)
        {
            if (IsSingleFileExtractDirectory(walk.FullName))
                continue;
            if (!IsPackageInstallRoot(walk.FullName))
                continue;
            installRoot = TrimDir(walk.FullName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="directory"/> owns the published relative layout
    /// (launcher and/or <c>app\</c> tree), matching update manifest path prefixes.
    /// </summary>
    public static bool IsPackageInstallRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || IsSingleFileExtractDirectory(directory))
            return false;

        var root = TrimDir(directory);
        if (File.Exists(CombineRelative(root, "VideoBrowser.exe")))
            return true;
        if (File.Exists(CombineRelative(root, "app/main/VideoDownloader.exe")))
            return true;
        if (File.Exists(CombineRelative(root, "app/VideoDownloader.exe")) &&
            (File.Exists(CombineRelative(root, "app/ffmpeg/ffmpeg.exe")) ||
             File.Exists(CombineRelative(root, "app/tools/yt-dlp.exe"))))
            return true;

        // Root-level legacy launcher only (no app\ffmpeg beside it).
        return File.Exists(CombineRelative(root, "VideoDownloader.exe")) &&
               !File.Exists(CombineRelative(root, "ffmpeg/ffmpeg.exe")) &&
               Directory.Exists(CombineRelative(root, "app"));
    }

    /// <summary>True when directory is the <c>app\</c> tools folder (not <c>app\main\</c>).</summary>
    public static bool LooksLikeAppInstallDirectory(string directory)
    {
        if (File.Exists(Path.Combine(directory, "ffmpeg", "ffmpeg.exe")))
            return true;
        if (File.Exists(Path.Combine(directory, "tools", "yt-dlp.exe")))
            return true;

        var leaf = Path.GetFileName(TrimDir(directory));
        if (leaf.Equals("main", StringComparison.OrdinalIgnoreCase))
            return false;

        var exeHere = Path.Combine(directory, "VideoDownloader.exe");
        var exeInMain = Path.Combine(directory, "main", "VideoDownloader.exe");
        return File.Exists(exeHere) && !File.Exists(exeInMain);
    }

    private static string CombineRelative(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool IsSingleFileExtractDirectory(string directory)
    {
        var normalized = NormalizeSeparators(directory);
        var marker = Path.DirectorySeparatorChar + ".net" + Path.DirectorySeparatorChar;
        return normalized.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimDir(string directory) =>
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
