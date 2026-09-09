using Serilog;
using Serilog.Events;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Diagnostics;

namespace VideoDownloader.Infrastructure.Logging;

/// <summary>
/// Owns app file logging + hang-probe logs: enable/disable and clear.
/// </summary>
public sealed class AppLogService
{
    private readonly AppOptions _options;
    private readonly object _gate = new();

    public AppLogService(AppOptions options)
    {
        _options = options;
    }

    public string LogDirectory
    {
        get
        {
            var path = PathExpander.Expand(_options.Logging.LogPath);
            return Path.GetDirectoryName(path)
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "VideoDownloader",
                       "logs");
        }
    }

    public void ApplyFromOptions()
    {
        lock (_gate)
        {
            HangProbe.SetEnabled(_options.Logging.Enabled);
            RebuildSerilogUnlocked();
        }
    }

    /// <summary>
    /// Deletes every managed log file (Serilog rolling files + HangProbe paths).
    /// Temporarily closes the file sink so deletes succeed on Windows.
    /// </summary>
    public (int deleted, int failed) ClearAllLogs()
    {
        lock (_gate)
        {
            HangProbe.SetEnabled(false);
            Log.CloseAndFlush();
            Log.Logger = new LoggerConfiguration().CreateLogger();

            var deleted = 0;
            var failed = 0;
            foreach (var path in EnumerateManagedLogFiles())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    File.Delete(path);
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }

            HangProbe.SetEnabled(_options.Logging.Enabled);
            RebuildSerilogUnlocked();
            return (deleted, failed);
        }
    }

    private void RebuildSerilogUnlocked()
    {
        var logPath = PathExpander.Expand(_options.Logging.LogPath);
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var level = Enum.TryParse<LogEventLevel>(_options.Logging.MinimumLevel, true, out var parsed)
            ? parsed
            : LogEventLevel.Information;

        Log.CloseAndFlush();

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(_options.Logging.Enabled ? level : LogEventLevel.Fatal)
            .Enrich.FromLogContext();

        if (_options.Logging.Enabled)
        {
            config = config.WriteTo.File(
                new SanitizingTextFormatter(),
                logPath,
                rollingInterval: RollingInterval.Day,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1));
        }

        Log.Logger = config.CreateLogger();
    }

    private IEnumerable<string> EnumerateManagedLogFiles()
    {
        var list = new List<string>();

        try
        {
            var dir = LogDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly))
                    list.Add(file);
                foreach (var file in Directory.EnumerateFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))
                    list.Add(file);
            }
        }
        catch
        {
            // ignore
        }

        foreach (var path in HangProbe.LogPaths)
            list.Add(path);

        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
