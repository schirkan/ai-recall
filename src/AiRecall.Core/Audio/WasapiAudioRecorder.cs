using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AiRecall.Core.Audio;

/// <summary>WASAPI microphone or loopback capture in the requested PCM WAV format.</summary>
public sealed class WasapiAudioRecorder : IAudioRecorder
{
    private readonly Func<IWaveIn> _createCapture;
    private readonly MMDevice? _device;
    private readonly object _lifecycle = new();
    private IWaveIn? _capture;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private bool _disposed;

    public WasapiAudioRecorder(MMDevice device, AudioFormat format)
        : this(device, format, loopback: false)
    {
    }

    public WasapiAudioRecorder(MMDevice device, AudioFormat format, bool loopback)
        : this(() => loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device), format)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    internal WasapiAudioRecorder(Func<IWaveIn> createCapture, AudioFormat format)
    {
        _createCapture = createCapture ?? throw new ArgumentNullException(nameof(createCapture));
        Format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public AudioFormat Format { get; }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null) throw new InvalidOperationException("Recorder already started");

            try
            {
                // NAudio captures the constructor's SynchronizationContext for
                // RecordingStopped. A synchronous Stop on the tray thread must
                // not wait for an event posted back to that same thread.
                var context = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try { _capture = _createCapture(); }
                finally { SynchronizationContext.SetSynchronizationContext(context); }

                // Shared-mode WASAPI (including loopback) enables AutoConvertPcm
                // in NAudio. Set the stream format, not just the WAV header.
                var pcm = new WaveFormat(Format.SampleRate, Format.BitsPerSample, Format.Channels);
                _capture.WaveFormat = pcm;
                var actual = _capture.WaveFormat;
                if (!pcm.Equals(actual))
                    throw new NotSupportedException($"WASAPI format {actual} does not match requested PCM format {pcm}");
                _buffer = new MemoryStream();
                _writer = new WaveFileWriter(_buffer, actual);
                _recordingStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
            }
            catch
            {
                ReleaseCapture();
                throw;
            }
        }
    }

    public byte[] Stop()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var capture = _capture ?? throw new InvalidOperationException("Recorder not started");
            try
            {
                capture.StopRecording();
                // StopRecording only requests a stop. The last DataAvailable
                // may still be in flight; close the WAV only after the event.
                var error = _recordingStopped!.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (error is not null)
                    throw new IOException("WASAPI capture failed", error);

                _writer!.Dispose(); // finalizes RIFF and data chunk lengths
                _writer = null;
                return _buffer!.ToArray(); // MemoryStream supports ToArray after Dispose
            }
            finally
            {
                ReleaseCapture();
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) =>
        _writer!.Write(e.Buffer, 0, e.BytesRecorded);

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        _recordingStopped!.TrySetResult(e.Exception);

    // Capture callbacks never take _lifecycle. NAudio Dispose joins the
    // capture thread, so its writer must remain valid until Dispose returns.
    private void ReleaseCapture()
    {
        try
        {
            _capture?.Dispose();
        }
        finally
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture = null;
            }
            _writer?.Dispose();
            _writer = null;
            _buffer?.Dispose();
            _buffer = null;
            _recordingStopped = null;
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            try { ReleaseCapture(); }
            finally { _device?.Dispose(); }
        }
    }
}
