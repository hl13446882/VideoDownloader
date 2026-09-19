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
        var results = await TranslateBatchAsync([request], cancellationToken).ConfigureAwait(false);
        return results.Count == 0
            ? new TranslationResult(string.Empty, ProviderId, ProviderVersion)
            : results[0];
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
            return Array.Empty<TranslationResult>();

        EnsureLocalEndpoint(_options.Endpoint);
        var items = requests.Select((request, index) => new
        {
            id = index,
            source = request.SourceLanguage,
            target = NormalizeTarget(request.TargetLanguage),
            text = request.Text
        }).ToArray();

        var payload = new
        {
            model = _options.Model,
            temperature = 0.1,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a video subtitle translation engine. Translate each JSON item independently but use neighboring items for context. Preserve meaning, use concise natural spoken language, add no explanation. Return ONLY a JSON array with the same number and order of items. Each output item must be {\"id\":number,\"text\":\"translated subtitle\"}."
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(items)
                }
            }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _httpClient.PostAsJsonAsync(_options.Endpoint, payload, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var envelope = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
            cancellationToken: timeout.Token).ConfigureAwait(false);
        var content = envelope.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Local translation returned empty text.");

        var jsonText = ExtractJsonArray(content);
        using var translated = JsonDocument.Parse(jsonText);
        if (translated.RootElement.ValueKind != JsonValueKind.Array ||
            translated.RootElement.GetArrayLength() != requests.Count)
            throw new InvalidOperationException("Local translation returned an invalid batch size.");

        var byId = new Dictionary<int, string>();
        foreach (var item in translated.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) ||
                !item.TryGetProperty("text", out var textElement))
                continue;
            var text = textElement.GetString()?.Trim();
            if (id >= 0 && id < requests.Count && !string.IsNullOrWhiteSpace(text))
                byId[id] = text;
        }

        if (byId.Count != requests.Count)
            throw new InvalidOperationException("Local translation batch response is incomplete.");

        return Enumerable.Range(0, requests.Count)
            .Select(i => new TranslationResult(byId[i], ProviderId, ProviderVersion))
            .ToArray();
    }

    private static string NormalizeTarget(string targetLanguage) =>
        targetLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "Simplified Chinese"
            : targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? "English"
                : targetLanguage;

    private static string ExtractJsonArray(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                text = text[(firstLine + 1)..lastFence].Trim();
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end < start)
            throw new InvalidOperationException("Local translation did not return a JSON array.");
        return text[start..(end + 1)];
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
