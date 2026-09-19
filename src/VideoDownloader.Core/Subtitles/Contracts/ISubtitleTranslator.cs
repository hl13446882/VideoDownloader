namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitleTranslator
{
    string ProviderId { get; }
    string ProviderVersion { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
