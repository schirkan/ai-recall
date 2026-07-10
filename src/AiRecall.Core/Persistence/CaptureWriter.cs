using System.Text;
using AiRecall.Core.Models;

namespace AiRecall.Core.Persistence;

/// <summary>
/// Persists a capture as a PNG + Markdown-with-YAML-frontmatter pair under
/// <c>{root}/yyyy-MM-dd/{process}/{HHmmss-fff}.{png,md}</c>.
/// The Markdown embeds a relative link to the screenshot (P-3 from MVP1 spec).
/// Optional <c>{HHmmss-fff}.content.md</c> for structured App-Reader output (Spec 0004).
///
/// File names deliberately do NOT include the window title: titles can be very long
/// (e.g. multi-clause browser tab titles, deeply nested document names) and cause
/// path-length issues on Windows. The window title is preserved verbatim in the
/// YAML frontmatter (<c>title</c>) and as the H1 in the body — only the filename
/// drops it. See spec decision 2026-07-10.
/// </summary>
public static class CaptureWriter
{
    public static CaptureItem Write(
        WindowInfo window,
        byte[] screenshotBytes,
        string contentText,
        string contentHash,
        string captureRoot,
        string? appContext = null,
        WindowInfo? parentWindow = null)
    {
        var timestamp = DateTimeOffset.Now;
        var dayDir = Path.Combine(
            Path.GetFullPath(captureRoot),
            timestamp.ToString("yyyy-MM-dd"),
            SanitizeFileName(window.ProcessName));
        Directory.CreateDirectory(dayDir);

        var baseName = BuildBaseName(timestamp);

        var screenshotPath = Path.Combine(dayDir, baseName + ".png");
        var markdownPath = Path.Combine(dayDir, baseName + ".md");

        File.WriteAllBytes(screenshotPath, screenshotBytes);
        File.WriteAllText(markdownPath, RenderMarkdown(window, timestamp, contentText, contentHash, baseName + ".png", appContext, parentWindow), new UTF8Encoding(false));

        return new CaptureItem(
            timestamp,
            window,
            screenshotPath,
            markdownPath,
            contentText ?? string.Empty,
            contentHash,
            appContext);
    }

    /// <summary>
    /// Schreibt einen initialen Capture ohne OCR-Text und ohne <c>*.content.md</c>.
    /// Setzt <c>conversion: pending</c> im Frontmatter und liefert den Pfad zum MD.
    /// Wird vom Capture-Pfad fuer die Async-Conversion-Pipeline (Spec 0007) verwendet.
    /// </summary>
    public static CaptureItem WritePending(
        WindowInfo window,
        byte[] screenshotBytes,
        string contentHash,
        string captureRoot,
        string? appContext = null,
        WindowInfo? parentWindow = null,
        string? filePath = null,
        string? uiaContent = null)
    {
        var timestamp = DateTimeOffset.Now;
        var dayDir = Path.Combine(
            Path.GetFullPath(captureRoot),
            timestamp.ToString("yyyy-MM-dd"),
            SanitizeFileName(window.ProcessName));
        Directory.CreateDirectory(dayDir);

        var baseName = BuildBaseName(timestamp);

        var screenshotPath = Path.Combine(dayDir, baseName + ".png");
        var markdownPath = Path.Combine(dayDir, baseName + ".md");

        File.WriteAllBytes(screenshotPath, screenshotBytes);
        File.WriteAllText(markdownPath, RenderMarkdownPending(
            window, timestamp, contentHash, baseName + ".png", appContext, parentWindow, filePath, uiaContent),
            new UTF8Encoding(false));

        return new CaptureItem(
            timestamp,
            window,
            screenshotPath,
            markdownPath,
            string.Empty, // leer — OCR/Document kommt async
            contentHash,
            appContext);
    }

