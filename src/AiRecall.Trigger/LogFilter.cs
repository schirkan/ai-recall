using Serilog.Events;

namespace AiRecall.Trigger;

/// <summary>
/// Pure-logic filter for <see cref="LogEventEntry"/>s. Used by
/// <c>LogviewerWindow</c> to decide which events to display (Spec 0008).
/// Lives outside the WinForms UI so it can be unit-tested without UI.
/// </summary>
public sealed class LogFilter
{
    /// <summary>Minimum level to include; <c>null</c> = include all levels.</summary>
    public LogEventLevel? MinLevel { get; set; }

    /// <summary>Substring filter on <see cref="LogEventEntry.Message"/>; case-insensitive; <c>null</c> = no filter.</summary>
    public string? SearchText { get; set; }

    /// <summary>True if the entry matches the current filter (level + search).</summary>
    public bool Matches(LogEventEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (MinLevel.HasValue && entry.Level < MinLevel.Value) return false;

        if (!string.IsNullOrEmpty(SearchText))
        {
            if (entry.Message.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }

        return true;
    }

    public LogFilter Clone() => new()
    {
        MinLevel = MinLevel,
        SearchText = SearchText
    };
}