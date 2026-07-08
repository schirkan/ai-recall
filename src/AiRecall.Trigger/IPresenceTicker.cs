using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiRecall.Trigger;

/// <summary>
/// Abstraktion ueber die Polling-Tick-Quelle. Produktion: <see cref="PeriodicPresenceTicker"/>
/// (wrappt <see cref="System.Threading.PeriodicTimer"/>). Tests: Fake-Ticker,
/// der Ticks manuell ausloest.
/// </summary>
public interface IPresenceTicker : IAsyncDisposable
{
    /// <summary>
    /// Wartet auf den naechsten Tick. Liefert <c>false</c>, wenn der Timer
    /// disposet/canceled wurde.
    /// </summary>
    ValueTask<bool> WaitForNextTickAsync(CancellationToken ct);
}

/// <summary>
/// Produktions-Implementierung: <see cref="System.Threading.PeriodicTimer"/>.
/// (PeriodicTimer hat in .NET 8 keine DisposeAsync-Methode — wir exposen
/// trotzdem <see cref="IAsyncDisposable"/> fuer eine einheitliche Ticker-API.)
/// </summary>
public sealed class PeriodicPresenceTicker : IPresenceTicker
{
    private readonly System.Threading.PeriodicTimer _timer;

    public PeriodicPresenceTicker(TimeSpan interval)
    {
        _timer = new System.Threading.PeriodicTimer(interval);
    }

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken ct) => _timer.WaitForNextTickAsync(ct);

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
