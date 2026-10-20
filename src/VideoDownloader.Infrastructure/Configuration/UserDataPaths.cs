using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoDownloader.Infrastructure.Configuration;

/// <summary>User-owned files live beside the launcher, outside the replaceable app tree.</summary>
public static class UserDataPaths
{
    public static string Root => Path.Combine(PathExpander.ResolveInstallRoot(), "userdata");
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDownloader");

    public static string RelocateLegacyPath(string path) => Relocate(path, LegacyRoot, Root);

    internal static string Relocate(string path, string legacy, string destination)
    {
        var prefix = Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path;
        var relative = path[prefix.Length..];
        if (relative.Equals("WebView2Data", StringComparison.OrdinalIgnoreCase)) relative = "browser";
        else if (relative.StartsWith("WebView2Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            relative = "browser" + relative["WebView2Data".Length..];
        return Path.Combine(destination, relative);
    }

    public static void EnsureMigrated() => Migrate(PathExpander.ResolveInstallRoot(), LegacyRoot);

    // Copy to a sibling staging directory first. The old profile remains untouched for rollback.
    public static void Migrate(string installRoot, string legacyRoot)
    {
        var destination = Path.Combine(Path.GetFullPath(installRoot), "userdata");
        var sourcePrefix = Path.GetFullPath(legacyRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installation must not be inside the legacy user-data directory.");
        if (Directory.Exists(destination)) return; // Never merge over an existing user profile.
        Directory.CreateDirectory(installRoot);
        var staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        if (Directory.Exists(legacyRoot))
        {
            ValidateLegacyLocations(legacyRoot);
            CopyVerified(legacyRoot, staging);
            var browser = Path.Combine(staging, "WebView2Data");
            if (Directory.Exists(browser)) Directory.Move(browser, Path.Combine(staging, "browser"));
            var settings = Path.Combine(staging, "settings.json");
            if (File.Exists(settings))
            {
                var json = JsonNode.Parse(File.ReadAllText(settings)) ?? throw new InvalidDataException("Invalid legacy settings");
                RewritePaths(json, legacyRoot, destination);
                File.WriteAllText(settings, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        File.WriteAllText(Path.Combine(staging, "migration.json"), JsonSerializer.Serialize(new {
            source = Path.GetFullPath(legacyRoot), completedAt = DateTimeOffset.UtcNow, sourcePreserved = true
        }));
        Directory.Move(staging, destination);
    }

    private static void ValidateLegacyLocations(string legacyRoot)
    {
        var settings = Path.Combine(legacyRoot, "settings.json");
        if (!File.Exists(settings)) return;
        var json = JsonNode.Parse(File.ReadAllText(settings)) ?? throw new InvalidDataException("Invalid legacy settings");
        var prefix = Path.GetFullPath(legacyRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (section, key) in new[] { ("browser", "userDataFolder"), ("database", "path") })
        {
            var value = json[section]?[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)) continue;
            value = Environment.ExpandEnvironmentVariables(value);
            if (value.StartsWith("%USERDATA%", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Path.GetFullPath(value).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Automatic migration cannot relocate custom {section} data at {value}. Copy it into userdata before continuing; the original data has not been changed.");
        }
    }

    private static void CopyVerified(string source, string destination)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot migrate linked user-data entry: " + entry.FullName);
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(target);
                CopyVerified(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target, overwrite: false);
                using var original = File.OpenRead(entry.FullName);
                using var copy = File.OpenRead(target);
                if (!System.Security.Cryptography.SHA256.HashData(original).AsSpan().SequenceEqual(
                    System.Security.Cryptography.SHA256.HashData(copy)))
                    throw new IOException("Migration checksum failed: " + entry.FullName);
            }
        }
    }

    private static void RewritePaths(JsonNode node, string legacy, string destination)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    text = text.Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);
                    var relocated = Relocate(text, legacy, destination);
                    if (relocated.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        relocated = "%USERDATA%" + relocated[destination.Length..];
                    obj[key] = relocated;
                }
                else if (obj[key] is { } child) RewritePaths(child, legacy, destination);
            }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) RewritePaths(child, legacy, destination);
    }
}
