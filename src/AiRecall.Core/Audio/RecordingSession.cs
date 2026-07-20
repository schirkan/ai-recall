using NAudio.CoreAudioApi;
using Serilog;

namespace AiRecall.Core.Audio;

/// <summary>
/// Auslöser einer <see cref="RecordingSession"/> (Spec 0014 Iter. 3.1).
/// Wird im MD-Frontmatter als <c>trigger_source</c> geschrieben
/// (Polling -> "polling", ManualAudio -> "manual-audio")
/// und beeinflusst das Folder-Key-Schema (ManualAudio bekommt "manual-"-Prefix).
/// </summary>
public enum RecordingTriggerSource
{
    /// <summary>Meeting-getriggerte Aufnahme via <c>MeetingTrigger</c> (Spec 0013).</summary>
    Polling,

    /// <summary>Manuelle Aufnahme via Tray-Menu (Ctrl+Shift+R, Spec 0014 Iter. 1+3).</summary>
    ManualAudio,
}

/// <summary>
/// Orchestriert eine einzelne Meeting-Aufnahme (Spec 0013 v0.3 Update 8).
///
/// <para>
/// Lifecycle:
/// </para>
/// <list type="number">
///   <item><see cref="Start"/> erstellt den Meeting-Folder, schreibt initiales
///         <c>meta.md</c> mit Status <c>recording</c>, startet zwei NAudio-Captures
///         (Mic + Loopback) in eigenem Thread.</item>
///   <item><see cref="StopAsync"/> cancelt den CancellationToken, wartet auf den
///         Background-Task, schreibt finale WAV-Files (mic.wav + loopback.wav),
///         aktualisiert <c>meta.md</c> mit Status <c>recorded</c> und Audio-Links,
///         liefert <see cref="MeetingRecordingPaths"/> fuer den Worker.</item>
///   <item><see cref="Dispose"/> ruft <see cref="StopAsync"/> falls laufend.</item>
/// </list>
///
/// <para>
/// Threading: NAudio erzeugt pro Capture-Stream einen eigenen internen Thread.
/// <see cref="Start"/> startet zusaetzlich einen Coordinator-Task via <see cref="Task.Run"/>,
/// der mit <see cref="Task.Delay(TimeSpan, CancellationToken)"/> auf das Stop-Signal wartet.
/// Beim Cancel werden die Capture-Streams gestoppt, die WAV-Files geschrieben und
/// das Coordinator-Task sauber beendet.
/// </para>
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly RecordingTriggerSource _triggerSource;
    private readonly AudioConfig _config;
    private readonly ILogger _logger;
    private readonly IAudioRecorderFactory _recorderFactory;
    private readonly IAudioDeviceProvider _deviceProvider;
    private readonly string _meetingIdShort;
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _cts = new();

    private IAudioRecorder? _micRecorder;
    private IAudioRecorder? _loopbackRecorder;
    private Task? _coordinatorTask;
    private string? _folder;
    private RecordingState _state = RecordingState.Created;
    private bool _disposed;

    /// <summary>Eindeutige Meeting-ID (8 hex, deterministisch).</summary>
    public string MeetingIdShort => _meetingIdShort;

    /// <summary>Meeting-Folder, oder null wenn noch nicht gestartet.</summary>
    public string? Folder => _folder;

    /// <summary>Aktueller Lifecycle-State.</summary>
    public RecordingState State => _state;

    /// <summary>Start-Zeitpunkt (UTC).</summary>
    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>
    /// Ausloeser der Session (Spec 0014 Iter. 3.1). Beeinflusst MD-Frontmatter
    /// (<c>trigger_source</c>-Feld) und Folder-Key-Schema
    /// (ManualAudio bekommt <c>"manual-"</c>-Prefix).
    /// </summary>
    public RecordingTriggerSource TriggerSource => _triggerSource;

    /// <summary>
    /// Erstellt eine neue Recording-Session fuer ein Meeting.
    /// </summary>
    /// <param name="triggerSource">
    /// Ausloeser der Aufnahme (Spec 0014 Iter. 3.1). Polling fuer Meeting-getriggerte
    /// Aufnahmen via <c>MeetingTrigger</c>, ManualAudio fuer manuelle Aufnahmen
    /// via Tray-Menu. Bestimmt MD-Frontmatter-Feld <c>trigger_source</c>
    /// und Folder-Key-Schema.
    /// </param>
    /// <param name="meetingIdShort">Eindeutige 8-hex-ID (vom Caller bestimmt).</param>
    /// <param name="startedAt">Start-Zeitpunkt (vom Caller bestimmt, fuer deterministische Pfade).</param>
    /// <param name="topic">Meeting-Topic (fuer meta.md).</param>
    /// <param name="config">Audio-Konfiguration.</param>
    /// <param name="logger">Serilog-Logger.</param>
    /// <param name="recorderFactory">Factory fuer IAudioRecorder (DI-faehig fuer Tests).</param>
    /// <param name="deviceProvider">Provider fuer Audio-Devices.</param>
    public RecordingSession(
        RecordingTriggerSource triggerSource,
        string meetingIdShort,
        DateTimeOffset startedAt,
        string topic,
        AudioConfig config,
        ILogger logger,
        IAudioRecorderFactory recorderFactory,
        IAudioDeviceProvider deviceProvider)
    {
        if (string.IsNullOrWhiteSpace(meetingIdShort))
            throw new ArgumentException("must not be empty", nameof(meetingIdShort));
        _triggerSource = triggerSource;
        _meetingIdShort = meetingIdShort;
        _startedAt = startedAt;
        _topic = topic;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        _deviceProvider = deviceProvider ?? throw new ArgumentNullException(nameof(deviceProvider));
    }

    private string _topic;

    /// <summary>
    /// Startet die Aufnahme im eigenen Background-Thread.
    /// Schreibt initiales meta.md mit Status <c>recording</c>.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != RecordingState.Created)
            throw new InvalidOperationException($"Cannot start: state is {_state}");

        // 1) Folder erstellen
        var storageRoot = ResolveStorageRoot(_config.StorageRoot);
        var datePart = _startedAt.ToString("yyyy-MM-dd");
        var timePart = _startedAt.ToString("HHmmss");
        // Spec 0014 Iter. 3.1: Manual-Audio bekommt "manual-"-Prefix im Key
        // (Spec: "{rootPath}/yyyy-MM-dd/audio/{key}/ mit Key manual-{guid}"),
        // Polling bleibt beim gewohnten Schema (kompatibel zu Spec 0013).
        var keyPart = _triggerSource == RecordingTriggerSource.ManualAudio
            ? $"manual-{_meetingIdShort}"
            : _meetingIdShort;
        _folder = Path.Combine(storageRoot, datePart, $"{timePart}-{keyPart}");
        Directory.CreateDirectory(_folder);

        // 2) Recorder erzeugen
        var format = AudioFormat.Default; // 16 kHz, 16-bit, Mono

        var micDevice = ResolveDevice(_deviceProvider.EnumerateInputDevices(),
            _config.MicDeviceId, () => _deviceProvider.GetDefaultInputDevice());
        if (micDevice == null)
        {
            _state = RecordingState.Failed;
            throw new InvalidOperationException("No input (microphone) device available");
        }
        _micRecorder = _recorderFactory.Create(micDevice, format, loopback: false);

        var loopDevice = ResolveDevice(_deviceProvider.EnumerateLoopbackDevices(),
            _config.LoopbackDeviceId, () => _deviceProvider.GetDefaultLoopbackDevice());
        if (loopDevice == null)
        {
            _state = RecordingState.Failed;
            throw new InvalidOperationException("No loopback (speaker) device available");
        }
        _loopbackRecorder = _recorderFactory.Create(loopDevice, format, loopback: true);

        // 3) Recorder starten
        try
        {
            _micRecorder.Start();
            _loopbackRecorder.Start();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RecordingSession: failed to start NAudio captures");
            _state = RecordingState.Failed;
            throw;
        }

        // 4) Coordinator-Task starten (wartet auf CancellationToken)
        _coordinatorTask = Task.Run(() => CoordinatorLoopAsync(_cts.Token));

        // 5) Initiales meta.md schreiben
        WriteInitialMetaMd();

        _state = RecordingState.Recording;
        _logger.Information("RecordingSession started: {Folder} (topic='{Topic}')", _folder, _topic);
    }

    /// <summary>
    /// Stoppt die Aufnahme (CancellationToken-basiert). Schreibt finale WAV-Files
    /// und aktualisiert meta.md auf Status <c>recorded</c> mit Audio-Links.
    /// </summary>
    public async Task<MeetingRecordingPaths> StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != RecordingState.Recording)
            throw new InvalidOperationException($"Cannot stop: state is {_state}");

        _logger.Information("RecordingSession stopping: {Folder}", _folder);

        // 1) CancellationToken canceln -> Coordinator-Task wirft OperationCanceledException
        _cts.Cancel();

        // 2) Coordinator-Task abwarten (max. 5 s)
        if (_coordinatorTask != null)
        {
            try
            {
                await _coordinatorTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { /* erwartet */ }
            catch (TimeoutException)
            {
                _logger.Warning("RecordingSession: coordinator task did not finish in 5 s");
            }
        }

        // 3) Recorder stoppen und WAV-Rohdaten holen
        var micBytes = _micRecorder?.Stop() ?? throw new InvalidOperationException("Mic recorder null");
        var loopBytes = _loopbackRecorder?.Stop() ?? throw new InvalidOperationException("Loopback recorder null");

        // 4) WAV-Files schreiben
        if (_folder == null) throw new InvalidOperationException("Folder not initialized");
        var micPath = Path.Combine(_folder, "mic.wav");
        var loopPath = Path.Combine(_folder, "loopback.wav");
        var metaPath = Path.Combine(_folder, "meta.md");
        await File.WriteAllBytesAsync(micPath, micBytes);
        await File.WriteAllBytesAsync(loopPath, loopBytes);

        // 5) meta.md auf Status "recorded" aktualisieren mit Audio-Links
        var endedAt = DateTimeOffset.Now;
        UpdateMetaMdRecorded(endedAt);

        _state = RecordingState.Recorded;
        _logger.Information("RecordingSession recorded: {Mic} ({MicBytes} B), {Loop} ({LoopBytes} B)",
            micPath, micBytes.Length, loopPath, loopBytes.Length);

        return new MeetingRecordingPaths(_folder, micPath, loopPath, metaPath);
    }

    /// <summary>
    /// Force-Stop (User-Tray-Menu "Stop Recording" oder Crash).
    /// Wie <see cref="StopAsync"/>, aber ohne 30-s-Debounce (in v0.3: identisch).
    /// </summary>
    public Task<MeetingRecordingPaths> ForceStopAsync() => StopAsync();

    private async Task CoordinatorLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Erwartetes Cancel-Signal vom StopAsync
        }
        // Wir machen hier nichts weiteres — die WAV-Files werden in StopAsync geschrieben
        await Task.CompletedTask;
    }

    private void WriteInitialMetaMd()
    {
        if (_folder == null) return;
        var metaPath = Path.Combine(_folder, "meta.md");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("schema: ai-recall/meeting-recording/v1");
        sb.AppendLine("type: audio-meeting");
        sb.AppendLine("status: recording");
        sb.AppendLine($"meeting_id_short: {_meetingIdShort}");
        sb.AppendLine($"started_at: {_startedAt:O}");
        sb.AppendLine("ended_at: null");
        sb.AppendLine("duration_seconds: null");
        sb.AppendLine($"topic: \"{EscapeYaml(_topic)}\"");
        // Spec 0014 Iter. 3.1: trigger_source wird aus _triggerSource abgeleitet.
        // Polling -> "polling" (Spec 0013 kompatibel),
        // ManualAudio -> "manual-audio" (Spec 0014 Tray-Manuelle-Aufnahme).
        var triggerSourceStr = _triggerSource switch
        {
            RecordingTriggerSource.Polling => "polling",
            RecordingTriggerSource.ManualAudio => "manual-audio",
            _ => "unknown",
        };
        sb.AppendLine($"trigger_source: {triggerSourceStr}");
        sb.AppendLine("audio_files: []");
        sb.AppendLine("transcript_status: pending");
        sb.AppendLine("diarization: required");
        sb.AppendLine("provider: \"\"");
        sb.AppendLine("language: deu");
        sb.AppendLine("participants: []");
        sb.AppendLine("calendar_appointment_id: null");
        sb.AppendLine("worker_task_enqueued: false");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Meeting: {_topic}");
        sb.AppendLine();
        sb.AppendLine($"**Aufnahme-Status:** l\u00e4uft seit {_startedAt:O}");
        sb.AppendLine();
        sb.AppendLine("## Teilnehmer");
        sb.AppendLine();
        sb.AppendLine("- _(Liste wird in v0.4 per Outlook-Kalender-Lookup bef\u00fcllt)_");
        sb.AppendLine();
        sb.AppendLine("## Audio-Files");
        sb.AppendLine();
        sb.AppendLine("- _(Audio-Files werden beim Meeting-Ende geschrieben und hier verlinkt)_");
        sb.AppendLine();
        sb.AppendLine("## Transkription");
        sb.AppendLine();
        sb.AppendLine("Wird im Hintergrund verarbeitet, sobald das Meeting beendet ist.");

        File.WriteAllText(metaPath, sb.ToString());
    }

    private void UpdateMetaMdRecorded(DateTimeOffset endedAt)
    {
        if (_folder == null) return;
        var metaPath = Path.Combine(_folder, "meta.md");
        if (!File.Exists(metaPath)) return;

        var existing = File.ReadAllText(metaPath);
        var durationSeconds = (long)(endedAt - _startedAt).TotalSeconds;

        // Ersetze status: recording -> recorded, ended_at, duration_seconds, audio_files
        var updated = existing
            .Replace("status: recording", "status: recorded")
            .Replace("ended_at: null", $"ended_at: {endedAt:O}")
            .Replace("duration_seconds: null", $"duration_seconds: {durationSeconds}")
            .Replace("audio_files: []", "audio_files:\n  - mic.wav\n  - loopback.wav")
            .Replace("## Audio-Files\n\n- _(Audio-Files werden beim Meeting-Ende geschrieben und hier verlinkt)_",
                     "## Audio-Files\n\n- Mikrofon: [`mic.wav`](mic.wav) (PCM 16 kHz Mono 16-bit)\n- Speaker-Loopback: [`loopback.wav`](loopback.wav) (PCM 16 kHz Mono 16-bit)")
            .Replace("**Aufnahme-Status:** l\u00e4uft seit", $"**Aufnahme-Status:** beendet um {endedAt:O} (Dauer {durationSeconds}s); Aufnahme lief seit");

        File.WriteAllText(metaPath, updated);
    }

    private static string ResolveStorageRoot(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) configured = "audio";
        if (Path.IsPathRooted(configured)) return configured;
        return Path.Combine(AppContext.BaseDirectory, configured);
    }

    private static AudioDeviceInfo? ResolveDevice(
        IReadOnlyList<AudioDeviceInfo> allDevices,
        string preferredDeviceId,
        Func<AudioDeviceInfo?> getDefault)
    {
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var match = allDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return getDefault();
    }

    private static string EscapeYaml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s!
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", " ")
            .Replace("\r", " ");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_state == RecordingState.Recording)
        {
            try { await StopAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "RecordingSession.DisposeAsync: StopAsync failed"); }
        }

        _micRecorder?.Dispose();
        _loopbackRecorder?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Factory fuer <see cref="IAudioRecorder"/> (DI-faehig fuer Tests).
/// </summary>
public interface IAudioRecorderFactory
{
    IAudioRecorder Create(AudioDeviceInfo device, AudioFormat format, bool loopback);
}

/// <summary>
/// NAudio-basierte Default-Implementierung von <see cref="IAudioRecorderFactory"/>.
/// Erzeugt pro Aufruf einen neuen <see cref="WasapiAudioRecorder"/>.
/// </summary>
public sealed class WasapiAudioRecorderFactory : IAudioRecorderFactory
{
    public IAudioRecorder Create(AudioDeviceInfo device, AudioFormat format, bool loopback)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            MMDevice mm;
            if (loopback)
            {
                var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                try
                {
                    mm = renderDevices.First(d => string.Equals(d.ID, device.DeviceId, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    foreach (var d in renderDevices.Where(d => d.ID != device.DeviceId)) d.Dispose();
                }
            }
            else
            {
                var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                try
                {
                    mm = captureDevices.First(d => string.Equals(d.ID, device.DeviceId, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    foreach (var d in captureDevices.Where(d => d.ID != device.DeviceId)) d.Dispose();
                }
            }
            return new WasapiAudioRecorder(mm, format, loopback);
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }
    }
}