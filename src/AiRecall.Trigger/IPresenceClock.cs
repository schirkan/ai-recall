using System;

namespace AiRecall.Trigger;

/// <summary>
/// Abstraktion ueber die Uhr fuer den Debounce-Timer. Produktion:
/// <see cref="SystemPresenceClock"/>; Tests: Fake-Clock zum kontrollierten
/// Vorruecken der Zeit.
/// </summary>
public interface IPresenceClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemPresenceClock : IPresenceClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
