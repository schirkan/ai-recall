namespace AiRecall.Trigger;

/// <summary>
/// Pure-logic mapping from a <see cref="TriggerState"/> + counters to the
/// user-visible state of the tray icon (which menu items are enabled, what
/// the status text says, etc.). Lives outside the WinForms
/// <c>TrayIconController</c> so it can be unit-tested without WinForms
/// (Spec 0006 Schritt 4).
/// </summary>
public sealed record TrayIconState(
    bool StartEnabled,
    bool StopEnabled,
    string StatusText,
    string TooltipText,
    string IconGlyph)
{
    /// <summary>
    /// Maps the supervisor state + counters to a tray-icon view-model.
    /// </summary>
    public static TrayIconState FromSupervisor(
        TriggerState supervisorState,
        int captureCount,
        int crashCount)
    {
        return supervisorState switch
        {
            TriggerState.Stopped => new TrayIconState(
                StartEnabled: true,
                StopEnabled: false,
                StatusText: "Stopped",
                TooltipText: crashCount > 0
                    ? $"AiRecall — Stopped ({crashCount} prior crashes)"
                    : "AiRecall — Stopped",
                IconGlyph: "🔴"),

            TriggerState.Starting => new TrayIconState(
                StartEnabled: false,
                StopEnabled: false,
                StatusText: "Starting…",
                TooltipText: "AiRecall — Starting…",
                IconGlyph: "🟡"),

            TriggerState.Running => new TrayIconState(
                StartEnabled: false,
                StopEnabled: true,
                StatusText: $"Running — {captureCount} captures",
                TooltipText: $"AiRecall — Running ({captureCount} captures today)",
                IconGlyph: "🟢"),

            TriggerState.Stopping => new TrayIconState(
                StartEnabled: false,
                StopEnabled: false,
                StatusText: "Stopping…",
                TooltipText: "AiRecall — Stopping…",
                IconGlyph: "🟡"),

            TriggerState.Crashed => new TrayIconState(
                StartEnabled: true,
                StopEnabled: false,
                StatusText: "⚠ Crashed",
                TooltipText: crashCount > 0
                    ? $"AiRecall — Crashed ({crashCount} crashes, manual restart)"
                    : "AiRecall — Crashed (manual restart needed)",
                IconGlyph: "⚠"),

            _ => throw new ArgumentOutOfRangeException(nameof(supervisorState), supervisorState, null)
        };
    }
}