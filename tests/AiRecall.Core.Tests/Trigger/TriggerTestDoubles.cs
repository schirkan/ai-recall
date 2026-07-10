using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Configuration;
using AiRecall.Transcription;
using AiRecall.Trigger;

namespace AiRecall.Core.Tests.Trigger;

// =============================================================================
// Shared Test-Doubles fuer Trigger-Tests (Spec 0005/0013).
//
// Diese Klassen liefern no-op bzw. minimale Antworten, weil die Tests in
// TriggerServiceTests.cs nicht die Anwesenheitserkennung oder Transkription
// ausfuehren, sondern nur die Privacy-Gates und Composition-Pfade pruefen.
// Wenn ein Test echtes Snapshot-Push-/-Tick-Verhalten braucht, soll er die
// interaktiven Fake-Klassen aus MeetingTriggerTests.cs nutzen.
// =============================================================================

/// <summary>Probe, die dauerhaft "kein Meeting aktiv" liefert.
/// Nicht interaktiv — wer Push-Verhalten braucht, nutzt <c>FakeProbe</c> aus MeetingTriggerTests.</summary>
internal sealed class NoopMeetingProbe : IMeetingPresenceProbe
{
    public Task<MeetingPresenceSnapshot> GetSnapshotAsync(TeamsConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new MeetingPresenceSnapshot(false, null, null, null));
    }
}

/// <summary>Ticker, der unendlich wartet (bis Cancellation) und <c>true</c> liefert
/// solange er nicht disposed ist. Reicht fuer Tests, die den Poller nicht
/// ticken muessen.</summary>
internal sealed class NoopPresenceTicker : IPresenceTicker
{
    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Einfache Wand-Uhr: <c>UtcNow</c> = System-UTC. Reicht fuer Tests,
/// die kein Clock-Advancement brauchen.</summary>
internal sealed class SystemPresenceClock : IPresenceClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Provider, der dauerhaft leere Segmente liefert. Echte Provider-
/// Implementation (Azure / Deepgram) wird nicht gerufen — dieser Double reicht
/// fuer TriggerServiceTests, die nur die Composition / Privacy-Gates pruefen.</summary>
internal sealed class NoopTranscriptionProvider : ITranscriptionProvider
{
    public string Name => "noop";

    public Task<TranscriptionResult> TranscribeAsync(
        string stereoPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
        => Task.FromResult(new TranscriptionResult(
            Segments: new List<TranscriptionSegment>(),
            ProviderName: Name,
            AudioDuration: TimeSpan.Zero,
            SpeakerCount: 0,
            SpeakerLabels: new List<string>(),
            ErrorMessage: null));
}
