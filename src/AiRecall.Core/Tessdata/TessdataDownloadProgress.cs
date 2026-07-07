namespace AiRecall.Core.Tessdata;

/// <summary>
/// Fortschritts-Report für laufenden tessdata-Download (Spec 0012).
/// Wird via <see cref="IProgress{T}"/> an den Caller gemeldet.
/// </summary>
public sealed record TessdataDownloadProgress(
    int CompletedCount,
    int TotalCount,
    long TotalBytesReceived,
    string? CurrentLanguage)
{
    /// <summary>Anzahl bereits abgeschlossener Downloads (1-basiert nach erstem Complete).</summary>
    public int CompletedCount { get; init; } = CompletedCount;

    /// <summary>Gesamtzahl der geplanten Downloads.</summary>
    public int TotalCount { get; init; } = TotalCount;

    /// <summary>Summe aller bereits empfangenen Bytes (über alle Downloads).</summary>
    public long TotalBytesReceived { get; init; } = TotalBytesReceived;

    /// <summary>Sprachcode des aktuell laufenden Downloads (null zwischen zwei Downloads).</summary>
    public string? CurrentLanguage { get; init; } = CurrentLanguage;
}