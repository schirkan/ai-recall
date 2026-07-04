using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Pdf;

/// <summary>
/// PDF-Viewer-App-Reader (Process-Liste konfigurierbar).
///
/// Iter. 1 (Martin 2026-07-04, Spec 0004 Erweiterung):
///   - Process-Liste aus Config <c>appReader.pdf.processes</c>
///     (Default: Adobe Reader, Acrobat, SumatraPDF, Foxit Reader,
///     PDF-XChange, Microsoft Edge als PDF-Fallback).
///   - Title-Parsing (Filename + Pfad-Extraktion wenn im Titel vorhand).
///   - <b>Kein PDF-Inhalt</b> in Iter. 1 — PDF-Parsing (PdfPig, iTextSharp)
///     waere naechste Iteration (Spec-Hinweis im MD).
///
/// Pfad-Extraktion: Manche PDF-Viewer (SumatraPDF, PDF-XChange) zeigen
/// im Fenstertitel den vollen Pfad, andere (Adobe Reader) nur den
/// Filename. Wir parsen robust: erst Full-Path-Variante, dann Fallback
/// auf Filename-only.
/// </summary>
public sealed class PdfViewerAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses =>
        _supportedProcesses ??= LoadSupportedProcesses();

    public override string DisplayName => "PDF Viewer (Title)";

    private IReadOnlyCollection<string>? _supportedProcesses;

    private IReadOnlyCollection<string> LoadSupportedProcesses()
    {
        // Default-Prozesse (Martin 2026-07-04). Werden in Read() per Config
        // ueberschrieben, falls <c>appReader.pdf.processes</c> gesetzt ist.
        // Da der Reader kein Config-Zugriff im SupportedProcesses-Getter
        // hat, wird die Liste in Read() zusatzlich gefiltert.
        return new[]
        {
            "AcroRd32",      // Adobe Acrobat Reader (32-bit)
            "Acrobat",       // Adobe Acrobat DC
            "SumatraPDF",    // SumatraPDF
            "FoxitReader",   // Foxit Reader
            "PDFXEdit",      // PDF-XChange Editor
            "msedge",        // Microsoft Edge (PDF-Fallback)
            "chrome"         // Google Chrome (PDF-Fallback)
        };
    }

    public override bool CanRead(WindowInfo window)
    {
        // Standard-Check gegen statische Liste
        if (MatchesProcess(window.ProcessName, SupportedProcesses)) return true;
        // Plus: konfigurierte Custom-Processes
        return false; // Custom-Match ueber Read() (s.u.)
    }

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            // Custom-Process-Liste aus Config (falls gesetzt)
            var configuredProcesses = context.Config.AppReader.Pdf?.Processes;
            var isSupported = MatchesProcess(window.ProcessName, SupportedProcesses)
                || (configuredProcesses != null && MatchesProcess(window.ProcessName, configuredProcesses));

            if (!isSupported) return null;

            var (fileName, fullPath, pageInfo) = ParseTitle(window.Title);

            var md = new StringBuilder();
            if (!string.IsNullOrEmpty(fullPath))
            {
                md.AppendLine($"**File:** `{fullPath}`");
            }
            else
            {
                md.AppendLine($"**File (from title):** `{fileName}`");
            }
            if (!string.IsNullOrEmpty(pageInfo))
            {
                md.AppendLine($"**Page:** {pageInfo}");
            }

            md.AppendLine();
            md.AppendLine("_(PDF-Inhalt-Extraktion nicht implementiert — Iter. 2 mit PdfPig / iTextSharp.)_");

            var label = !string.IsNullOrEmpty(fullPath) ? System.IO.Path.GetFileName(fullPath) : fileName;

            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: label,
                ContextKind: "pdf",
                ReaderName: DisplayName,
                ReaderVersion: typeof(PdfViewerAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["fileName"] = fileName,
                    ["filePath"] = fullPath ?? string.Empty,
                    ["hasFullPath"] = (!string.IsNullOrEmpty(fullPath)).ToString(),
                    ["pageInfo"] = pageInfo ?? string.Empty
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "PDF reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>
    /// Parst einen PDF-Viewer-Fenster-Titel.
    /// Versucht, vollen Pfad zu extrahieren (SumatraPDF: <c>"C:\path\to\file.pdf - SumatraPDF"</c>,
    /// PDF-XChange: <c>"file.pdf - PDF-XChange Editor"</c>).
    /// </summary>
    internal static (string FileName, string FullPath, string PageInfo) ParseTitle(string? title)
    {
        const string pageSep = " - Page ";

        if (string.IsNullOrWhiteSpace(title)) return ("(unknown)", string.Empty, string.Empty);

        // Page-Info extrahieren (SumatraPDF: "file.pdf - Page 5 of 10")
        var pageInfo = string.Empty;
        var pageIdx = title.IndexOf(pageSep, StringComparison.OrdinalIgnoreCase);
        if (pageIdx > 0)
        {
            var after = title[(pageIdx + pageSep.Length)..];
            var spaceIdx = after.IndexOf(' ');
            pageInfo = spaceIdx > 0 ? after[..spaceIdx] : after;
            title = title[..pageIdx].TrimEnd();
        }

        // Wenn noch " - " Separator vorhanden: alles davor ist Pfad oder Filename
        var dashIdx = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            var before = title[..dashIdx].Trim();
            if (before.Contains(":\\") || before.Contains(":/"))
            {
                return (System.IO.Path.GetFileName(before), before, pageInfo);
            }
            return (before, string.Empty, pageInfo);
        }

        // Kein Separator: ganzer Titel ist Pfad oder Filename
        if (title.Contains(":\\") || title.Contains(":/"))
        {
            return (System.IO.Path.GetFileName(title), title, pageInfo);
        }

        return (title.Trim(), string.Empty, pageInfo);
    }
}