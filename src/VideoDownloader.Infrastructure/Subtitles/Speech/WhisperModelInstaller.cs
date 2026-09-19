using System.Security.Cryptography;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles.Speech;

/// <summary>
/// Optional one-time installer for the multilingual Whisper base model.
/// Recognition itself remains fully local after the model file is installed.
/// </summary>
public sealed class WhisperModelInstaller
{
    public const string BaseModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    // Official whisper.cpp model-table SHA-1 for ggml-base.bin.
    public const string BaseModelSha1 = "465707469ff3a37a2b9b8d8f89f2f99de7299dac";

    private readonly HttpClient _httpClient;

    public WhisperModelInstaller(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("subtitle-model-download");
    }

    public bool IsInstalled(string configuredPath)
    {
        try
        {
            var path = PathExpander.Expand(configuredPath);
            return File.Exists(path) && new FileInfo(path).Length > 100 * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> InstallBaseModelAsync(
        string configuredPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new ArgumentException("Whisper model path is required.", nameof(configuredPath));

        var path = Path.GetFullPath(PathExpander.Expand(configuredPath));
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Whisper model directory is invalid.");
        Directory.CreateDirectory(directory);

        if (File.Exists(path) && await HasExpectedHashAsync(path, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1);
            return path;
        }

        var temp = path + ".download";
        try
        {
            using var response = await _httpClient.GetAsync(
                BaseModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (total is > 0)
                        progress?.Report(Math.Clamp((double)written / total.Value, 0, 0.99));
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await HasExpectedHashAsync(temp, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Downloaded Whisper model failed SHA-1 verification.");

            File.Move(temp, path, overwrite: true);
            progress?.Report(1);
            return path;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);
        using var sha1 = SHA1.Create();
        var hash = await sha1.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash),
            BaseModelSha1,
            StringComparison.OrdinalIgnoreCase);
    }
}
