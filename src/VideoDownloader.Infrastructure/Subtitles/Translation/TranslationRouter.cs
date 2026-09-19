using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles.Translation;

public enum SubtitleTranslationProvider
{
    Local = 0,
    Cloud = 1
}

public sealed class TranslationRouter
{
    private readonly LocalLlmTranslator _local;
    private readonly CloudTranslatorStub _cloud;

    public TranslationRouter(LocalLlmTranslator local, CloudTranslatorStub cloud)
    {
        _local = local;
        _cloud = cloud;
    }

    public ISubtitleTranslator Resolve(SubtitleTranslationProvider provider) => provider switch
    {
        SubtitleTranslationProvider.Cloud => _cloud,
        _ => _local
    };

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        SubtitleTranslationProvider provider = SubtitleTranslationProvider.Local,
        CancellationToken cancellationToken = default) =>
        Resolve(provider).TranslateAsync(request, cancellationToken);
}
