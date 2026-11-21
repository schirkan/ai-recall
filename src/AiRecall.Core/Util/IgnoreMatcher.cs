using AiRecall.Core.Configuration;
using AiRecall.Core.Models;

namespace AiRecall.Core.Util;

/// <summary>
/// Blacklist-based matcher: returns <c>true</c> if a capture candidate matches
/// any of the configured ignore patterns. Matching is case-insensitive substring.
/// Used by both the one-shot and continuous capture paths.
/// </summary>
public static class IgnoreMatcher
{
    public static bool IsIgnored(WindowInfo window, string? appContext, ScreenRecorderConfig config) =>
        IsWindowIgnored(window, config) || IsUrlIgnored(appContext, config);

    /// <summary>Evaluate metadata before capturing a screenshot.</summary>
    public static bool IsWindowIgnored(WindowInfo window, ScreenRecorderConfig config) =>
        MatchesAny(window.ProcessName, config.IgnoreApps) || MatchesAny(window.Title, config.IgnoreWindowTitles);

    /// <summary>Evaluate the known URL before committing any capture files.</summary>
    public static bool IsUrlIgnored(string? url, ScreenRecorderConfig config) => MatchesAny(url, config.IgnoreUrls);

    private static bool MatchesAny(string? value, IReadOnlyCollection<string>? patterns)
    {
        if (string.IsNullOrEmpty(value) || patterns is null || patterns.Count == 0) return false;
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (value.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
