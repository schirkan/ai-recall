namespace AiRecall.Core.Util;

/// <summary>Close input, allow queued work to finish, then cancel on timeout.</summary>
public sealed class WorkerShutdown(Action closeInput, Task completion, CancellationTokenSource cancellation, TimeSpan drainTimeout)
{
    private readonly object _gate = new();
    private bool _stopped;

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            closeInput();
            try
            {
                if (completion.Wait(drainTimeout))
                {
                    cancellation.Dispose();
                    return;
                }
            }
            catch (AggregateException)
            {
                // The faulted worker has already exited.
                cancellation.Dispose();
                return;
            }

            // Keep the token source alive until the workers and cancellation callbacks exit.
            _ = CancelAndReleaseAsync();
        }
    }

    private async Task CancelAndReleaseAsync()
    {
        try { await Task.WhenAll(completion, cancellation.CancelAsync()).ConfigureAwait(false); }
        catch (Exception) { /* Shutdown can surface cancellation or an already-failed worker. */ }
        finally { cancellation.Dispose(); }
    }
}
