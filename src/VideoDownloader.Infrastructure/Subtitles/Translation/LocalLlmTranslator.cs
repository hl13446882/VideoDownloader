using System.Net.Http.Json;
using System.Text.Json;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles.Translation;

/// <summary>
/// Local-only translator using an OpenAI-compatible endpoint on loopback by default.
/// No cloud endpoint is configured or contacted by this implementation.
/// </summary>
public sealed class LocalLlmTranslator : ISubtitleTranslator
{
    private readonly HttpClient _httpClient;
    private readonly LocalLlmTranslatorOptions _options;

    public LocalLlmTranslator(HttpClient httpClient, LocalLlmTranslatorOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public string ProviderId => "local-llm";
    public string ProviderVersion => "v1";

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return new TranslationResult(string.Empty, ProviderId, ProviderVersion);

        EnsureLocalEndpoint(_options.Endpoint);

        var targetName = request.TargetLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "Simplified Chinese"
            : request.TargetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? "English"
                : request.TargetLanguage;

        var payload = new
        {
            model = _options.Model,
            temperature = 0.1,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a video subtitle translation engine. Translate only the supplied subtitle text. Preserve meaning, use natural concise spoken language, add no explanation, and output only the translation."
                },
                new
                {
                    role = "user",
                    content = $"Source language: {request.SourceLanguage}\nTarget language: {targetName}\nSubtitle:\n{request.Text}"
                }
            }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _httpClient.PostAsJsonAsync(_options.Endpoint, payload, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
            cancellationToken: timeout.Token).ConfigureAwait(false);

        var text = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim();

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Local translation returned empty text.");

        return new TranslationResult(text, ProviderId, ProviderVersion);
    }

    private static void EnsureLocalEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Local translation endpoint is invalid.");

        if (!uri.IsLoopback)
            throw new InvalidOperationException(
                "Local translator only accepts loopback endpoints. Cloud translation is reserved for a separate provider.");
    }
}
