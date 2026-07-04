using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// COM-Interop-Wrapper fuer die Office-Apps (Word/Excel/PowerPoint).
///
/// Strategie: <b>late binding</b> via ProgID + <c>Type.InvokeMember</c>.
/// COM-Verbindung zur laufenden Instanz ueber P/Invoke auf
/// <c>oleaut32.dll!GetActiveObject</c> (in .NET 8 SDK 8.0.422 ist
/// <c>Marshal.GetActiveObject</c> nicht mehr verfuegbar — P/Invoke ist
/// der robuste Weg). Keine PIAs / NuGet-Pakete noetig; die Office-
/// Anwendung muss nur auf dem Zielsystem installiert sein.
///
/// Bei Fehlern (kein Office, andere Instanz aktiv, COM-Exception) wird
/// <c>null</c> zurueckgegeben — die Reader fallen dann auf die UIA+Title-
/// Logik zurueck. <b>Niemals crashen</b>.
///
/// Spec 0004 Iter. Documents Schritt 2 (Martin 2026-07-04): COM liefert
/// den echten <c>FullPath</c> der geoeffneten Datei (UIA nicht) und
/// optional den Inhalt. Auf Office-Maschinen deutlich reichhaltiger als
/// UIA-only.
/// </summary>
internal static class OfficeComInterop
{
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.Interface)] out object ppunk);

    /// <summary>
    /// Liefert die laufende COM-Instanz fuer <paramref name="progId"/> oder
    /// <c>null</c>, wenn nicht vorhanden / nicht erreichbar.
    /// </summary>
    private static object? GetActiveInstance(string progId)
    {
        try
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type == null) return null;
            GetActiveObject(type.GUID, IntPtr.Zero, out var obj);
            return obj;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>
    /// Ergebnis eines COM-Lookups. <see cref="FullPath"/> ist nie null
    /// bei Erfolg. <see cref="Text"/> kann null/leer sein (z. B. leere
    /// Datei). <see cref="Error"/> enthaelt eine Diagnosemeldung bei
    /// teilweisem Erfolg (FullPath gelesen, Text fehlgeschlagen).
    /// </summary>
    public sealed record OfficeDocumentInfo(
        string FullPath,
        string? Text,
        string? Error = null);

    /// <summary>Word: liefert FullName + Range.Text (Plain).</summary>
    public static OfficeDocumentInfo? TryGetWordInfo()
    {
        return TryGet(
            progId: "Word.Application",
            documentProperty: "ActiveDocument",
            rangeProperty: "Content",
            textProperty: "Text",
            rowProperty: null);
    }

    /// <summary>Excel: liefert FullName + UsedRange als Markdown-Tabelle.</summary>
    public static OfficeDocumentInfo? TryGetExcelInfo()
    {
        var info = TryGet(
            progId: "Excel.Application",
            documentProperty: "ActiveWorkbook",
            rangeProperty: "ActiveSheet",
            textProperty: "UsedRange",
            rowProperty: null);

        if (info == null) return null;

        // UsedRange als 2D-Array lesen und in Markdown-Tabelle konvertieren
        try
        {
            var excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null) return info;
            var app = GetActiveInstance("Excel.Application");
            try
            {
                var wb = excelType.InvokeMember("ActiveWorkbook", BindingFlags.GetProperty, null, app, null);
                if (wb == null) return info;
                try
                {
                    var sheet = wb.GetType().InvokeMember("ActiveSheet", BindingFlags.GetProperty, null, wb, null);
                    if (sheet == null) return info;
                    try
                    {
                        var usedRange = sheet.GetType().InvokeMember("UsedRange", BindingFlags.GetProperty, null, sheet, null);
                        if (usedRange == null) return info;
                        try
                        {
                            var value = usedRange.GetType().InvokeMember("Value", BindingFlags.GetProperty, null, usedRange, null);
                            var md = Convert2DArrayToMarkdownTable(value);
                            return info with { Text = md };
                        }
                        finally { Marshal.ReleaseComObject(usedRange); }
                    }
                    finally { Marshal.ReleaseComObject(sheet); }
                }
                finally { Marshal.ReleaseComObject(wb); }
            }
            finally { Marshal.ReleaseComObject(app); }
        }
        catch (Exception ex)
        {
            return info with { Error = $"UsedRange-Fehler: {ex.Message}" };
        }
    }

    /// <summary>PowerPoint: liefert FullName + Slide-Texte als Markdown-Liste.</summary>
    public static OfficeDocumentInfo? TryGetPowerPointInfo()
    {
        var info = TryGet(
            progId: "PowerPoint.Application",
            documentProperty: "ActivePresentation",
            rangeProperty: "Slides",
            textProperty: null,
            rowProperty: null);

        if (info == null) return null;

        // Slides einzeln durchlaufen, Text-Frames sammeln
        try
        {
            var ppType = Type.GetTypeFromProgID("PowerPoint.Application");
            if (ppType == null) return info;
            var app = GetActiveInstance("PowerPoint.Application");
            try
            {
                var pres = ppType.InvokeMember("ActivePresentation", BindingFlags.GetProperty, null, app, null);
                if (pres == null) return info;
                try
                {
                    var slides = pres.GetType().InvokeMember("Slides", BindingFlags.GetProperty, null, pres, null);
                    if (slides == null) return info;
                    try
                    {
                        var count = (int)slides.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, slides, null);
                        var sb = new StringBuilder();
                        for (int i = 1; i <= count; i++)
                        {
                            var slide = slides.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, slides, new object[] { i });
                            if (slide == null) continue;
                            try
                            {
                                var shapes = slide.GetType().InvokeMember("Shapes", BindingFlags.GetProperty, null, slide, null);
                                if (shapes == null) continue;
                                try
                                {
                                    var shapeCount = (int)shapes.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, shapes, null);
                                    sb.AppendLine($"### Slide {i}");
                                    bool anyText = false;
                                    for (int s = 1; s <= shapeCount; s++)
                                    {
                                        var shape = shapes.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, shapes, new object[] { s });
                                        if (shape == null) continue;
                                        try
                                        {
                                            // Hat die Shape eine TextFrame?
                                            var hasTF = (bool)shape.GetType().InvokeMember("HasTextFrame", BindingFlags.GetProperty, null, shape, null);
                                            if (!hasTF) continue;
                                            var tf = shape.GetType().InvokeMember("TextFrame", BindingFlags.GetProperty, null, shape, null);
                                            if (tf == null) continue;
                                            try
                                            {
                                                var tr = tf.GetType().InvokeMember("TextRange", BindingFlags.GetProperty, null, tf, null);
                                                if (tr == null) continue;
                                                try
                                                {
                                                    var text = (string)tr.GetType().InvokeMember("Text", BindingFlags.GetProperty, null, tr, null);
                                                    if (!string.IsNullOrWhiteSpace(text))
                                                    {
                                                        sb.AppendLine(text.TrimEnd());
                                                        sb.AppendLine();
                                                        anyText = true;
                                                    }
                                                }
                                                finally { Marshal.ReleaseComObject(tr); }
                                            }
                                            finally { Marshal.ReleaseComObject(tf); }
                                        }
                                        finally { if (shape != null) Marshal.ReleaseComObject(shape); }
                                    }
                                    if (!anyText) sb.AppendLine("_(keine Text-Inhalte)_");
                                    sb.AppendLine();
                                }
                                finally { Marshal.ReleaseComObject(shapes); }
                            }
                            finally { Marshal.ReleaseComObject(slide); }
                        }
                        return info with { Text = sb.ToString() };
                    }
                    finally { Marshal.ReleaseComObject(slides); }
                }
                finally { Marshal.ReleaseComObject(pres); }
            }
            finally { Marshal.ReleaseComObject(app); }
        }
        catch (Exception ex)
        {
            return info with { Error = $"Slides-Fehler: {ex.Message}" };
        }
    }

    /// <summary>
    /// Generischer Office-Lookup fuer Word/Excel/PowerPoint mit late binding.
    /// Liest ProgID.{documentProperty}.{FullPath} und optional Content-Text.
    /// </summary>
    private static OfficeDocumentInfo? TryGet(
        string progId,
        string documentProperty,
        string rangeProperty,
        string? textProperty,
        string? rowProperty)
    {
        try
        {
            var appType = Type.GetTypeFromProgID(progId);
            if (appType == null) return null;
            var app = GetActiveInstance(progId);
            if (app == null) return null;
            try
            {
                var doc = appType.InvokeMember(documentProperty, BindingFlags.GetProperty, null, app, null);
                if (doc == null) return null;
                try
                {
                    var fullPath = (string)doc.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null);
                    if (string.IsNullOrEmpty(fullPath)) return null;

                    if (textProperty == null) return new OfficeDocumentInfo(fullPath, null);

                    var range = doc.GetType().InvokeMember(rangeProperty, BindingFlags.GetProperty, null, doc, null);
                    if (range == null) return new OfficeDocumentInfo(fullPath, null);
                    try
                    {
                        var text = range.GetType().InvokeMember(textProperty, BindingFlags.GetProperty, null, range, null);
                        return new OfficeDocumentInfo(fullPath, text as string);
                    }
                    finally { Marshal.ReleaseComObject(range); }
                }
                finally { Marshal.ReleaseComObject(doc); }
            }
            finally { Marshal.ReleaseComObject(app); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Konvertiert ein COM-2D-Array (object[,]) in eine Markdown-Tabelle.
    /// Leere / Null-Zellen werden zu leerem String.
    /// </summary>
    private static string? Convert2DArrayToMarkdownTable(object? comValue)
    {
        if (comValue is not object[,] arr) return null;
        var rows = arr.GetLength(0);
        var cols = arr.GetLength(1);
        if (rows == 0 || cols == 0) return null;

        var sb = new StringBuilder();
        // Header (Spalten 1..cols als "Spalte N")
        sb.Append("| ");
        for (int c = 0; c < cols; c++) sb.Append($"Spalte {c + 1} | ");
        sb.AppendLine();
        sb.Append("|");
        for (int c = 0; c < cols; c++) sb.Append(" --- |");
        sb.AppendLine();
        for (int r = 0; r < rows; r++)
        {
            sb.Append("| ");
            for (int c = 0; c < cols; c++)
            {
                var cell = arr[r, c];
                var txt = cell?.ToString() ?? string.Empty;
                txt = txt.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ").Trim();
                if (txt.Length > 60) txt = txt[..60] + "…";
                sb.Append(txt).Append(" | ");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}