using System;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Audio;
using AiRecall.Core.Configuration;
using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Production-Default-Composition fuer <see cref="MeetingTrigger"/> + den
/// Recording- und Transcription-Worker (Spec 0013 v0.3 §1 + §5.4).
/// <para>
/// Erzeugt:
/// <list type="bullet">
///   <item><see cref="MeetingPresencePoller"/> (5-s-Polling) + einen TeamsAppReaderProbe</item>
///   <item><see cref="TranscriptionWorker"/> mit Default-Provider
///         (Azure Speech oder Deepgram, je nach <see cref="TranscriptionConfig.Provider"/>)</item>
///   <item>Recorder-Factory, die <see cref="RecordingSession"/> mit dem echten
///         NAudio-Setup baut (<see cref="WasapiAudioRecorderFactory"/> + Default-Devices)</item>
///   <item><see cref="MeetingTrigger"/>, der Poller und Worker verkettet</item>
/// </list>
/// </para>
/// </summary>
public static class MeetingTriggerFactory
{
    /// <summary>
    /// Erstellt die Default-Composition. Privacy-First: wenn
    /// <c>AudioConfig.Enabled=false</c> oder
    /// <c>TeamsConfig.AutoRecordMeetings=false</c>, wird <c>null</c>
    /// zurueckgegeben (Consumer startet dann kein Auto-Recording).
    /// </summary>
    public static MeetingTrigger? TryCreateDefault(AppConfig config, ILogger logger)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (logger is null) throw new ArgumentNullException(nameof(logger));

        // Privacy-First Gate: kein Auto-Recording ohne explizite Opt-in
        if (!config.Audio.Enabled) return null;
        if (!config.AppReader.Teams.Enabled || !config.AppReader.Teams.AutoRecordMeetings) return null;

        // 1. Poller mit TeamsAppReaderProbe
        var probe = new TeamsAppReaderProbe(logger);
        var poller = new MeetingPresencePoller(probe, logger: logger);

        // 2. Transkriptions-Provider je nach Konfiguration
        ITranscriptionProvider provider = TranscriptionConfigResolver
            .ResolveProviderName(config.Transcription) switch
        {
            "deepgram" => new DeepgramTranscriptionProvider(logger),
            _ => new AzureSpeechTranscriptionProvider(logger),
        };

        // 3. Worker mit Default-Pool (max-N parallel = 2)
        var worker = new TranscriptionWorker(provider, maxParallel: 2, logger: logger);

        // 4. Recorder-Factory: baut RecordingSession mit NAudio + Default-Devices
        var deviceProvider = new AudioDeviceProvider();
        var recorderFactory = new WasapiAudioRecorderFactory();
        var resolvedOptions = TranscriptionConfigResolver.ResolveOptions(config.Transcription);
        var storageRoot = string.IsNullOrWhiteSpace(config.Audio.StorageRoot)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AiRecall", "audio")
            : config.Audio.StorageRoot;

        Func<MeetingRecordingContext, RecordingSession> recorderCtxFactory = ctx =>
        {
            var folder = System.IO.Path.Combine(
                storageRoot,
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                $"{DateTime.UtcNow:HHmmss}-{ctx.ChatIdShort}");
            return new RecordingSession(
                triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
                meetingIdShort: ctx.ChatIdShort,
                startedAt: DateTimeOffset.UtcNow,
                topic: ctx.Topic,
                config: config.Audio,
                logger: logger,
                recorderFactory: recorderFactory,
                deviceProvider: deviceProvider);
        };

        // 5. Spec 0014 Iter. 3.1: Manual-Factory fuer Tray-Menu-gestartete Aufnahmen
        //    (Ctrl+Shift+R). Erzeugt eine eigene Session mit RecordingTriggerSource.ManualAudio,
        //    Folder-Key "manual-{guid}", und Default-Topic "Manual recording".
        Func<RecordingSession> manualRecorderFactory = () =>
        {
            var manualIdShort = Guid.NewGuid().ToString("N").Substring(0, 8);
            return new RecordingSession(
                triggerSource: RecordingTriggerSource.ManualAudio,  // Spec 0014 Iter. 3.1
                meetingIdShort: manualIdShort,
                startedAt: DateTimeOffset.UtcNow,
                topic: "Manual recording",
                config: config.Audio,
                logger: logger,
                recorderFactory: recorderFactory,
                deviceProvider: deviceProvider);
        };

        // 6. Trigger
        var trigger = new MeetingTrigger(
            poller, worker, recorderCtxFactory, resolvedOptions, logger, manualRecorderFactory);

        // 6. Poller starten (in Start()-Phase des TriggerSupervisors kommt das)
        logger.Information(
            "MeetingTriggerFactory: Composition erstellt (Provider={Provider}, Audio.Enabled={AudioEnabled})",
            provider.Name, config.Audio.Enabled);

        return trigger;
    }
}
