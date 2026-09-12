using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Licensing;

namespace VideoDownloader.Infrastructure.Update;

public sealed class UpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly HttpClient _client;
    private readonly LicenseService _license;
    private readonly AppOptions _options;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateService(
        HttpClient client,
        LicenseService license,
        IOptions<AppOptions> options,
        ILogger<UpdateService> logger)
    {
        _client = client;
        _license = license;
        _options = options.Value;
        _logger = logger;
    }

    public static string UpdatesRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "updates");

    public static string PendingPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "pending-update.json");

    public static string HostedFilesCachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "update-hosted-files.json");

    public static string InstallRoot
    {
        get
        {
            // Main app lives in <installRoot>\app\; launcher is <installRoot>\VideoDownloader.exe.
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(appDir);
            if (parent is not null &&
                File.Exists(Path.Combine(parent.FullName, "VideoDownloader.exe")))
                return parent.FullName;
            return appDir;
        }
    }

    public bool CanUpdate =>
        _options.Update.Enabled &&
        (!_options.Update.RequireFullLicense || (_license.Current.IsFull && _license.Current.IsValid));

    public async Task<UpdateCheckResult> CheckAndPrepareAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Message: "busy");

        try
        {
            if (!_options.Update.Enabled)
                return new UpdateCheckResult(UpdateCheckOutcome.SkippedDisabled);

            if (_options.Update.RequireFullLicense && !(_license.Current.IsFull && _license.Current.IsValid))
            {
                ClearPending();
                return new UpdateCheckResult(UpdateCheckOutcome.SkippedNotLicensed);
            }

            var document = _license.LastDocument;
            if (document is null)
            {
                ClearPending();
                return new UpdateCheckResult(UpdateCheckOutcome.SkippedNotLicensed, Message: "no license document");
            }

            var existing = TryReadPending();
            if (existing is not null &&
                File.Exists(existing.ZipPath) &&
                AppVersionInfo.CompareSemVer(existing.Version, AppVersionInfo.SemVer) > 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.ReadyToApply,
                    existing.Version,
                    Pending: existing);
            }

            using var manifestRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/update/manifest")
            {
                Content = JsonContent(new UpdateManifestRequest
                {
                    Channel = _options.Update.Channel,
                    License = document
                })
            };
            using var manifestResponse = await _client.SendAsync(manifestRequest, ct);
            if (manifestResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                ClearPending();
                return new UpdateCheckResult(UpdateCheckOutcome.Forbidden);
            }

            if (!manifestResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Update manifest HTTP {Status}", (int)manifestResponse.StatusCode);
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, Message: $"manifest {(int)manifestResponse.StatusCode}");
            }

            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
                await manifestResponse.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files.Count == 0)
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, Message: "invalid manifest");

            if (AppVersionInfo.CompareSemVer(manifest.Version, AppVersionInfo.SemVer) <= 0)
            {
                ClearPending();
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, manifest.Version);
            }

            var installRoot = InstallRoot;
            var (changed, deletes) = await DiffAsync(manifest, installRoot, ct);
            if (changed.Count == 0 && deletes.Count == 0)
            {
                // Version bumped but files match — still treat as up to date locally.
                await SaveHostedFilesAsync(manifest.Files.Select(f => f.Path), ct);
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, manifest.Version);
            }

            if (changed.Count == 0)
            {
                // Only deletes: write empty zip pending so launcher can apply deletes.
                var pendingDeletesOnly = await WritePendingAsync(manifest.Version, installRoot, changed, deletes, zipPath: null, ct);
                return new UpdateCheckResult(UpdateCheckOutcome.Downloaded, manifest.Version, Pending: pendingDeletesOnly);
            }

            using var deltaRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/update/delta")
            {
                Content = JsonContent(new UpdateDeltaRequest
                {
                    Channel = _options.Update.Channel,
                    Paths = changed,
                    License = document
                })
            };
            using var deltaResponse = await _client.SendAsync(deltaRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (deltaResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                ClearPending();
                return new UpdateCheckResult(UpdateCheckOutcome.Forbidden);
            }

            deltaResponse.EnsureSuccessStatusCode();

            var versionDir = Path.Combine(UpdatesRoot, SanitizeFileName(manifest.Version));
            Directory.CreateDirectory(versionDir);
            var zipPath = Path.Combine(versionDir, "delta.zip");
            var tempZip = zipPath + ".partial";
            await using (var input = await deltaResponse.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(tempZip))
                await input.CopyToAsync(output, ct);

            await VerifyDeltaZipAsync(tempZip, manifest, changed, ct);
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            File.Move(tempZip, zipPath);

            var pending = await WritePendingAsync(manifest.Version, installRoot, changed, deletes, zipPath, ct);
            await SaveHostedFilesAsync(manifest.Files.Select(f => f.Path), ct);
            _logger.LogInformation(
                "Update package ready: {Version}, changed={Changed}, deletes={Deletes}",
                manifest.Version, changed.Count, deletes.Count);
            return new UpdateCheckResult(UpdateCheckOutcome.Downloaded, manifest.Version, Pending: pending);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or CryptographicException)
        {
            _logger.LogWarning(ex, "Update check/download failed");
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Message: ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public PendingUpdate? TryReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath))
                return null;
            return JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath));
        }
        catch
        {
            return null;
        }
    }

    public void ClearPending()
    {
        try
        {
            if (File.Exists(PendingPath))
                File.Delete(PendingPath);
        }
        catch
        {
            // ignore
        }
    }

    private async Task<PendingUpdate> WritePendingAsync(
        string version,
        string installRoot,
        List<string> changed,
        List<string> deletes,
        string? zipPath,
        CancellationToken ct)
    {
        if (zipPath is null)
        {
            var versionDir = Path.Combine(UpdatesRoot, SanitizeFileName(version));
            Directory.CreateDirectory(versionDir);
            zipPath = Path.Combine(versionDir, "delta.zip");
            if (!File.Exists(zipPath))
            {
                await using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
                // empty archive
            }
        }

        var pending = new PendingUpdate
        {
            Version = version,
            ZipPath = zipPath,
            InstallRoot = installRoot,
            Changed = changed,
            Deletes = deletes
        };
        Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
        await File.WriteAllTextAsync(PendingPath, JsonSerializer.Serialize(pending, JsonOptions), ct);
        return pending;
    }

    private async Task<(List<string> Changed, List<string> Deletes)> DiffAsync(
        UpdateManifest manifest,
        string installRoot,
        CancellationToken ct)
    {
        var changed = new List<string>();
        var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(file.Path);
            if (relative is null)
                continue;
            remotePaths.Add(relative);
            var localPath = Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(localPath))
            {
                changed.Add(relative);
                continue;
            }

            var hash = await HashFileAsync(localPath, ct);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                changed.Add(relative);
        }

        var deletes = new List<string>();
        var previous = await LoadHostedFilesAsync(ct);
        if (previous is { Count: > 0 })
        {
            foreach (var path in previous)
            {
                var relative = NormalizeRelativePath(path);
                if (relative is null || remotePaths.Contains(relative))
                    continue;
                var localPath = Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localPath))
                    deletes.Add(relative);
            }
        }

        return (changed, deletes);
    }

    private static async Task VerifyDeltaZipAsync(
        string zipPath,
        UpdateManifest manifest,
        IReadOnlyList<string> expectedPaths,
        CancellationToken ct)
    {
        var byPath = manifest.Files.ToDictionary(
            f => NormalizeRelativePath(f.Path) ?? f.Path,
            f => f,
            StringComparer.OrdinalIgnoreCase);

        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;
            var relative = NormalizeRelativePath(entry.FullName);
            if (relative is null)
                throw new InvalidDataException("Unsafe zip entry: " + entry.FullName);
            if (!byPath.TryGetValue(relative, out var meta))
                throw new InvalidDataException("Unexpected zip entry: " + relative);

            await using var stream = entry.Open();
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
            if (!string.Equals(hash, meta.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Hash mismatch for " + relative);
            if (entry.Length != meta.Size && meta.Size > 0 && entry.Length > 0)
            {
                // Prefer hash; size mismatch alone is not fatal when hash matches.
            }

            seen.Add(relative);
        }

        foreach (var path in expectedPaths)
        {
            if (!seen.Contains(path))
                throw new InvalidDataException("Missing zip entry: " + path);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(path) ||
            normalized.StartsWith("~/"))
            return null;
        return normalized;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private async Task SaveHostedFilesAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var list = paths
            .Select(NormalizeRelativePath)
            .Where(p => p is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(HostedFilesCachePath)!);
        await File.WriteAllTextAsync(HostedFilesCachePath, JsonSerializer.Serialize(list), ct);
    }

    private async Task<List<string>?> LoadHostedFilesAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(HostedFilesCachePath))
                return null;
            return JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(HostedFilesCachePath, ct));
        }
        catch
        {
            return null;
        }
    }
}
