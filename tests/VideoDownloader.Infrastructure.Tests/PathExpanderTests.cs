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
}
