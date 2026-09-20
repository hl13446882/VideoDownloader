using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Subtitles.Audio;
using VideoDownloader.Infrastructure.Subtitles.Cache;
using VideoDownloader.Infrastructure.Subtitles.Speech;
using VideoDownloader.Infrastructure.Subtitles.Translation;

namespace VideoDownloader.Infrastructure.Subtitles;

public static class SubtitleServiceCollectionExtensions
{
    public static IServiceCollection AddVideoDownloaderSubtitles(
        this IServiceCollection services,
        AppOptions options)
    {
        services.AddSingleton(options.Subtitles);
        services.AddSingleton(new WhisperSpeechRecognizerOptions
        {
            ModelPath = options.Subtitles.WhisperModelPath,
            Language = "auto"
        });
        services.AddSingleton(new LocalLlmTranslatorOptions
        {
            Endpoint = options.Subtitles.LocalTranslationEndpoint,
            Model = options.Subtitles.LocalTranslationModel
        });

        services.AddSingleton<ISpeechRecognizer, WhisperSpeechRecognizer>();
        services.AddSingleton<IMediaAudioDecoder, FfmpegMediaAudioDecoder>();
        services.AddSingleton<ISubtitleCacheStore, FileSubtitleCacheStore>();

        services.AddHttpClient("subtitle-local-translation", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false
        });

        // Model download is user-triggered and may use the system proxy/network route.
        services.AddHttpClient("subtitle-model-download", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
        });

        services.AddSingleton(sp => new LocalLlmTranslator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("subtitle-local-translation"),
            sp.GetRequiredService<LocalLlmTranslatorOptions>()));
        // Cloud remains selectable in settings, but runtime always uses the local translator.
        services.AddSingleton<ISubtitleTranslator>(sp => sp.GetRequiredService<LocalLlmTranslator>());
        services.AddSingleton<CloudTranslatorStub>();
        services.AddSingleton<TranslationRouter>();
        services.AddSingleton<WhisperModelInstaller>();
        services.AddSingleton<LocalPlaybackMediaSourceResolver>();
        services.AddSingleton<SubtitleAsrActivity>();
        services.AddSingleton<SubtitleIdlePrewarmService>();

        // Timeline/pipeline are transient: each local-player WebView/tab owns an isolated subtitle session.
        services.AddTransient<ISubtitleTimeline, SubtitleTimeline>();
        services.AddTransient<ISubtitlePipeline, SubtitlePipeline>();

        return services;
    }
}
