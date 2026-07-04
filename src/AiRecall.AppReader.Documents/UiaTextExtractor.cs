using System.Text;
using System.Windows.Automation;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Extrahiert Text aus einem Fenster via UIA (System.Windows.Automation).
///
/// Liefert plainen Text, der via <c>TextPattern</c> aus allen
/// Text-Container-Elementen im Fenster gesammelt wird. Bei Fehlern
/// (kein UIA, kein TextPattern, leerer Inhalt) gibt die Methode
/// <c>null</c> zurück — die Reader koennen dann auf Title-only-Fallback
/// zurueckfallen.
///
/// Real-Office-Smoke-Tests entfallen in der Sandbox (Martin 2026-07-04);
/// dieser Code laeuft nur auf Maschinen mit Office / WPF-Stack, dort aber
/// ohne weitere Konfiguration.
/// </summary>
internal static class UiaTextExtractor
{
    /// <summary>
    /// Sammelt Text aus dem Fenster <paramref name="hwnd"/>, begrenzt auf
    /// <paramref name="maxChars"/> Zeichen. Liefert <c>null</c>, wenn kein
    /// Text gefunden wurde oder ein Fehler aufgetreten ist.
    /// </summary>
    public static string? TryExtract(IntPtr hwnd, int maxChars)
    {
        if (hwnd == IntPtr.Zero || maxChars <= 0) return null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var sb = new StringBuilder();
            Walk(root, sb, maxChars);
            var text = sb.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // UIA-Fehler (z. B. Element weg, COM-Exception). Caller faellt
            // auf Title-only zurueck.
            return null;
        }
    }

    private static void Walk(AutomationElement element, StringBuilder sb, int maxChars)
    {
        if (sb.Length >= maxChars) return;

        try
        {
            if (TryGetText(element, out var t) && !string.IsNullOrWhiteSpace(t))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.Trim());
                if (sb.Length >= maxChars) return;
            }
        }
        catch { /* ignore — Kinder trotzdem probieren */ }

        try
        {
            var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                if (sb.Length >= maxChars) return;
                Walk(child, sb, maxChars);
            }
        }
        catch { /* Baum nicht traversierbar — fertig */ }
    }

    private static bool TryGetText(AutomationElement element, out string text)
    {
        text = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern tp)
            {
                text = tp.DocumentRange.GetText(-1) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern vpv)
            {
                text = vpv.Current.Value ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
            }
        }
        catch { /* pattern not supported */ }
        return false;
    }
}