using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class PathExpanderTests
{
    [Fact]
    public void Expand_replaces_localappdata_placeholder()
    {
        var expanded = PathExpander.Expand("%LOCALAPPDATA%\\VideoDownloader\\models\\x.bin");
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "models",
            "x.bin");
        Assert.Equal(expected, expanded);
    }

    [Fact]
    public void ResolveAppDirectory_returns_non_empty_path()
    {
        var dir = PathExpander.ResolveAppDirectory();
        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void ResolveInstallRoot_avoids_single_file_extract_directory()
    {
        var root = PathExpander.ResolveInstallRoot();
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Directory.Exists(root));

        var normalized = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = Path.DirectorySeparatorChar + ".net" + Path.DirectorySeparatorChar;
        Assert.DoesNotContain(marker, normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikeAppInstallDirectory_prefers_app_folder_over_main_host()
    {
        var root = Path.Combine(Path.GetTempPath(), "vd-path-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var main = Path.Combine(app, "main");
        try
        {
            Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));
            Directory.CreateDirectory(main);
            File.WriteAllBytes(Path.Combine(app, "ffmpeg", "ffmpeg.exe"), [0]);
            File.WriteAllBytes(Path.Combine(main, "VideoDownloader.exe"), [0]);

            var method = typeof(PathExpander).GetMethod(
                "LooksLikeAppInstallDirectory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            Assert.False((bool)method!.Invoke(null, [main])!);
            Assert.True((bool)method.Invoke(null, [app])!);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
