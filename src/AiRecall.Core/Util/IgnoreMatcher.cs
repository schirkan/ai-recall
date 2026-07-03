using AiRecall.Core.Configuration;
using AiRecall.Core.Models;

namespace AiRecall.Core.Util;

/// <summary>
/// Blacklist-based matcher: returns <c>true</c> if a capture candidate matches
/// any of the configured ignore patterns. Matching is case-insensitive substring.
/// Used by the active-window one-shot and (later) the continuous record loop.
/// </summary>
public static class IgnoreMatcher
{
    public static bool IsIgnored(WindowInfo window, string? appContext, ScreenRecorderConfig config)
    {
        if (MatchesAny(window.ProcessName, config.IgnoreApps)) return true;
        if (MatchesAny(window.Title, config.IgnoreWindowTitles)) return true;
        if (!string.IsNullOrEmpty(appContext) && MatchesAny(appContext, config.IgnoreUrls)) return true;
        return false;
    }

    private static bool MatchesAny(string? value, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrEmpty(value) || patterns.Count == 0) return false;
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (value.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
