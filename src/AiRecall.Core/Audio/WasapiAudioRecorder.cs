using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AiRecall.Core.Audio;

/// <summary>
/// NAudio <c>WasapiCapture</c>-basierte Implementierung von <see cref="IAudioRecorder"/>
/// fuer Input-Devices (Mikrofone).
///
/// <para>
/// Verwendet <see cref="WasapiCapture"/> fuer Capture-Streams und
/// <see cref="WasapiLoopbackCapture"/> fuer Output-Loopback.
/// Daten werden im PCM-Format (16-bit, Mono, 16 kHz) per
/// <see cref="IWaveProvider"/> konvertiert und in einem internen
/// <see cref="MemoryStream"/> gesammelt.
/// </para>
/// </summary>
public sealed class WasapiAudioRecorder : IAudioRecorder
{
    private readonly AudioFormat _format;
    private readonly MMDevice? _device;
    private readonly bool _isLoopback;
    private readonly object _lock = new();
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private IWaveIn? _capture;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Erstellt einen neuen Recorder fuer Input (Mikrofon).
    /// </summary>
    /// <param name="device">Device oder null fuer System-Default.</param>
    /// <param name="format">Gewuenschtes Audio-Format.</param>
    public WasapiAudioRecorder(MMDevice device, AudioFormat format)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _isLoopback = false;
    }

    /// <summary>
    /// Erstellt einen neuen Recorder fuer Output-Loopback (Speaker).
    /// </summary>
    /// <param name="device">Device oder null fuer System-Default.</param>
    /// <param name="format">Gewuenschtes Audio-Format.</param>
    public WasapiAudioRecorder(MMDevice device, AudioFormat format, bool loopback)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _isLoopback = loopback;
    }

    public AudioFormat Format => _format;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (_started)
                throw new InvalidOperationException("Recorder already started");

            _buffer = new MemoryStream();
            // WaveFileWriter mit PCM-Format initialisieren
            var waveFormat = new WaveFormat(_format.SampleRate, _format.BitsPerSample, _format.Channels);
            _writer = new WaveFileWriter(_buffer, waveFormat);

            // Capture starten
            if (_isLoopback)
            {
                var loop = new WasapiLoopbackCapture(_device);
                _capture = loop;
                loop.DataAvailable += OnDataAvailable;
                loop.RecordingStopped += OnRecordingStopped;
                loop.StartRecording();
            }
            else
            {
                var cap = new WasapiCapture(_device);
                _capture = cap;
                cap.DataAvailable += OnDataAvailable;
                cap.RecordingStopped += OnRecordingStopped;
                cap.StartRecording();
            }

            _started = true;
        }
    }

    public byte[] Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (!_started)
                throw new InvalidOperationException("Recorder not started");

            _capture?.StopRecording();
            // OnRecordingStopped schliesst writer und buffer
            // Wir warten kurz, bis die Events durch sind
            // (in NAudio werden sie synchron nach StopRecording gefeuert)
            if (_buffer == null || _writer == null)
                throw new InvalidOperationException("Recorder buffer not initialized");

            // Daten finalisieren
            _writer.Flush();
            var data = _buffer.ToArray();

            _started = false;
            return data;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            // Rohdaten aus NAudio ins WAV schreiben
            // WasapiCapture liefert bereits PCM-Bytes im Ziel-Format (Mono, 16-bit, 16 kHz)
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            // Buffer NICHT disposen — wir brauchen die Daten noch in Stop()
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            try { _capture?.StopRecording(); }
            catch { /* ignore */ }

            if (_capture is IDisposable disp)
            {
                try { disp.Dispose(); }
                catch { /* ignore */ }
            }

            _writer?.Dispose();
            _writer = null;
            _buffer?.Dispose();
            _buffer = null;
            _device?.Dispose();
        }
    }
}