    /// <summary>
    /// Updated nur die Conversion-Felder im Frontmatter einer bestehenden MD-Datei.
    /// Body bleibt unangetastet. Wird vom ConversionWorker nach erfolgreicher
    /// (oder fehlgeschlagener) Konvertierung aufgerufen.
    /// </summary>
    public static void UpdateConversionStatus(
        string mdPath,
        string conversionStatus,
        string? conversionError = null,
        string? conversionTimestamp = null,
        string? conversionSteps = null,
        string? converterUsed = null)
    {
        if (!File.Exists(mdPath)) return;

        // Bug-Bash 2026-07-06 I-17: ReadAllText+Split('\n') hinterliess
        // Trailing-\r auf CRLF-Dateien, was mit AppendLine zu \r\r\n fuehrte
        // und "Leerzeilen im Frontmatter" erzeugte. StreamReader mit
        // detectEncodingFromByteOrderMarks=false liest die Zeilen direkt
        // korrekt (ohne Trailing-\r).
        var lines = new List<string>();
        using (var reader = new StreamReader(mdPath))
        {
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
        }

        // Finde Frontmatter-Bereich (zwischen erstem --- und zweitem ---)
        int startIdx = -1, endIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "---")
            {
                if (startIdx == -1) startIdx = i;
                else { endIdx = i; break; }
            }
        }
        if (startIdx == -1 || endIdx == -1) return;

        // Update oder fuege Conversion-Felder hinzu
        var frontmatter = lines.GetRange(startIdx + 1, endIdx - startIdx - 1);
        UpdateOrAddFrontmatterField(frontmatter, "conversion", conversionStatus);
        UpdateOrAddFrontmatterField(frontmatter, "conversionTimestamp", conversionTimestamp ?? DateTimeOffset.Now.ToString("O"));
        if (conversionError != null)
            UpdateOrAddFrontmatterField(frontmatter, "conversionError", conversionError);
        else
            RemoveFrontmatterField(frontmatter, "conversionError");
        if (conversionSteps != null)
            UpdateOrAddFrontmatterField(frontmatter, "conversionSteps", conversionSteps);
        if (converterUsed != null)
            UpdateOrAddFrontmatterField(frontmatter, "converterUsed", converterUsed);

        // Schreibe zurueck. Bug-Bash 2026-07-06 I-17: Split('\n') auf einer
        // CRLF-Datei hinterlaesst an jeder Zeile ein Trailing-\r, das mit
        // AppendLine (das nochmal \r\n anhaengt) zu \r\r\n fuehrt — das wurde
        // bisher als "Leerzeile im Frontmatter" sichtbar. Wir normalisieren
        // auf LF beim Einlesen.
        var sb = new StringBuilder();
        sb.AppendLine("---");
        foreach (var line in frontmatter) sb.AppendLine(line.TrimEnd('\r'));
        sb.AppendLine("---");
        for (int i = endIdx + 1; i < lines.Count; i++) sb.AppendLine(lines[i].TrimEnd('\r'));

        File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Schreibt die Konvertierungs-Sektionen (Document / App-Reader / OCR)
    /// in den Body der bestehenden Capture-MD-Datei. Ersetzt den
    /// <c>_(conversion pending)_</c>-Platzhalter
    /// durch den assemblierten Markdown-Content.
    ///
    /// Bug-Bash 2026-07-06 I-17: Vorher hat der ConversionWorker ein
    /// separates <c>*.conversion.md</c> geschrieben. Das fuehrte zu zwei
    /// Dateien pro Capture, doppelte OCR-Section, und der Original-MD
    /// behielt den Pending-Platzhalter. Jetzt: ein File pro Capture,
    /// Content landet direkt unter <c>## Content</c>.
    /// </summary>
    /// <param name="sections">Bereits gerenderte Markdown-Sektionen
    /// (z. B. <c>## Document content ...</c>, <c>## OCR Content ...</c>).</param>
    public static void WriteConversionContent(string mdPath, IEnumerable<string> sections)
    {
        if (!File.Exists(mdPath)) return;
        var content = File.ReadAllText(mdPath);

        // CRLF normalisieren, damit Replace/IndexOf robust funktioniert.
        content = content.Replace("\r\n", "\n");

        const string placeholder = "_(conversion pending)_";
        var body = string.Join("\n\n", sections);
        string updated;
        if (content.Contains(placeholder))
        {
            updated = content.Replace(placeholder, body);
        }
        else
        {
            // Fallback: Platzhalter wurde bereits ersetzt (z. B. bei
            // doppeltem Enqueue). Content-Section unter ## Content
            // einfuegen, falls vorhanden, sonst anhaengen.
            const string header = "## Content";
            var headerIdx = content.IndexOf('\n' + header + '\n', StringComparison.Ordinal);
            if (headerIdx >= 0)
            {
                var insertAt = headerIdx + 1 + header.Length + 1;
                updated = content.Insert(insertAt, "\n" + body + "\n");
            }
            else
            {
                updated = content.TrimEnd() + "\n\n## Content\n\n" + body + "\n";
            }
        }

        File.WriteAllText(mdPath, updated, new UTF8Encoding(false));
    }

    private static void UpdateOrAddFrontmatterField(List<string> frontmatter, string key, string value)
    {
        for (int i = 0; i < frontmatter.Count; i++)
        {
            var trimmed = frontmatter[i].TrimStart();
            if (trimmed.StartsWith(key + ":"))
            {
                frontmatter[i] = $"{key}: \"{EscapeYaml(value)}\"";
                return;
            }
        }
        frontmatter.Add($"{key}: \"{EscapeYaml(value)}\"");
    }

    private static void RemoveFrontmatterField(List<string> frontmatter, string key)
    {
        frontmatter.RemoveAll(l => l.TrimStart().StartsWith(key + ":"));
    }

    private static string RenderMarkdownPending(
        WindowInfo window,
        DateTimeOffset timestamp,
        string contentHash,
        string screenshotFileName,
        string? appContext,
        WindowInfo? parentWindow,
        string? filePath,
        string? uiaContent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"timestamp: {timestamp:O}");
        sb.AppendLine($"process: \"{EscapeYaml(window.ProcessName)}\"");
        sb.AppendLine($"pid: {window.ProcessId}");
        sb.AppendLine($"hwnd: 0x{window.Handle.ToInt64():X}");
        sb.AppendLine($"title: \"{EscapeYaml(window.Title)}\"");
        if (parentWindow is not null)
        {
            sb.AppendLine($"parentHwnd: 0x{parentWindow.Handle.ToInt64():X}");
            sb.AppendLine($"parentTitle: \"{EscapeYaml(parentWindow.Title)}\"");
            sb.AppendLine($"parentProcess: \"{EscapeYaml(parentWindow.ProcessName)}\"");
        }
        if (!string.IsNullOrEmpty(appContext))
        {
            sb.AppendLine($"context: \"{EscapeYaml(appContext)}\"");
        }
        if (!string.IsNullOrEmpty(filePath))
        {
            sb.AppendLine($"filePath: \"{EscapeYaml(filePath)}\"");
        }
        if (!string.IsNullOrEmpty(uiaContent))
        {
            sb.AppendLine($"uiaContent: \"{EscapeYaml(uiaContent)}\"");
        }
        sb.AppendLine($"screenshot: {screenshotFileName}");
        sb.AppendLine($"hash: {contentHash}");
        sb.AppendLine("conversion: \"pending\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {window.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Process:** `{window.ProcessName}` (PID {window.ProcessId})  ");
        sb.AppendLine($"**Captured:** {timestamp:yyyy-MM-dd HH:mm:ss zzz}");
        if (parentWindow is not null)
        {
            sb.AppendLine($"**Parent window:** `{parentWindow.ProcessName}` — {parentWindow.Title}  ");
        }
        if (!string.IsNullOrEmpty(appContext))
        {
            sb.AppendLine($"**Context:** {appContext}  ");
        }
        if (!string.IsNullOrEmpty(filePath))
        {
            sb.AppendLine($"**File:** `{filePath}`  ");
        }
        sb.AppendLine($"**Screenshot:** [{screenshotFileName}]({screenshotFileName})");
        sb.AppendLine();
        sb.AppendLine("## Content");
        sb.AppendLine();
        sb.AppendLine("_(conversion pending)_");
        return sb.ToString();
    }

    /// <summary>
    /// Rendert den Markdown-Body für eine <see cref="AppContentRecord"/>.
    /// Wird sowohl von <see cref="WriteContent"/> als auch von Tests verwendet.
    /// </summary>
    public static string RenderContentMarkdownBody(
        AppContentRecord result,
        WindowInfo window,
        DateTimeOffset timestamp,
        string? appContext)
    {
        return RenderContentMarkdown(result, window, timestamp, appContext);
    }

    /// <summary>
    /// Schreibt das <see cref="AppContentRecord"/> als zusätzliches
    /// <c>{baseName}.content.md</c> neben dem Capture-MD. Liefert den Pfad,
    /// oder <c>null</c> wenn nichts geschrieben wurde.
    /// </summary>
    public static string? WriteContent(
        AppContentRecord result,
        WindowInfo window,
        DateTimeOffset timestamp,
        string captureRoot,
        string? appContext = null)
    {
        var dayDir = Path.Combine(
            Path.GetFullPath(captureRoot),
            timestamp.ToString("yyyy-MM-dd"),
            SanitizeFileName(window.ProcessName));
        Directory.CreateDirectory(dayDir);

        var baseName = BuildBaseName(timestamp);

        var contentPath = Path.Combine(dayDir, baseName + ".content.md");
        File.WriteAllText(contentPath, RenderContentMarkdown(result, window, timestamp, appContext), new UTF8Encoding(false));
        return contentPath;
    }

    private static string RenderMarkdown(
        WindowInfo window,
        DateTimeOffset timestamp,
        string contentText,
        string contentHash,
        string screenshotFileName,
        string? appContext,
        WindowInfo? parentWindow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"timestamp: {timestamp:O}");
        sb.AppendLine($"process: \"{EscapeYaml(window.ProcessName)}\"");
        sb.AppendLine($"pid: {window.ProcessId}");
        sb.AppendLine($"hwnd: 0x{window.Handle.ToInt64():X}");
        sb.AppendLine($"title: \"{EscapeYaml(window.Title)}\"");
        if (parentWindow is not null)
        {
            sb.AppendLine($"parentHwnd: 0x{parentWindow.Handle.ToInt64():X}");
            sb.AppendLine($"parentTitle: \"{EscapeYaml(parentWindow.Title)}\"");
            sb.AppendLine($"parentProcess: \"{EscapeYaml(parentWindow.ProcessName)}\"");
        }
        if (!string.IsNullOrEmpty(appContext))
        {
            sb.AppendLine($"context: \"{EscapeYaml(appContext)}\"");
        }
        sb.AppendLine($"screenshot: {screenshotFileName}");
        sb.AppendLine($"hash: {contentHash}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {window.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Process:** `{window.ProcessName}` (PID {window.ProcessId})  ");
        sb.AppendLine($"**Captured:** {timestamp:yyyy-MM-dd HH:mm:ss zzz}");
        if (parentWindow is not null)
        {
            sb.AppendLine($"**Parent window:** `{parentWindow.ProcessName}` — {parentWindow.Title}  ");
        }
        if (!string.IsNullOrEmpty(appContext))
        {
            sb.AppendLine($"**Context:** {appContext}  ");
        }
        sb.AppendLine($"**Screenshot:** [{screenshotFileName}]({screenshotFileName})");
        sb.AppendLine();
        sb.AppendLine("## Content");
        sb.AppendLine();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            sb.AppendLine("_(no text content extracted)_");
        }
        else
        {
            sb.AppendLine("```");
            sb.AppendLine(contentText.TrimEnd());
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        var s = sb.ToString().Trim();
        if (s.Length > 80) s = s[..80];
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>
    /// Baut den <c>{baseName}</c>-Anteil des Capture-Dateinamens (ohne Extension).
    /// Bewusst OHNE Fenstertitel — der Titel landet im YAML-Frontmatter und als H1 im
    /// Body, nicht im Filename. Gründe: (1) Browser-Tab-Titel können beliebig lang
    /// sein (Multi-Clause-Strings), (2) Sonderzeichen-Sanitisierung ist fehleranfällig,
    /// (3) Window-Titel können sich zwischen Trigger und Conversion unterscheiden,
    ///   was die Korrelation Capture ↔ Content unnötig kompliziert.
    /// Kollisionen mehrerer Captures in derselben Millisekunde sind durch das
    /// <c>fff</c>-Segment extrem unwahrscheinlich; bei Bedarf kann später
    /// ein Counter-Suffix angehängt werden.
    /// </summary>
    private static string BuildBaseName(DateTimeOffset timestamp)
    {
        return timestamp.ToString("HHmmss-fff");
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string RenderContentMarkdown(
        AppContentRecord result,
        WindowInfo window,
        DateTimeOffset timestamp,
        string? appContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"timestamp: {timestamp:O}");
        sb.AppendLine($"kind: \"{EscapeYaml(result.ContextKind ?? "unknown")}\"");
        sb.AppendLine($"reader: \"{EscapeYaml(result.ReaderName)}\"");
        sb.AppendLine($"readerVersion: \"{EscapeYaml(result.ReaderVersion)}\"");
        if (!string.IsNullOrEmpty(result.ContextLabel))
        {
            sb.AppendLine($"context: \"{EscapeYaml(result.ContextLabel)}\"");
        }
        sb.AppendLine($"process: \"{EscapeYaml(window.ProcessName)}\"");
        sb.AppendLine($"pid: {window.ProcessId}");
        if (result.Extra is { Count: > 0 })
        {
            foreach (var kv in result.Extra)
            {
                sb.AppendLine($"{kv.Key}: \"{EscapeYaml(kv.Value)}\"");
            }
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {result.ContextKind ?? "App-Reader"} — {window.ProcessName}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(result.ContextLabel))
        {
            sb.AppendLine($"**Context:** {result.ContextLabel}");
            sb.AppendLine();
        }
        sb.AppendLine(result.ContentMarkdown.TrimEnd());
        return sb.ToString();
    }
}
