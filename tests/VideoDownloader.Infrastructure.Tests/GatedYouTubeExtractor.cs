using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

internal sealed class GatedYouTubeExtractor : YouTubeYtDlpExtractor
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IReadOnlyList<DetectedVideo>>> _results = new();
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _available;
    private readonly bool _completesImmediately;
    private readonly bool _ignoreCancellation;
    private int _resolveCount;

    public GatedYouTubeExtractor(bool available = true, bool completesImmediately = false, bool ignoreCancellation = false)
        : base(
            Options.Create(new AppOptions
            {
                ExternalResolvers = new ExternalResolversOptions { Enabled = false }
            }),
            NullLogger<YouTubeYtDlpExtractor>.Instance,
            Substitute.For<IProbeMethodStats>())
    {
        _available = available;
        _completesImmediately = completesImmediately;
        _ignoreCancellation = ignoreCancellation;
    }

    public override bool IsAvailable => _available;
    public int ResolveCount => Volatile.Read(ref _resolveCount);
    public Task CancellationObserved => _cancelled.Task;

    public override async Task<IReadOnlyList<DetectedVideo>> ResolveAsync(
        Uri pageUrl, RequestContext context, CancellationToken ct)
    {
        var result = Result(Interlocked.Increment(ref _resolveCount));
        using var registration = ct.Register(() => _cancelled.TrySetResult());
        return _ignoreCancellation
            ? await result.Task.ConfigureAwait(false)
            : await result.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Complete(params DetectedVideo[] videos) => CompleteCall(1, videos);
    public void CompleteCall(int call, params DetectedVideo[] videos) => Result(call).TrySetResult(videos);
    public void Fail(Exception error) => Result(1).TrySetException(error);

    private TaskCompletionSource<IReadOnlyList<DetectedVideo>> Result(int call) =>
        _results.GetOrAdd(call, _ =>
        {
            var result = new TaskCompletionSource<IReadOnlyList<DetectedVideo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_completesImmediately) result.TrySetResult([]);
            return result;
        });
}
