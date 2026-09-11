using System.Text.Json;
using System.Text.Json.Serialization;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Configuration;

public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "settings.json");

    public AppOptions Load(AppOptions defaults)
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return Clone(defaults);

            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppOptions>(json, JsonOptions);
            return Merge(defaults, loaded);
        }
        catch
        {
            return Clone(defaults);
        }
    }

    public void Save(AppOptions options)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(options, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static AppOptions Merge(AppOptions defaults, AppOptions? loaded)
    {
        if (loaded is null)
            return Clone(defaults);

        return new AppOptions
        {
            Download = loaded.Download ?? defaults.Download,
            Browser = loaded.Browser ?? defaults.Browser,
            Ffmpeg = defaults.Ffmpeg,
            Database = loaded.Database ?? defaults.Database,
            Logging = loaded.Logging ?? defaults.Logging,
            Detection = loaded.Detection ?? defaults.Detection,
            Sites = loaded.Sites ?? defaults.Sites,
            ExternalResolvers = loaded.ExternalResolvers ?? defaults.ExternalResolvers,
            License = defaults.License,
            Ui = loaded.Ui ?? defaults.Ui
        };
    }

    private static AppOptions Clone(AppOptions source) => new()
    {
        Download = new DownloadOptions
        {
            DefaultSavePath = source.Download.DefaultSavePath,
            MaxConcurrentDownloads = source.Download.MaxConcurrentDownloads,
            RetryCount = source.Download.RetryCount,
            AutoRecoverDownloads = source.Download.AutoRecoverDownloads,
            FailedRetryIntervalSeconds = source.Download.FailedRetryIntervalSeconds,
            ParallelConnections = source.Download.ParallelConnections,
            ParallelMinBytes = source.Download.ParallelMinBytes,
            ParallelAudioVideoTracks = source.Download.ParallelAudioVideoTracks
        },
        Browser = new BrowserOptions
        {
            UserDataFolder = source.Browser.UserDataFolder,
            CaptureCookies = source.Browser.CaptureCookies
        },
        Ffmpeg = new FfmpegOptions { ExecutablePath = source.Ffmpeg.ExecutablePath },
        Database = new DatabaseOptions { Path = source.Database.Path },
        Logging = new LoggingOptions
        {
            Enabled = source.Logging.Enabled,
            MinimumLevel = source.Logging.MinimumLevel,
            LogPath = source.Logging.LogPath
        },
        Detection = new DetectionOptions
        {
            ChannelCapacity = source.Detection.ChannelCapacity,
            DedupCacheMaxEntries = source.Detection.DedupCacheMaxEntries,
            DedupCacheTtlMinutes = source.Detection.DedupCacheTtlMinutes,
            MinDisplayBytes = source.Detection.MinDisplayBytes
        },
        Sites = new SitesOptions
        {
            PreferSiteAdapters = source.Sites.PreferSiteAdapters,
            FallbackToGeneric = source.Sites.FallbackToGeneric,
            YouTube = new SiteToggleOptions
            {
                Enabled = source.Sites.YouTube.Enabled,
                ExternalResolver = source.Sites.YouTube.ExternalResolver
            },
            Bilibili = new SiteToggleOptions { Enabled = source.Sites.Bilibili.Enabled },
            Douyin = new SiteToggleOptions { Enabled = source.Sites.Douyin.Enabled },
            TikTok = new SiteToggleOptions
            {
                Enabled = source.Sites.TikTok.Enabled,
                ExternalResolver = source.Sites.TikTok.ExternalResolver
            }
        },
        ExternalResolvers = new ExternalResolversOptions
        {
            Enabled = source.ExternalResolvers.Enabled,
            UseBrowserCookies = source.ExternalResolvers.UseBrowserCookies,
            YtDlpPath = source.ExternalResolvers.YtDlpPath
        },
        License = new LicenseOptions
        {
            Endpoint = source.License.Endpoint,
            PublicKeyPem = source.License.PublicKeyPem,
            DemoMaxBytes = source.License.DemoMaxBytes
        },
        Ui = new UiOptions
        {
            Language = source.Ui?.Language ?? "zh-CN",
            QueueGrouped = source.Ui?.QueueGrouped ?? false
        }
    };
}
