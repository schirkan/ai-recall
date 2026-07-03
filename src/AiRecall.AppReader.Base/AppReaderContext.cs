using AiRecall.Core.Configuration;
using Serilog;

namespace AiRecall.AppReader.Base;

/// <summary>
/// Kontext, der jedem App-Reader-Aufruf mitgegeben wird. Enthält Config und Logger.
/// </summary>
public sealed class AppReaderContext
{
    public required AppConfig Config { get; init; }
    public required ILogger Logger { get; init; }
    public CancellationToken CancellationToken { get; init; } = default;
}