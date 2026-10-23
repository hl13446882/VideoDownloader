using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class PageProbePublicationsTests
{
    [Fact]
    public void Consumer_Sees_Only_Final_Publication()
    {
        var producer = new PageProbePublications();
        var reader = producer.Reader;
        Assert.Null(reader.Current);
        var result = new PageProbeResult(Guid.NewGuid(), []);
        producer.Completion.SetResult(result);
        Assert.Same(result, reader.Current);
        Assert.IsNotType<PageProbePublications>(reader);
        Assert.Single(typeof(IPageProbePublications).GetProperties());
        Assert.All(typeof(IPageProbePublications).GetMethods(), method => Assert.True(method.IsSpecialName));
    }

    [Fact]
    public void Navigation_Retires_Previously_Published_Address()
    {
        var producer = new PageProbePublications();
        producer.Completion.SetResult(new(Guid.NewGuid(), []));
        producer.Invalidate();
        Assert.Null(producer.Reader.Current);
    }

    [Fact]
    public void Late_Old_Producer_Cannot_Publish_Into_New_Session()
    {
        var producer = new PageProbePublications();
        var old = producer.Completion;
        producer.Begin();
        Assert.True(old.Task.IsCanceled);
        Assert.False(old.TrySetResult(new(Guid.NewGuid(), [])));
        Assert.Null(producer.Reader.Current);
        var current = new PageProbeResult(Guid.NewGuid(), []);
        producer.Completion.SetResult(current);
        Assert.Same(current, producer.Reader.Current);
    }

    [Fact]
    public async Task Stopping_Auto_Subscription_Never_Cancels_Probe_Or_Publication()
    {
        var producer = new PageProbePublications();
        var pending = producer.Completion;
        using var cancellation = new CancellationTokenSource();
        var consumer = new PublishedProbeSubscription(producer.Reader);
        var waiting = consumer.WaitAsync(DateTime.UtcNow.AddMinutes(1), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Same(pending, producer.Completion);
        Assert.False(pending.Task.IsCompleted);
        var result = new PageProbeResult(Guid.NewGuid(), []);
        pending.SetResult(result);
        Assert.Same(result, producer.Reader.Current);
        Assert.Same(result, await new PublishedProbeSubscription(producer.Reader)
            .WaitAsync(DateTime.UtcNow.AddSeconds(1), CancellationToken.None));
    }
}
