using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Licensing;

public sealed record LicenseInfo(string MachineId, bool IsFull, DateTimeOffset ExpiresAt, string Source)
{
    public bool IsValid => ExpiresAt > DateTimeOffset.UtcNow;
}

public sealed record LicenseDocument(
    [property: JsonPropertyName("machine_id")] string MachineId,
    [property: JsonPropertyName("edition")] string Edition,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("expires_at")] string ExpiresAt,
    [property: JsonPropertyName("signature")] string Signature);

public sealed class LicenseService
{
    private const string CacheFileName = "license.json";
    private readonly HttpClient _client;
    private readonly AppOptions _options;
    private readonly ILogger<LicenseService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public LicenseInfo Current { get; private set; } = new("unavailable", false, DateTimeOffset.MinValue, "uninitialized");
    public LicenseDocument? LastDocument { get; private set; }
    public int? DownloadLimitBytes => Current.IsFull && Current.IsValid ? null : _options.License.DemoMaxBytes;

    public LicenseService(HttpClient client, IOptions<AppOptions> options, ILogger<LicenseService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
        => await RefreshAsync(ct);

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _refreshLock.WaitAsync(0, ct))
            return;

        try
        {
            var machineId = await GetMachineIdAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/license/check")
                {
                    // The licensing endpoint reads a bounded Content-Length body.
                    Content = new StringContent(JsonSerializer.Serialize(new { machine_id = machineId }), Encoding.UTF8, "application/json")
                };
                using var response = await _client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var document = await response.Content.ReadFromJsonAsync<LicenseDocument>(cancellationToken: ct)
                    ?? throw new InvalidDataException("Empty license response.");
                Current = Verify(document, machineId);
                LastDocument = document;
                await File.WriteAllTextAsync(CachePath(), JsonSerializer.Serialize(document), ct);
                _logger.LogInformation("License checked: edition={Edition}, source=server", Current.IsFull ? "full" : "demo");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or CryptographicException)
            {
                _logger.LogWarning("License server unavailable or response invalid; checking signed cache: {Error}", ex.GetType().Name);
                try
                {
                    var cached = JsonSerializer.Deserialize<LicenseDocument>(await File.ReadAllTextAsync(CachePath(), ct));
                    Current = Verify(cached ?? throw new InvalidDataException("Empty cache."), machineId) with { Source = "cache" };
                    LastDocument = cached;
                }
                catch (Exception cacheEx)
                {
                    _logger.LogWarning("No valid license cache: {Error}", cacheEx.GetType().Name);
                    Current = new LicenseInfo(machineId, false, DateTimeOffset.MinValue, "demo");
                    LastDocument = null;
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private LicenseInfo Verify(LicenseDocument document, string machineId)
    {
        if (!string.Equals(document.MachineId, machineId, StringComparison.Ordinal) ||
            document.Edition is not ("full" or "demo"))
            throw new InvalidDataException("License does not match this machine.");

        var issued = DateTimeOffset.Parse(document.IssuedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal);
        var expires = DateTimeOffset.Parse(document.ExpiresAt, null, System.Globalization.DateTimeStyles.AssumeUniversal);
        if (expires <= DateTimeOffset.UtcNow || issued > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("License is expired or not yet valid.");

        var canonical = string.Join("\n", document.MachineId, document.Edition, document.IssuedAt, document.ExpiresAt);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_options.License.PublicKeyPem);
        if (!ecdsa.VerifyData(Encoding.UTF8.GetBytes(canonical), FromBase64Url(document.Signature), HashAlgorithmName.SHA256))
            throw new CryptographicException("License signature invalid.");

        return new LicenseInfo(machineId, document.Edition == "full", expires, "server");
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static async Task<string> GetMachineIdAsync(CancellationToken ct)
    {
        var serial = await ReadCimBaseboardSerialAsync(ct);
        var normalized = (serial ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized is "DEFAULT STRING" or "TO BE FILLED BY O.E.M.")
            return "unavailable";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VideoDownloader/v1\n" + normalized))).ToLowerInvariant();
    }

    private static async Task<string?> ReadCimBaseboardSerialAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_BaseBoard | Select-Object -First 1 -ExpandProperty SerialNumber)\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                return process.ExitCode == 0 ? (await output).Trim() : null;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string CachePath()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDownloader");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, CacheFileName);
    }

}
