using System.Runtime.InteropServices;
using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Notepad;

/// <summary>
/// Liest den Buffer des Windows-Notepads via Win32 <c>WM_GETTEXT</c> +
/// Fenster-Titel-Parsing für den Dateinamen.
///
/// Spart Tesseract-OCR (~500 ms/Capture) und liefert 100% korrekten Text
/// statt OCR-Artefakte.
/// </summary>
public sealed class NotepadAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "Notepad" };
    public override string DisplayName => "Notepad (Win32 WM_GETTEXT)";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETLINECOUNT = 0x00BA;

    // Edit-Control-Klassen, die Notepad in verschiedenen Windows-Versionen nutzt.
    private static readonly string[] EditClasses = { "RichEditD2DPT", "Edit", "RichEdit20W" };

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var maxBufferBytes = context.Config.AppReader.Notepad.MaxBufferKB * 1024;
            var edit = FindEditControl(window.Handle);

            var textLength = (int)SendMessage(edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            var lineCount = (int)SendMessage(edit, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);

            string text;
            if (textLength <= 0)
            {
                text = string.Empty;
            }
            else
            {
                var charsToAlloc = Math.Min(textLength + 1, (maxBufferBytes / 2) + 1);
                var ptr = Marshal.AllocHGlobal((charsToAlloc + 1) * 2);
                try
                {
                    var copied = (int)SendMessage(edit, WM_GETTEXT, (IntPtr)charsToAlloc, ptr);
                    text = copied > 0 ? Marshal.PtrToStringUni(ptr, copied) ?? string.Empty : string.Empty;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            var (fileName, isUntitled) = ParseTitle(window.Title);

            var md = new StringBuilder();
            md.AppendLine($"**File:** {(isUntitled ? "_(untitled)_" : fileName)}");
            md.AppendLine($"**Lines:** {lineCount}");
            md.AppendLine($"**Chars:** {text.Length}");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(text);
            md.AppendLine("```");

            var label = isUntitled ? "(untitled)" : fileName;

            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: label,
                ContextKind: "buffer",
                ReaderName: DisplayName,
                ReaderVersion: typeof(NotepadAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["fileName"] = fileName,
                    ["isUntitled"] = isUntitled.ToString(),
                    ["lineCount"] = lineCount.ToString()
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Notepad reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    private static (string FileName, bool IsUntitled) ParseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("(untitled)", true);

        // Patterns (En-Dash U+2013 UND ASCII-Hyphen U+002D werden akzeptiert):
        //   "filename.txt - Notepad"
        //   "filename.txt – Notepad"
        //   "filename.txt - Notepad (Administrator)"
        //   "*filename.txt - Notepad"   (unsaved marker)
        //   "Untitled - Notepad"
        const string hyphen = "-";
        const string enDash = "\u2013"; // –
        const string emDash = "\u2014"; // —
        string[] separators = { $" {hyphen} ", $" {enDash} ", $" {emDash} " };

        foreach (var sep in separators)
        {
            var idx = title.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var name = title[..idx].Trim();
                // unsaved marker
                if (name.StartsWith("*")) name = name[1..].Trim();
                // (Administrator) suffix (case-insensitive)
                const string adminSuffix = " (Administrator)";
                if (name.EndsWith(adminSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^adminSuffix.Length].Trim();
                }
                if (string.Equals(name, "Untitled", StringComparison.OrdinalIgnoreCase))
                    return ("Untitled", true);
                return (string.IsNullOrEmpty(name) ? "(untitled)" : name, false);
            }
        }

        // Kein " - Notepad"-Suffix — gib Titel unverändert zurück.
        var fallback = title.StartsWith("*") ? title[1..] : title;
        return (fallback, false);
    }

    private static IntPtr FindEditControl(IntPtr parent)
    {
        // Rekursive Suche via EnumChildWindows. Bei modernem Win11-Notepad
        // ist das Edit-Control einige Hierarchie-Ebenen tiefer.
        foreach (var cls in EditClasses)
        {
            var found = IntPtr.Zero;
            EnumChildWindows(parent, (hWnd, _) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                if (sb.ToString() == cls)
                {
                    found = hWnd;
                    return false; // stop enumeration
                }
                return true; // continue
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
        }
        // Fallback: direkt auf das Window selbst senden (manche älteren Hosts).
        return parent;
    }
}