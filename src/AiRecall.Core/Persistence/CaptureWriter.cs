using System.Text;
using AiRecall.Core.Models;

namespace AiRecall.Core.Persistence;

/// <summary>
/// Persists a capture as a PNG + Markdown-with-YAML-frontmatter pair under
/// <c>{root}/yyyy-MM-dd/{process}/{HHmmss-fff}-{title-slug}.{png,md}</c>.
/// The Markdown embeds a relative link to the screenshot (P-3 from MVP1 spec).
/// Optional <c>*.content.md</c> for structured App-Reader output (Spec 0004).
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

        var stamp = timestamp.ToString("HHmmss-fff");
        var titleSlug = SanitizeFileName(string.IsNullOrWhiteSpace(window.Title) ? "untitled" : window.Title);
        var baseName = $"{stamp}-{titleSlug}";

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

        var stamp = timestamp.ToString("HHmmss-fff");
        var titleSlug = SanitizeFileName(string.IsNullOrWhiteSpace(window.Title) ? "untitled" : window.Title);
        var baseName = $"{stamp}-{titleSlug}";

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
