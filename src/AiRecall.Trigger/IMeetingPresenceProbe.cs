using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Configuration;

namespace AiRecall.Trigger;

/// <summary>
/// Abstraktion ueber den Teams-Meeting-Presence-Snapshot (Spec 0013 v0.3 §1).
/// Produktion: <c>TeamsAppReaderProbe</c> wrappt <see cref="TeamsAppReader.TryGetActiveMeetingAsync"/>.
/// Tests: <c>FakeProbe</c> liefert vorprogrammierte Snapshots.
/// </summary>
public interface IMeetingPresenceProbe
{
    /// <summary>
    /// Liefert den aktuellen Teams-Meeting-Snapshot.
    /// Implementierungen MUSSEN <paramref name="ct"/> respektieren und
    /// bei Cancellation sauber abbrechen.
    /// </summary>
    Task<MeetingPresenceSnapshot> GetSnapshotAsync(TeamsConfig cfg, CancellationToken ct);
}
