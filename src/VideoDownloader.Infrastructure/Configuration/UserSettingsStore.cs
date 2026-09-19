using System.Text.Json;
using System.Text.Json.Serialization;
using VideoDownloader.Core.Subtitles;

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
        Sanitize(options);
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(options, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static AppOptions Merge(AppOptions defaults, AppOptions? loaded)
    {
        if (loaded is null)
            return Clone(defaults);

        var merged = new AppOptions
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
            Update = defaults.Update,
            Ui = loaded.Ui ?? defaults.Ui,
            Subtitles = loaded.Subtitles ?? defaults.Subtitles
        };
        Sanitize(merged);
        return merged;
    }

    private static void Sanitize(AppOptions options)
    {
        options.Download.MaxConcurrentDownloads =
            Math.Clamp(options.Download.MaxConcurrentDownloads, 1, 5);
        if (options.Ui.AutoModeIdleMinutes < 1)
            options.Ui.AutoModeIdleMinutes = 5;

        options.Subtitles.PreloadAheadSeconds = Math.Clamp(options.Subtitles.PreloadAheadSeconds, 10, 90);
        options.Subtitles.FontSize = Math.Clamp(options.Subtitles.FontSize, 12, 60);
        options.Subtitles.OutlineSize = Math.Clamp(options.Subtitles.OutlineSize, 0, 8);
        options.Subtitles.BackgroundOpacity = Math.Clamp(options.Subtitles.BackgroundOpacity, 0, 1);
        options.Subtitles.BottomOffsetPx = SnapBottomOffset(options.Subtitles.BottomOffsetPx);
        options.Subtitles.MaxLines = Math.Clamp(options.Subtitles.MaxLines, 1, 4);
        options.Subtitles.MaxWidthPercent = Math.Clamp(options.Subtitles.MaxWidthPercent, 20, 100);
        options.Subtitles.SubtitleOffsetMs = Math.Clamp(options.Subtitles.SubtitleOffsetMs, -5000, 5000);
        options.Subtitles.TextColor = NormalizePresetColor(options.Subtitles.TextColor);
        if (!Enum.IsDefined(typeof(SubtitleMode), options.Subtitles.Mode))
            options.Subtitles.Mode = SubtitleMode.Chinese;
        options.Subtitles.TranslationProvider = string.Equals(
            options.Subtitles.TranslationProvider,
            "cloud",
            StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
    }

    private static readonly int[] BottomOffsetChoices = [20, 40, 60, 80, 100, 120, 140, 160];

    private static int SnapBottomOffset(int value)
    {
        var best = BottomOffsetChoices[0];
        var bestDistance = Math.Abs(value - best);
        foreach (var choice in BottomOffsetChoices)
        {
            var distance = Math.Abs(value - choice);
            if (distance >= bestDistance)
                continue;
            best = choice;
            bestDistance = distance;
        }
        return best;
    }

    private static string NormalizePresetColor(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        return text switch
        {
            "#FF0000" or "RED" or "红" or "红色" => "#FF0000",
            "#000000" or "BLACK" or "黑" or "黑色" => "#000000",
            "#0000FF" or "#0080FF" or "BLUE" or "蓝" or "蓝色" => "#0000FF",
            "#FFFF00" or "YELLOW" or "黄" or "黄色" => "#FFFF00",
            "#00FF00" or "#008000" or "GREEN" or "绿" or "绿色" => "#00FF00",
            _ => "#FFFF00"
        };
    }

    private static AppOptions Clone(AppOptions source)
    {
        var clone = new AppOptions
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
            Update = new UpdateOptions
            {
                Endpoint = source.Update.Endpoint,
                Channel = source.Update.Channel,
                Enabled = source.Update.Enabled,
                RequireFullLicense = source.Update.RequireFullLicense
            },
            Ui = new UiOptions
            {
                Language = source.Ui?.Language ?? "zh-CN",
                QueueGrouped = source.Ui?.QueueGrouped ?? false,
                AutoModeIdleMinutes = source.Ui?.AutoModeIdleMinutes > 0
                    ? source.Ui.AutoModeIdleMinutes
                    : 5
            },
            Subtitles = new SubtitleOptions
            {
                Enabled = source.Subtitles.Enabled,
                Mode = source.Subtitles.Mode,
                PreloadAheadSeconds = source.Subtitles.PreloadAheadSeconds,
                TranslationProvider = source.Subtitles.TranslationProvider,
                LocalTranslationEndpoint = source.Subtitles.LocalTranslationEndpoint,
                LocalTranslationModel = source.Subtitles.LocalTranslationModel,
                WhisperModelPath = source.Subtitles.WhisperModelPath,
                FontFamily = source.Subtitles.FontFamily,
                FontSize = source.Subtitles.FontSize,
                Bold = source.Subtitles.Bold,
                TextColor = source.Subtitles.TextColor,
                OutlineColor = source.Subtitles.OutlineColor,
                OutlineSize = source.Subtitles.OutlineSize,
                BackgroundColor = source.Subtitles.BackgroundColor,
                BackgroundOpacity = source.Subtitles.BackgroundOpacity,
                BottomOffsetPx = source.Subtitles.BottomOffsetPx,
                MaxLines = source.Subtitles.MaxLines,
                MaxWidthPercent = source.Subtitles.MaxWidthPercent,
                SubtitleOffsetMs = source.Subtitles.SubtitleOffsetMs
            }
        };
        Sanitize(clone);
        return clone;
    }
}
