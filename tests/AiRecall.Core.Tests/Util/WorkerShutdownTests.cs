using AiRecall.Core.Util;

namespace AiRecall.Core.Tests.Util;

public class WorkerShutdownTests
{
    [Fact]
    public async Task Stop_WhenDrainTimesOut_CancelsAndClosesOnlyOnce()
    {
        var cancellation = new CancellationTokenSource();
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerMayExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Token.Register(() => cancellationSeen.SetResult());
        var closed = 0;
        var shutdown = new WorkerShutdown(() => closed++, workerMayExit.Task, cancellation, TimeSpan.Zero);

        try
        {
            shutdown.Stop();
            shutdown.Stop();
            await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, closed);
            Assert.False(workerMayExit.Task.IsCompleted);
        }
        finally { workerMayExit.TrySetResult(); }
    }
}
