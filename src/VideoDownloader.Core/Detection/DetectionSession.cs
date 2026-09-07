namespace VideoDownloader.Core.Detection;

public enum DetectionPhase { Discovering, Validating, Completed, Cancelled }

// One instance belongs to one generation; leases from an old instance cannot complete a new one.
public sealed class DetectionSession
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private bool _sealed;
    public Guid Id { get; } = Guid.NewGuid();
    public DetectionPhase Phase { get; private set; } = DetectionPhase.Discovering;
    public IDisposable? TryEnter(bool continuation = false)
    {
        lock (_gate)
        {
            if (_sealed && !(continuation && _pending > 0 && Phase == DetectionPhase.Validating)) return null;
            _pending++;
            return new Lease(this);
        }
    }
    public Task CompleteAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _sealed = true;
            if (Phase != DetectionPhase.Cancelled)
            {
                Phase = DetectionPhase.Validating;
                FinishIfIdle();
            }
            return _idle.Task.WaitAsync(ct);
        }
    }
    public void Cancel()
    {
        lock (_gate)
        {
            _sealed = true;
            Phase = DetectionPhase.Cancelled;
            _idle.TrySetCanceled();
        }
    }
    private void Leave()
    {
        lock (_gate) { _pending--; FinishIfIdle(); }
    }
    private void FinishIfIdle()
    {
        if (_sealed && _pending == 0 && Phase != DetectionPhase.Cancelled)
        {
            Phase = DetectionPhase.Completed;
            _idle.TrySetResult();
        }
    }
    private sealed class Lease(DetectionSession owner) : IDisposable
    {
        private DetectionSession? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
