using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
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

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache, NullLogger<SubtitlePipeline>.Instance);
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

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache, NullLogger<SubtitlePipeline>.Instance);
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
    public void Display_text_supports_original_chinese_english_and_bilingual()
    {
        var englishSource = new SubtitleSegment
        {
            Id = 1,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            SourceLanguage = "en",
            OriginalText = "hello",
            ChineseText = "你好"
        };

        Assert.Equal("hello", englishSource.GetDisplayText(SubtitleMode.Original));
        Assert.Equal("你好", englishSource.GetDisplayText(SubtitleMode.Chinese));
        Assert.Equal("hello", englishSource.GetDisplayText(SubtitleMode.English));
        Assert.Equal("你好\nhello", englishSource.GetDisplayText(SubtitleMode.Bilingual));

        var chineseSource = new SubtitleSegment
        {
            Id = 2,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            SourceLanguage = "zh",
            OriginalText = "你好",
            EnglishText = "hello"
        };
        Assert.Equal("你好\nhello", chineseSource.GetDisplayText(SubtitleMode.Bilingual));
    }

    [Fact]
    public async Task Bilingual_translation_fills_missing_chinese_and_english_sides()
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
                    SourceLanguage = "ja",
                    OriginalText = "こんにちは"
                }
            ]);

        var translator = Substitute.For<ISubtitleTranslator>();
        translator.TranslateBatchAsync(
                Arg.Is<IReadOnlyList<TranslationRequest>>(x => x.All(r => r.TargetLanguage == "zh")),
                Arg.Any<CancellationToken>())
            .Returns([new TranslationResult("你好", "fake", "1")]);
        translator.TranslateBatchAsync(
                Arg.Is<IReadOnlyList<TranslationRequest>>(x => x.All(r => r.TargetLanguage == "en")),
                Arg.Any<CancellationToken>())
            .Returns([new TranslationResult("hello", "fake", "1")]);

        var cache = Substitute.For<ISubtitleCacheStore>();
        cache.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SubtitleCacheSnapshot.Empty);

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache, NullLogger<SubtitlePipeline>.Instance);
        await pipeline.StartSessionAsync("s1", "media-1");
        await pipeline.SubmitAudioAsync(new AudioChunk(
            new byte[16000 * 2 * 3],
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3)));
        await pipeline.PrepareTranslationsAsync(
            SubtitleMode.Bilingual,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3));

        var current = pipeline.GetCurrent(TimeSpan.FromSeconds(1), SubtitleMode.Bilingual);
        Assert.NotNull(current);
        Assert.Equal("你好", current!.ChineseText);
        Assert.Equal("hello", current.EnglishText);
        Assert.Equal("你好\nhello", current.GetDisplayText(SubtitleMode.Bilingual));
        await translator.Received(2).TranslateBatchAsync(
            Arg.Any<IReadOnlyList<TranslationRequest>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Recognition_cursor_resumes_from_prior_coverage_when_playhead_runs_ahead()
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

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache, NullLogger<SubtitlePipeline>.Instance);
        pipeline.StartSessionAsync("s1", "media-1").GetAwaiter().GetResult();
        pipeline.SubmitAudioAsync(new AudioChunk(
            new byte[16000 * 2 * 4],
            TimeSpan.Zero,
            TimeSpan.FromSeconds(12))).GetAwaiter().GetResult();

        Assert.Equal(TimeSpan.FromSeconds(12), pipeline.GetCoveredUntil(TimeSpan.FromSeconds(5)));
        Assert.Null(pipeline.GetCoveredUntil(TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(12), pipeline.GetRecognitionCursor(TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(12), pipeline.GetRecognitionCursor(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(12), pipeline.GetSequentialCoveredUntil());
    }

    [Fact]
    public async Task Clear_recognized_wipes_timeline_and_deletes_cache()
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
                    OriginalText = "hello",
                    SourceLanguage = "en"
                }
            ]);

        var translator = Substitute.For<ISubtitleTranslator>();
        var cache = Substitute.For<ISubtitleCacheStore>();
        cache.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SubtitleCacheSnapshot.Empty);

        var pipeline = new SubtitlePipeline(
            recognizer,
            new SubtitleTimeline(),
            translator,
            cache,
            NullLogger<SubtitlePipeline>.Instance);
        await pipeline.StartSessionAsync("s-clear", "media-clear");
        await pipeline.SubmitAudioAsync(new AudioChunk(
            new byte[16000 * 2 * 2],
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2)));

        Assert.Equal(TimeSpan.FromSeconds(2), pipeline.GetSequentialCoveredUntil());
        Assert.NotNull(pipeline.GetCurrent(TimeSpan.FromSeconds(1), SubtitleMode.Original));

        await pipeline.ClearRecognizedAsync();

        Assert.Equal(TimeSpan.Zero, pipeline.GetSequentialCoveredUntil());
        Assert.Null(pipeline.GetCurrent(TimeSpan.FromSeconds(1), SubtitleMode.Original));
        await cache.Received(1).DeleteAsync("media-clear", Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task Manual_edit_with_translations_is_cached_and_skips_mt_and_marks_coverage()
    {
        var recognizer = Substitute.For<ISpeechRecognizer>();
        var translator = Substitute.For<ISubtitleTranslator>();
        var cache = Substitute.For<ISubtitleCacheStore>();
        cache.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubtitleCacheSnapshot(
                [
                    new SubtitleSegment
                    {
                        Id = 1,
                        Start = TimeSpan.FromSeconds(1),
                        End = TimeSpan.FromSeconds(4),
                        SourceLanguage = "en",
                        OriginalText = "hello",
                        ChineseText = "你好",
                        EnglishText = "hello"
                    }
                ],
                [new SubtitleCoverageRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4))]));

        SubtitleCacheSnapshot? saved = null;
        cache.SaveAsync(
                Arg.Any<string>(),
                Arg.Do<SubtitleCacheSnapshot>(x => saved = x),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var pipeline = new SubtitlePipeline(recognizer, new SubtitleTimeline(), translator, cache, NullLogger<SubtitlePipeline>.Instance);
        await pipeline.StartSessionAsync("s1", "media-1");

        var updated = await pipeline.ApplyManualEditAsync(
            TimeSpan.FromSeconds(2),
            "hi there",
            "嗨",
            "hi there");

        Assert.NotNull(updated);
        Assert.Equal("hi there", updated!.OriginalText);
        Assert.Equal("嗨", updated.ChineseText);
        Assert.Equal("hi there", updated.EnglishText);
        Assert.Equal(TimeSpan.FromSeconds(4), pipeline.GetCoveredUntil(TimeSpan.FromSeconds(2)));
        Assert.NotNull(saved);
        Assert.Equal("嗨", saved!.Segments[0].ChineseText);

        await pipeline.PrepareTranslationsAsync(
            SubtitleMode.Bilingual,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4));

        await translator.DidNotReceive().TranslateBatchAsync(
            Arg.Any<IReadOnlyList<TranslationRequest>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Recognition_transcript_parser_reads_editable_ranges()
    {
        const string text = """
            # comment
            [00:00:01.000 --> 00:00:03.500] [en]
            hello world

            [00:00:04.000 --> 00:00:06.000]
            第二句
            """;

        var segments = VideoDownloader.Infrastructure.Subtitles.Cache.FileSubtitleCacheStore
            .ParseRecognitionTranscript(text);

        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), segments[0].Start);
        Assert.Equal("hello world", segments[0].OriginalText);
        Assert.Equal("en", segments[0].SourceLanguage);
        Assert.Equal("第二句", segments[1].OriginalText);
    }
}
