using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiRecall.Trigger;

/// <summary>
/// Quelle einer Audio-Aufnahme. Wird vom <see cref="MeetingTrigger"/> bei jedem
/// <see cref="RecordingStateChangedEventArgs"/> mitgeliefert, damit Konsumenten
/// (Tray-Icon, UI) zwischen automatischer Meeting-Aufnahme und manueller
/// User-Aufnahme unterscheiden koennen.
/// </summary>
public enum RecordingSource
{
    /// <summary>
    /// Aufnahme wurde automatisch durch die <see cref="MeetingPresencePoller"/>-
    /// Erkennung gestartet (Spec 0013 v0.3 §1).
    /// </summary>
    MeetingAuto,

    /// <summary>
    /// Aufnahme wurde manuell durch den User gestartet (Spec 0014 — z. B.
    /// fuer Meetings ausserhalb von Teams).
    /// </summary>
    Manual,
}

/// <summary>
/// EventArgs fuer <see cref="IRecordingControl.RecordingStateChanged"/>.
/// <see cref="Key"/> ist der eindeutige Session-Key (ChatIdShort bei
/// <see cref="RecordingSource.MeetingAuto"/>, <c>"manual-{guid}"</c> bei
/// <see cref="RecordingSource.Manual"/>).
/// <see cref="Topic"/> ist bei manuellen Aufnahmen <c>null</c>.
/// </summary>
public sealed record RecordingStateChangedEventArgs(
    bool IsRecording,
    RecordingSource Source,
    string Key,
    string? Topic,
    DateTimeOffset At);

/// <summary>
/// Public API fuer Audio-Recording-Steuerung. Wird vom <see cref="MeetingTrigger"/>
/// implementiert und von der Tray-UI (Spec 0014) sowie von externen Tests
/// konsumiert.
/// <para>
/// Getrennt von <see cref="MeetingTrigger"/>'s Lifecycle-API
/// (<see cref="MeetingTrigger.Start"/>/<see cref="MeetingTrigger.Stop"/> =
/// Poller-Event-Subscription), weil Recording-Steuerung eine andere
/// Verantwortlichkeit ist als Trigger-Subscription.
/// </para>
/// </summary>
public interface IRecordingControl
{
    /// <summary>
    /// True, wenn aktuell mindestens eine Aufnahme laeuft (Meeting-Auto oder manuell).
    /// </summary>
    bool IsRecording { get; }

    /// <summary>
    /// Feuert bei jedem Aufnahme-Start und -Stop, fuer Auto- und Manual-Aufnahmen.
    /// Mehrere parallele Aufnahmen fuehren zu mehreren Events (z. B. Auto+Manual
    /// gleichzeitig moeglich, wenn Poller ein Meeting erkennt waehrend manuell
    /// aufgenommen wird).
    /// </summary>
    event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

    /// <summary>
    /// Startet eine manuelle Audio-Aufnahme (auch ohne Meeting-Kontext). Erzeugt
    /// eine neue <see cref="AiRecall.Core.Audio.RecordingSession"/> via der
    /// injizierten Factory, feuert <see cref="RecordingStateChanged"/> mit
    /// <see cref="RecordingSource.Manual"/>. Returnwert ist der eindeutige
    /// Session-Key (Format <c>"manual-{guid}"</c>) fuer spaeteren
    /// <see cref="StopAsync(string?)"/>.
    /// <para>
    /// Wirft <see cref="NotSupportedException"/>, wenn der Trigger ohne
    /// Manual-Factory konstruiert wurde (Rueckwaertskompatibilitaet mit
    /// Spec 0013 v0.3 Tests).
    /// </para>
    /// </summary>
    /// <param name="ct">CancellationToken. Wird nicht an die Recording-Session
    /// weitergereicht (die hat ihren eigenen Lifecycle).</param>
    Task<string> StartManualAsync(CancellationToken ct);

    /// <summary>
    /// Stoppt eine oder alle aktiven Aufnahmen. Nach dem Stop wird der
    /// resultierende Audio-Transkriptions-Task automatisch im
    /// <see cref="AiRecall.Transcription.TranscriptionWorker"/> enqueued
    /// (analog Auto-Recording, Spec 0013 v0.3 §5.4).
    /// </summary>
    /// <param name="key">
    /// <c>null</c> = alle aktiven Sessions stoppen. Sonst nur die Session
    /// mit dem angegebenen Key (Meeting-Auto-Key = ChatIdShort,
    /// Manual-Key = <c>"manual-{guid}"</c>).
    /// </param>
    Task StopAsync(string? key = null);
}