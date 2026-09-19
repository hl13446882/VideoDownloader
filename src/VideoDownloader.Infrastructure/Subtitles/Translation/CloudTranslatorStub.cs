using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles.Translation;

/// <summary>
/// Reserved cloud translation entry point. No network request is implemented in this phase.
/// </summary>
public sealed class CloudTranslatorStub : ISubtitleTranslator
{
    public string ProviderId => "cloud";
    public string ProviderVersion => "disabled-v1";

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Cloud subtitle translation is reserved but not enabled.");
    }
}
