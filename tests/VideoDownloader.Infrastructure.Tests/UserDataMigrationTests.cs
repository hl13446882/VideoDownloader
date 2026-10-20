using System.Reflection;
using System.Text.Json.Nodes;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Update;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class UserDataMigrationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "vd-data-migration-" + Guid.NewGuid().ToString("N"));
    public UserDataMigrationTests() => Directory.CreateDirectory(_work);
    public void Dispose() => Directory.Delete(_work, true);

    [Fact]
    public void Migration_PreservesOriginalAndRewritesOnlyOwnedPaths()
    {
        var legacy = Path.Combine(_work, "legacy");
        var install = Path.Combine(_work, "install");
        Directory.CreateDirectory(Path.Combine(legacy, "WebView2Data", "Default"));
        File.WriteAllText(Path.Combine(legacy, "WebView2Data", "Default", "Cookies"), "encrypted browser fixture");
        Directory.CreateDirectory(Path.Combine(legacy, "data"));
        File.WriteAllBytes(Path.Combine(legacy, "data", "downloads.db"), [1, 2, 3, 4]);
        var json = new JsonObject {
            ["browser"] = new JsonObject { ["userDataFolder"] = Path.Combine(legacy, "WebView2Data") },
            ["database"] = new JsonObject { ["path"] = Path.Combine(legacy, "data", "downloads.db") },
            ["download"] = new JsonObject { ["defaultSavePath"] = "D:\\Downloads" }
        };
        File.WriteAllText(Path.Combine(legacy, "settings.json"), json.ToJsonString());
        UserDataPaths.Migrate(install, legacy);
        var root = Path.Combine(install, "userdata");
        Assert.Equal("encrypted browser fixture", File.ReadAllText(Path.Combine(root, "browser", "Default", "Cookies")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(root, "data", "downloads.db")));
        Assert.True(File.Exists(Path.Combine(legacy, "WebView2Data", "Default", "Cookies")));
        var migrated = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "settings.json")))!;
        Assert.Equal("%USERDATA%\\browser", migrated["browser"]!["userDataFolder"]!.GetValue<string>());
        Assert.Equal("D:\\Downloads", migrated["download"]!["defaultSavePath"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(root, "data", "downloads.db"), "new data");
        UserDataPaths.Migrate(install, legacy);
        Assert.Equal("new data", File.ReadAllText(Path.Combine(root, "data", "downloads.db")));
    }

    [Fact]
    public void InvalidSettings_DoesNotCommitPartialProfile()
    {
        var legacy = Path.Combine(_work, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "invalid json");
        var install = Path.Combine(_work, "install");
        Assert.ThrowsAny<Exception>(() => UserDataPaths.Migrate(install, legacy));
        Assert.False(Directory.Exists(Path.Combine(install, "userdata")));
        Assert.Equal("invalid json", File.ReadAllText(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void CustomExternalProfile_IsNotSilentlyReplacedByAnEmptyProfile()
    {
        var legacy = Path.Combine(_work, "legacy");
        Directory.CreateDirectory(legacy);
        var json = new JsonObject { ["database"] = new JsonObject { ["path"] = Path.Combine(_work, "external", "jobs.db") } };
        File.WriteAllText(Path.Combine(legacy, "settings.json"), json.ToJsonString());
        var install = Path.Combine(_work, "install");
        Assert.Throws<IOException>(() => UserDataPaths.Migrate(install, legacy));
        Assert.False(Directory.Exists(Path.Combine(install, "userdata")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Theory]
    [InlineData("userdata/settings.json")]
    [InlineData("USERDATA/browser/Default/Cookies")]
    [InlineData("userdata. /data/downloads.db")]
    public void UpdatePaths_CannotTargetUserData(string path)
    {
        var normalize = typeof(UpdateService).GetMethod("NormalizeRelativePath", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(normalize.Invoke(null, [path]));
        Assert.Equal("app/main/VideoDownloader.exe", normalize.Invoke(null, ["app/main/VideoDownloader.exe"]));
    }

    [Fact]
    public void LegacyPrefix_DoesNotCaptureUnrelatedDirectories()
    {
        var legacy = Path.Combine(_work, "legacy");
        var external = Path.Combine(_work, "legacy-other", "models.bin");
        Assert.Equal(external, UserDataPaths.Relocate(external, legacy, Path.Combine(_work, "userdata")));
    }
}
