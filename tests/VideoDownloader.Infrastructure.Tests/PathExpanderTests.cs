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

            Assert.False(PathExpander.LooksLikeAppInstallDirectory(main));
            Assert.True(PathExpander.LooksLikeAppInstallDirectory(app));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryResolveInstallRoot_from_main_host_strips_relative_app_main_exe()
    {
        var package = CreatePackageLayout();
        try
        {
            var host = Path.Combine(package, "app", "main", "VideoDownloader.exe");
            Assert.True(PathExpander.TryResolveInstallRootFromAbsolutePath(host, out var root));
            Assert.Equal(Path.GetFullPath(package), Path.GetFullPath(root));
            Assert.True(PathExpander.IsPackageInstallRoot(root));
        }
        finally
        {
            TryDelete(package);
        }
    }

    [Fact]
    public void TryResolveInstallRoot_from_app_main_directory_strips_relative_suffix()
    {
        var package = CreatePackageLayout();
        try
        {
            var mainDir = Path.Combine(package, "app", "main");
            Assert.True(PathExpander.TryResolveInstallRootFromAbsolutePath(mainDir, out var root));
            Assert.Equal(Path.GetFullPath(package), Path.GetFullPath(root));
        }
        finally
        {
            TryDelete(package);
        }
    }

    [Fact]
    public void TryResolveInstallRoot_from_launcher_is_package_root()
    {
        var package = CreatePackageLayout();
        try
        {
            var launcher = Path.Combine(package, "VideoBrowser.exe");
            Assert.True(PathExpander.TryResolveInstallRootFromAbsolutePath(launcher, out var root));
            Assert.Equal(Path.GetFullPath(package), Path.GetFullPath(root));
        }
        finally
        {
            TryDelete(package);
        }
    }

    [Fact]
    public void TryResolveInstallRoot_rejects_dotnet_extract_path()
    {
        var extract = Path.Combine(
            Path.GetTempPath(),
            ".net",
            "VideoDownloader",
            Guid.NewGuid().ToString("N"),
            "VideoDownloader.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(extract)!);
        File.WriteAllBytes(extract, [0]);
        try
        {
            Assert.False(PathExpander.TryResolveInstallRootFromAbsolutePath(extract, out _));
        }
        finally
        {
            TryDelete(Path.Combine(Path.GetTempPath(), ".net", "VideoDownloader"));
        }
    }

    [Fact]
    public void TryResolveInstallRoot_rejects_app_subdir_as_root()
    {
        var package = CreatePackageLayout();
        try
        {
            var app = Path.Combine(package, "app");
            // app\ itself is not the package root — relative strip / walk must yield package.
            Assert.True(PathExpander.TryResolveInstallRootFromAbsolutePath(app, out var root));
            Assert.Equal(Path.GetFullPath(package), Path.GetFullPath(root));
            Assert.False(PathExpander.IsPackageInstallRoot(app));
        }
        finally
        {
            TryDelete(package);
        }
    }

    [Fact]
    public void ResolveAppDirectory_from_package_layout_is_relative_app()
    {
        var package = CreatePackageLayout();
        try
        {
            var host = Path.Combine(package, "app", "main", "VideoDownloader.exe");
            Assert.True(PathExpander.TryResolveInstallRootFromAbsolutePath(host, out var root));
            var appDir = Path.Combine(root, PathExpander.AppDirectoryRelative);
            Assert.True(Directory.Exists(appDir));
            Assert.True(File.Exists(Path.Combine(appDir, "ffmpeg", "ffmpeg.exe")));
        }
        finally
        {
            TryDelete(package);
        }
    }

    private static string CreatePackageLayout()
    {
        var package = Path.Combine(Path.GetTempPath(), "vd-pkg-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(package, "app");
        var main = Path.Combine(app, "main");
        Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));
        Directory.CreateDirectory(Path.Combine(app, "tools"));
        Directory.CreateDirectory(main);
        File.WriteAllBytes(Path.Combine(package, "VideoBrowser.exe"), [1]);
        File.WriteAllBytes(Path.Combine(app, "VideoDownloader.exe"), [2]);
        File.WriteAllBytes(Path.Combine(main, "VideoDownloader.exe"), [3]);
        File.WriteAllBytes(Path.Combine(app, "ffmpeg", "ffmpeg.exe"), [4]);
        File.WriteAllBytes(Path.Combine(app, "tools", "yt-dlp.exe"), [5]);
        return package;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
