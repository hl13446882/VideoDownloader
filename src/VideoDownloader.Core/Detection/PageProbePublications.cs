using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

/// <summary>Consumer contract: only the final publication, never probe controls or pending work.</summary>
public interface IPageProbePublications
{
    PageProbeResult? Current { get; }
}

/// <summary>Owned by the common probe lifecycle. Consumers receive a separate read-only object.</summary>
public sealed class PageProbePublications
{
    private TaskCompletionSource<PageProbeResult> _completion = CreateCompletion();

    public PageProbePublications() => Reader = new PublicationReader(this);

    public IPageProbePublications Reader { get; }
    public TaskCompletionSource<PageProbeResult> Completion => _completion;

    public void Begin()
    {
        Invalidate();
    }

    public void Invalidate()
    {
        // Replace even a successfully completed receipt: a retired result must disappear.
        Interlocked.Exchange(ref _completion, CreateCompletion()).TrySetCanceled();
    }

    private static TaskCompletionSource<PageProbeResult> CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PublicationReader(PageProbePublications owner) : IPageProbePublications
    {
        public PageProbeResult? Current
        {
            get
            {
                var task = Volatile.Read(ref owner._completion).Task;
                return task.IsCompletedSuccessfully ? task.Result : null;
            }
        }
    }
}
