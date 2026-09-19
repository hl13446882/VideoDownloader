using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitleTranslator
{
    string ProviderId { get; }
    string ProviderVersion { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TranslationResult>(requests.Count);
        foreach (var request in requests)
            results.Add(await TranslateAsync(request, cancellationToken).ConfigureAwait(false));
        return results;
    }
}
