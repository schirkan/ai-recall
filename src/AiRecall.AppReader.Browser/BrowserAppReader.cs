using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Browser;

/// <summary>
/// Liest den Tab-Titel + URL aus Edge / Chrome.
///
/// Strategie (Spec 0004):
///   1. Tab-Titel aus dem Fenster-Titel parsen ("Page Title — Browser").
///   2. URL via UIA <c>ValuePattern</c> auf das Address-Bar-Edit-Control ("Address and search bar").
///      Schlägt fehl, wenn das Browser-Profil UIA nicht exponiert (z. B. Edge in
///      „Windows-Benutzeroberfläche"-Hosts) — dann nur Titel, kein Crash.
///   3. Body-Content via UIA <c>TextPattern</c> auf das Document-Control (optional, hart gekappt).
/// </summary>
public sealed class BrowserAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "msedge", "chrome" };
    public override string DisplayName => "Browser (UIA + Title-Parsing)";

    // Bekannte Suffixe, die Browser an den Tab-Titel hängen.
// WICHTIG: Längste zuerst! Sonst matched " - Microsoft Edge" vor
// " - InPrivate - Microsoft Edge" und "InPrivate" bleibt im Titel.
    private static readonly string[] BrowserSuffixes =
    {
        " - InPrivate - Microsoft\u00A0Edge",  // non-breaking space
        " - InPrivate - Microsoft Edge",
        " - Microsoft\u00A0Edge",
        " - Microsoft Edge",
        " - Google Chrome",
        " - Chrome",
        " - Mozilla Firefox"            // Firefox fällt hier zwar nicht hin, schadet nicht
    };

    private static readonly Regex UrlRegex = new(
        @"^(https?://[^\s]+|file://[^\s]+|about:[^\s]+|chrome://[^\s]+|edge://[^\s]+|chrome-extension://[^\s]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var (tabTitle, browserSuffix) = ParseTitle(window.Title);
            var url = TryReadUrlViaUia(window.Handle);
            var body = TryReadBodyViaUia(window.Handle, context.Config.AppReader.Browser.MaxTextLengthKB * 1024);

            // Wenn weder URL noch Body, lohnt sich der Capture vermutlich nicht — wir geben
            // trotzdem was zurück, damit der User überhaupt mitbekommt, dass sein Browser aktiv war.
            var md = new StringBuilder();
            md.AppendLine($"**Tab title:** {tabTitle}");
            md.AppendLine($"**URL:** {(url ?? "_(not exposed via UIA)_")}");
            md.AppendLine($"**Browser suffix:** {browserSuffix}");
            if (!string.IsNullOrEmpty(body))
            {
                md.AppendLine();
                md.AppendLine($"**Body** (first {Math.Min(body.Length, context.Config.AppReader.Browser.MaxTextLengthKB * 1024)} chars):");
                md.AppendLine();
                md.AppendLine("```");
                md.AppendLine(body);
                md.AppendLine("```");
            }

            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: url ?? tabTitle,
                ContextKind: "url",
                ReaderName: DisplayName,
                ReaderVersion: typeof(BrowserAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["tabTitle"] = tabTitle,
                    ["url"] = url ?? "",
                    ["process"] = window.ProcessName
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Browser reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    private static (string Title, string Suffix) ParseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("", "");

        foreach (var suffix in BrowserSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = title[..^suffix.Length].Trim();
                return (string.IsNullOrEmpty(stripped) ? title : stripped, suffix);
            }
        }

        // Kein bekannter Suffix — gib Titel unverändert zurück, Suffix leer.
        return (title, "");
    }

    private static string? TryReadUrlViaUia(IntPtr hWnd)
    {
        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element is null) return null;

            // Suche nach dem Address-Bar-Edit. UIA ClassName variiert pro Browser,
            // aber Name ist standardisiert in Chromium-basierten Browsern.
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true));

            var edits = element.FindAll(TreeScope.Descendants, cond);
            foreach (AutomationElement edit in edits)
            {
                var name = edit.Current.Name ?? string.Empty;
                if (name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("URL", StringComparison.OrdinalIgnoreCase))
                {
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) &&
                        patternObj is ValuePattern vp)
                    {
                        var value = vp.Current.Value;
                        if (!string.IsNullOrWhiteSpace(value) && UrlRegex.IsMatch(value))
                        {
                            return value;
                        }
                    }
                }
            }
            return null;
        }
        catch
        {
            // UIA kann auf manchen Edge-Builds Exceptions werfen (z. B. wenn das Window
            // in einem fremden Thread liegt). Wir geben stillschweigend auf.
            return null;
        }
    }

    private static string? TryReadBodyViaUia(IntPtr hWnd, int maxChars)
    {
        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element is null) return null;

            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
            var doc = element.FindFirst(TreeScope.Descendants, cond);
            if (doc is null) return null;

            if (doc.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                patternObj is TextPattern tp)
            {
                var range = tp.DocumentRange;
                var text = range.GetText(maxChars);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}