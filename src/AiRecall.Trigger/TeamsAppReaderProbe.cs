using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Configuration;

using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Produktions-Implementierung von <see cref="IMeetingPresenceProbe"/>:
/// delegiert an <see cref="TeamsAppReader.TryGetActiveMeetingAsync"/>.
/// </summary>
public sealed class TeamsAppReaderProbe : IMeetingPresenceProbe
{
    private readonly ILogger? _logger;

    public TeamsAppReaderProbe(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<MeetingPresenceSnapshot> GetSnapshotAsync(TeamsConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // TeamsAppReader.TryGetActiveMeetingAsync ist im Kern synchron (Process-IO),
        // aber als Task<...> deklariert. Hier awaiten wir fuer einheitliches
        // Cancel-Handling.
        return await TeamsAppReader.TryGetActiveMeetingAsync(cfg, _logger, ct).ConfigureAwait(false);
    }
}
