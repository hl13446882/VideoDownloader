using NSubstitute;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Subtitles;
using VideoDownloader.Infrastructure.Subtitles.Translation;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class SubtitlePipelineTests
{
    [Fact]
    public void Timeline_finds_segment_by_media_time()
    {
        var timeline = new SubtitleTimeline();
        timeline.AddOrUpdate([
            new SubtitleSegment
            {
                Id = 1,
                Start = TimeSpan.FromSeconds(2),
                End = TimeSpan.FromSeconds(5),
                SourceLanguage = "en",
                OriginalText = "hello"
            }
        ]);

        Assert.Null(timeline.Find(TimeSpan.FromSeconds(1.9)));
        Assert.Equal("hello", timeline.Find(TimeSpan.FromSeconds(3))?.OriginalText);
        Assert.Null(timeline.Find(TimeSpan.FromSeconds(5.1)));
    }

    [Fact]
    public async Task Recognition_records_coverage_even_when_audio_is_silent()
    {
        var recognizer = Substitute.For<ISpeechRecognizer>();
        recognizer.RecognizeAsync(
                Arg.Any<AudioChunk>(),
                Arg.Any<SpeechRecognitionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SubtitleSegment>());

        var translator = Substitute.For<ISubtitleTranslator>();
        var cache = Substitute.For<ISubtitleCacheStore>();
        cache.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SubtitleCacheSnapshot.Empty);

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache);
        await pipeline.StartSessionAsync("s1", "media-1");
        await pipeline.SubmitAudioAsync(new AudioChunk(
            new byte[16000 * 2 * 10],
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromSeconds(10), pipeline.GetCoveredUntil(TimeSpan.FromSeconds(5)));
        Assert.Null(pipeline.GetCoveredUntil(TimeSpan.FromSeconds(12)));
        await cache.Received().SaveAsync(
            "media-1",
            Arg.Is<SubtitleCacheSnapshot>(x => x.Coverage.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chinese_translation_is_batched_for_recognition_window()
    {
        var recognizer = Substitute.For<ISpeechRecognizer>();
        recognizer.RecognizeAsync(
                Arg.Any<AudioChunk>(),
                Arg.Any<SpeechRecognitionContext>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new SubtitleSegment
                {
                    Id = 1,
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(2),
                    SourceLanguage = "en",
                    OriginalText = "hello"
                },
                new SubtitleSegment
                {
                    Id = 2,
                    Start = TimeSpan.FromSeconds(2),
                    End = TimeSpan.FromSeconds(4),
                    SourceLanguage = "en",
                    OriginalText = "world"
                }
            ]);

        IReadOnlyList<TranslationRequest>? captured = null;
        var translator = Substitute.For<ISubtitleTranslator>();
        translator.TranslateBatchAsync(
                Arg.Do<IReadOnlyList<TranslationRequest>>(x => captured = x),
                Arg.Any<CancellationToken>())
            .Returns([
                new TranslationResult("你好", "fake", "1"),
                new TranslationResult("世界", "fake", "1")
            ]);

        var cache = Substitute.For<ISubtitleCacheStore>();
        cache.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SubtitleCacheSnapshot.Empty);

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache);
        await pipeline.StartSessionAsync("s1", "media-1");
        await pipeline.SubmitAudioAsync(new AudioChunk(
            new byte[16000 * 2 * 5],
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5)));
        await pipeline.PrepareTranslationsAsync(
            SubtitleMode.Chinese,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        Assert.All(captured, x => Assert.Equal("zh", x.TargetLanguage));
        Assert.Equal("你好", pipeline.GetCurrent(TimeSpan.FromSeconds(1), SubtitleMode.Chinese)?.ChineseText);
        Assert.Equal("世界", pipeline.GetCurrent(TimeSpan.FromSeconds(3), SubtitleMode.Chinese)?.ChineseText);
        await translator.Received(1).TranslateBatchAsync(
            Arg.Any<IReadOnlyList<TranslationRequest>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Local_translator_rejects_non_loopback_endpoint_before_network_call()
    {
        var translator = new LocalLlmTranslator(
            new HttpClient(),
            new LocalLlmTranslatorOptions
            {
                Endpoint = "https://api.example.com/v1/chat/completions",
                Model = "test"
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            translator.TranslateAsync(new TranslationRequest("hello", "en", "zh")));

        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
