using AiRecall.Core.Audio;
using NAudio.Wave;

namespace AiRecall.Core.Tests.Audio;

public class WasapiAudioRecorderTests
{
    [Fact]
    public async Task Stop_WaitsForRecordingStopped_AndWritesFinalPcmWavIncludingLateData()
    {
        var capture = new FakeCapture();
        using var recorder = new WasapiAudioRecorder(() => capture, AudioFormat.Default);
        recorder.Start();
        Assert.Equal(WaveFormatEncoding.Pcm, capture.FormatAtStart!.Encoding);
        Assert.Equal(16000, capture.FormatAtStart.SampleRate);
        Assert.Equal(16, capture.FormatAtStart.BitsPerSample);
        Assert.Equal(1, capture.FormatAtStart.Channels);

        capture.Send([1, 0, 2, 0]);
        var stop = Task.Run(recorder.Stop);
        await capture.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.False(capture.Disposed);
            capture.Send([3, 0]);
        }
        finally { capture.Finish(); }

        var wav = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capture.Disposed);
        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(capture.FormatAtStart, reader.WaveFormat);
        Assert.Equal(6L, reader.Length);
        var pcm = new byte[6];
        Assert.Equal(6, reader.Read(pcm, 0, pcm.Length));
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0 }, pcm);
    }

    [Fact]
    public async Task Stop_ReportsCaptureError_AndDisposesCapture()
    {
        var capture = new FakeCapture();
        var failure = new InvalidOperationException("device disconnected");
        using var recorder = new WasapiAudioRecorder(() => capture, AudioFormat.Default);
        recorder.Start();

        var stop = Task.Run(recorder.Stop);
        await capture.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capture.Finish(failure);

        var error = await Assert.ThrowsAsync<IOException>(async () => await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, error.InnerException);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public void Start_DoesNotBindCaptureToCallersSynchronizationContext()
    {
        var capture = new FakeCapture();
        var previous = SynchronizationContext.Current;
        var uiContext = new SynchronizationContext();
        using var recorder = new WasapiAudioRecorder(() =>
        {
            Assert.Null(SynchronizationContext.Current);
            return capture;
        }, AudioFormat.Default);

        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            recorder.Start();
            Assert.Same(uiContext, SynchronizationContext.Current);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public WaveFormat? FormatAtStart { get; private set; }
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() => FormatAtStart = WaveFormat;
        public void StopRecording() => StopRequested.TrySetResult();
        public void Send(byte[] pcm) => DataAvailable?.Invoke(this, new WaveInEventArgs(pcm, pcm.Length));
        public void Finish(Exception? exception = null) => RecordingStopped?.Invoke(this, new StoppedEventArgs(exception));
        public void Dispose() => Disposed = true;
    }
}
