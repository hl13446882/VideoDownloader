using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Subtitles.Audio;
using VideoDownloader.Infrastructure.Subtitles.Speech;
using VideoDownloader.Infrastructure.Subtitles.Translation;

namespace VideoDownloader.Infrastructure.Subtitles;

public static class SubtitleServiceCollectionExtensions
{
    public static IServiceCollection AddVideoDownloaderSubtitles(this IServiceCollection services)
    {
        services.AddSingleton(new WhisperSpeechRecognizerOptions());
        services.AddSingleton(new LocalLlmTranslatorOptions());

        services.AddSingleton<ISpeechRecognizer, WhisperSpeechRecognizer>();
        services.AddSingleton<IMediaAudioDecoder, FfmpegMediaAudioDecoder>();

        services.AddHttpClient("subtitle-local-translation", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false
        });

        services.AddSingleton(sp => new LocalLlmTranslator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("subtitle-local-translation"),
            sp.GetRequiredService<LocalLlmTranslatorOptions>()));
        services.AddSingleton<ISubtitleTranslator>(sp => sp.GetRequiredService<LocalLlmTranslator>());
        services.AddSingleton<CloudTranslatorStub>();
        services.AddSingleton<TranslationRouter>();

        // Timeline/pipeline are transient: each WebView/tab owns an isolated subtitle session.
        services.AddTransient<ISubtitleTimeline, SubtitleTimeline>();
        services.AddTransient<ISubtitlePipeline, SubtitlePipeline>();

        return services;
    }
}
