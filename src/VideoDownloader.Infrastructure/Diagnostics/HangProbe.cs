using System.Diagnostics;

namespace VideoDownloader.Infrastructure.Diagnostics;

/// <summary>
/// Fire-and-forget hang localization. Writes only to disk/Debug — never touches the WPF dispatcher.
/// </summary>
public static class HangProbe
{
    private static readonly object Gate = new();
    private static readonly string[] Paths = ResolvePaths();

    public static IReadOnlyList<string> LogPaths => Paths;

    public static void Reset(string? reason = null)
    {
        lock (Gate)
        {
            foreach (var path in Paths)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, $"# hang-probe reset {DateTime.Now:O} {reason}{Environment.NewLine}");
                }
                catch
                {
                    // ignore
                }
            }
        }

        Mark("reset", reason);
    }

    public static void Mark(string stage, string? detail = null)
    {
        var line =
            $"{DateTime.Now:HH:mm:ss.fff} T{Environment.CurrentManagedThreadId} {stage}" +
            (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail) +
            Environment.NewLine;
        lock (Gate)
        {
            foreach (var path in Paths)
            {
                try { File.AppendAllText(path, line); }
                catch { /* ignore */ }
            }
        }

        Debug.WriteLine("[HangProbe] " + line.TrimEnd());
    }

    private static string[] ResolvePaths()
    {
        var list = new List<string>
        {
            Path.Combine(Path.GetTempPath(), "vd-hang-probe.log")
        };

        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var artifacts = Path.Combine(dir.FullName, "artifacts");
                if (Directory.Exists(artifacts) || File.Exists(Path.Combine(dir.FullName, "VideoDownloader.sln")))
                {
                    list.Add(Path.Combine(artifacts, "hang-probe.log"));
                    break;
                }
            }
        }
        catch
        {
            // temp path alone is enough
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
