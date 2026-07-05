using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;

namespace AiRecall.AppReader.OneNote;

/// <summary>
/// COM-Interop-Wrapper fuer OneNote (Spec 0010).
///
/// <para><b>Strategie</b>: <c>late binding</c> via ProgID <c>OneNote.Application</c>
/// + <c>Type.InvokeMember</c>. Keine PIAs / NuGet-Pakete noetig; OneNote muss
/// nur auf dem Zielsystem installiert sein. Verwendet wird COM-Late-Binding
/// anstelle einer typisierten Interop-Assembly, um Build-Zeit, DLL-Groesse
/// und Abhaengigkeiten niedrig zu halten.</para>
///
/// <para><b>Active-Page-Strategie (4-stufig, siehe Spec 0010):</b></para>
/// <list type="number">
/// <item>Windows.CurrentWindow.CurrentPageId (offizielle API)</item>
/// <item>Windows-Collection + window.Active == true (Fallback)</item>
/// <item>GetHierarchy(hsPages) + XPath <c>isCurrentlyViewed="true"</c> (Robust-Fallback)</item>
/// <item>null zurueckgeben (Caller faellt auf OCR zurueck)</item>
/// </list>
///
/// <para>Bei Fehlern (OneNote nicht installiert, keine Instanz aktiv, COM-Exception)
/// wird <c>null</c> zurueckgegeben — Caller fallen auf OCR zurueck.
/// <b>Niemals crashen</b>.</para>
///
/// <para>OneNote-COM-Objekte muessen explizit mit <c>Marshal.ReleaseComObject</c>
/// freigegeben werden, sonst bleiben RCW-Handles haengen bis GC laeuft.
/// Alle hier exponierten Methoden sind dafuer verantwortlich.</para>
///
/// <para>Retry-Logik bei transienten COM-Fehlern (max. 3 Versuche, je 500 ms
/// Backoff, mit frischem COM-Objekt). Fatal-Fehler (siehe
/// <see cref="OneNoteComException"/>) werden ohne Retry gemeldet.</para>
/// </summary>
internal static class OneNoteComInterop
{
    /// <summary>OneNote COM-ProgID (Office-Variante).</summary>
    private const string OneNoteProgId = "OneNote.Application";

    /// <summary>OneNote-Process-Name (case-sensitive fuer <c>Process.GetProcessesByName</c>).</summary>
    private const string OneNoteProcessName = "OneNote";

    /// <summary>OneNote-XML-Namespace (Schema-Variante 2013).</summary>
    internal const string OneNoteXmlNamespace = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    /// <summary>Hierarchy-Scope fuer <c>GetHierarchy</c>: Page-Ebene.</summary>
    private const string HierarchyScopePages = "hsPages";

    /// <summary>Hierarchy-Scope fuer <c>GetHierarchy</c>: nur den Node selbst (fuer Volltextpfad).</summary>
    private const string HierarchyScopeSelf = "hsSelf";

    /// <summary>XML-Schema fuer <c>GetPageContent</c> (immer 2013 fuer Versions-Kompatibilitaet).</summary>
    private const string XmlSchema2013 = "xs2013";

    /// <summary>Max. Retry-Versuche bei transienten COM-Fehlern.</summary>
    private const int MaxRetries = 3;

    /// <summary>Backoff zwischen Retries (ms).</summary>
    private const int RetryBackoffMs = 500;

    // ============================================================================
    // Public API — Process-Detection
    // ============================================================================

    /// <summary>
    /// Liefert <c>true</c>, wenn ein OneNote-Prozess laeuft. Process-Check ist
    /// pre-COM-Filter: wenn OneNote gar nicht laeuft, koennen wir die COM-Aufrufe
    /// komplett sparen.
    /// </summary>
    public static bool IsOneNoteRunning()
    {
        try
        {
            return Process.GetProcessesByName(OneNoteProcessName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================================
    // Public API — Active-Page-Strategie
    // ============================================================================

    /// <summary>
    /// Ermittelt die aktive Page und liefert <see cref="OneNoteHierarchyInfo"/>
    /// (Page + Section + Notebook + LastModified).
    ///
    /// Implementiert die 4-stufige Fallback-Kette:
    /// <list type="number">
    /// <item>Windows.CurrentWindow.CurrentPageId (Strategie <c>WindowsApi</c>)</item>
    /// <item>Windows-Collection foreach + window.Active (Stage-2-Fallback)</item>
    /// <item>GetHierarchy(hsPages) + isCurrentlyViewed="true" (Strategie <c>HierarchyXml</c>)</item>
    /// <item>null zurueckgeben (Caller faellt auf OCR zurueck)</item>
    /// </list>
    /// </summary>
    /// <param name="strategy"><c>"WindowsApi"</c>, <c>"HierarchyXml"</c> oder <c>"Auto"</c> (Default).</param>
    public static OneNoteHierarchyInfo? TryGetActivePage(string strategy = "Auto")
    {
        object? app = null;
        try
        {
            app = GetActiveInstance();
            if (app == null) return null;

            var type = app.GetType();

            // Stage 1+2 (schnell + offiziell) — bevorzugt bei Auto / WindowsApi.
            if (strategy is "WindowsApi" or "Auto")
            {
                var info = TryStage1Or2(type, app);
                if (info != null) return info;
                if (strategy == "WindowsApi") return null;
            }

            // Stage 3 (Robust-Fallback) — bei Auto nach Stage 1+2 versuchen.
            if (strategy is "HierarchyXml" or "Auto")
            {
                return TryStage3(type, app);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (app != null) Marshal.ReleaseComObject(app);
        }
    }

    /// <summary>
    /// Liefert den vollstaendigen Page-Content der angegebenen Page-ID als XML.
    /// Verwendet <c>xs2013</c>-Schema fuer Versions-Kompatibilitaet
    /// (siehe Spec 0010 §Komponenten / OneNoteComInterop).
    /// </summary>
    public static string? TryGetPageContentXml(string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return null;

        object? app = null;
        try
        {
            app = GetActiveInstance();
            if (app == null) return null;

            // GetPageContent(pageId, schema) — 2-Arg-Methode
            var args = new object?[] { pageId, XmlSchema2013 };
            var xmlObj = app.GetType().InvokeMember(
                "GetPageContent",
                BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                null,
                app,
                args);

            return xmlObj as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (app != null) Marshal.ReleaseComObject(app);
        }
    }

    // ============================================================================
    // Stage 1 + 2: Windows-Collection API (offizielle MS-Strategie)
    // ============================================================================

    private static OneNoteHierarchyInfo? TryStage1Or2(Type appType, object app)
    {
        object? windows = null;
        object? currentWindow = null;
        object? explicitWindow = null;
        try
        {
            windows = TryInvokeGetProperty(appType, app, "Windows");
            if (windows == null) return null;

            // Stage 1: CurrentWindow (Property auf der Collection).
            currentWindow = TryInvokeGetProperty(windows.GetType(), windows, "CurrentWindow");
            if (currentWindow != null)
            {
                var info = TryReadPageFromWindow(currentWindow);
                if (info != null) return info;
            }

            // Stage 2: foreach Windows-Collection, suche Active == true.
            var countObj = TryInvokeGetProperty(windows.GetType(), windows, "Count");
            if (countObj is int count && count > 0)
            {
                // Index 1-basiert (COM-Konvention), max. 16 versuchen (Cap gegen edge-cases).
                var max = Math.Min(count, 16);
                for (var i = 1; i <= max; i++)
                {
                    object? windowItem = null;
                    try
                    {
                        windowItem = windows.GetType().InvokeMember(
                            "Item",
                            BindingFlags.GetProperty,
                            null,
                            windows,
                            new object[] { i });

                        if (windowItem == null) continue;

                        var isActive = TryInvokeGetProperty(windowItem.GetType(), windowItem, "Active");
                        if (isActive is bool active && active)
                        {
                            // Speichere fuer finally-Block-Release.
                            explicitWindow = windowItem;
                            return TryReadPageFromWindow(explicitWindow);
                        }
                    }
                    finally
                    {
                        if (windowItem != null && !ReferenceEquals(windowItem, explicitWindow))
                        {
                            Marshal.ReleaseComObject(windowItem);
                        }
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (explicitWindow != null) Marshal.ReleaseComObject(explicitWindow);
            if (currentWindow != null && !ReferenceEquals(currentWindow, explicitWindow))
            {
                Marshal.ReleaseComObject(currentWindow);
            }
            if (windows != null) Marshal.ReleaseComObject(windows);
        }
    }

    /// <summary>
    /// Liest aus einem Window-Objekt die Page-ID, dann die vollstaendige Hierarchy.
    /// </summary>
    private static OneNoteHierarchyInfo? TryReadPageFromWindow(object window)
    {
        try
        {
            var pageIdObj = TryInvokeGetProperty(window.GetType(), window, "CurrentPageId");
            if (pageIdObj is not string pageId || string.IsNullOrEmpty(pageId))
            {
                return null;
            }

            return ReadHierarchyForPage(app: GetActiveInstance(), pageId);
        }
        catch
        {
            return null;
        }
    }

    // ============================================================================
    // Stage 3: Hierarchy-XML + isCurrentlyViewed="true" (Robust-Fallback)
    // ============================================================================

    private static OneNoteHierarchyInfo? TryStage3(Type appType, object app)
    {
        try
        {
            // GetHierarchy(scope, startNodeId, ...) — wir nutzen hsPages ohne Start-Knoten.
            var args = new object?[] { HierarchyScopePages, string.Empty };
            var xmlObj = appType.InvokeMember(
                "GetHierarchy",
                BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                null,
                app,
                args);

            if (xmlObj is not string xml || string.IsNullOrEmpty(xml))
            {
                return null;
            }

            return ParseIsCurrentlyViewed(xml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// XPath-basierte Suche nach <c>//one:Page[@isCurrentlyViewed='true']</c> im
    /// Hierarchy-XML. Liefert eine vollstaendige <see cref="OneNoteHierarchyInfo"/>
    /// mit Notebook/Section/Page-IDs und Titeln.
    /// </summary>
    /// <remarks>Internal fuer Tests (siehe <c>OneNoteComInteropTests</c>).</remarks>
    internal static OneNoteHierarchyInfo? ParseIsCurrentlyViewed(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("one", OneNoteXmlNamespace);

            var node = doc.SelectSingleNode("//one:Page[@isCurrentlyViewed='true']", ns);
            if (node == null) return null;

            var pageId = ReadAttribute(node, "ID");
            if (string.IsNullOrEmpty(pageId)) return null;

            var sectionNode = node.ParentNode;
            var notebookNode = sectionNode?.ParentNode;

            var sectionId = ReadAttribute(sectionNode, "ID");
            var sectionTitle = ReadAttribute(sectionNode, "name");
            var notebookId = ReadAttribute(notebookNode, "ID");
            var notebookTitle = ReadAttribute(notebookNode, "name");
            var pageTitle = ReadAttribute(node, "name");

            DateTime lastModified = DateTime.MinValue;
            var lastModifiedStr = ReadAttribute(node, "lastModifiedTime");
            if (!string.IsNullOrEmpty(lastModifiedStr) &&
                DateTime.TryParse(lastModifiedStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var lm))
            {
                lastModified = lm;
            }

            return new OneNoteHierarchyInfo(
                PageId: pageId,
                PageTitle: pageTitle,
                SectionId: sectionId,
                SectionTitle: sectionTitle,
                NotebookId: notebookId,
                NotebookTitle: notebookTitle,
                LastModified: lastModified);
        }
        catch
        {
            return null;
        }
    }

    // ============================================================================
    // Hierarchy fuer eine spezifische Page-ID
    // ============================================================================

    /// <summary>
    /// Holt die vollstaendige Hierarchy (Notebook + Section + Page) fuer eine Page-ID.
    /// Verwendet <c>GetHierarchy(hsSelf, pageId)</c>. Wenn das fehlschlaegt,
    /// wird eine Minimal-<see cref="OneNoteHierarchyInfo"/> mit nur der Page-ID zurueckgegeben.
    /// </summary>
    private static OneNoteHierarchyInfo? ReadHierarchyForPage(object? app, string pageId)
    {
        if (app == null) return null;

        try
        {
            var args = new object?[] { HierarchyScopeSelf, pageId };
            var xmlObj = app.GetType().InvokeMember(
                "GetHierarchy",
                BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                null,
                app,
                args);

            if (xmlObj is not string xml || string.IsNullOrEmpty(xml))
            {
                return new OneNoteHierarchyInfo(pageId, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, DateTime.MinValue);
            }

            return ParseSelfHierarchyXml(xml, pageId);
        }
        catch
        {
            // Minimal-Fallback: nur Page-ID setzen, Rest leer.
            return new OneNoteHierarchyInfo(pageId, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, DateTime.MinValue);
        }
    }

    /// <summary>
    /// Parst das Self-Hierarchy-XML einer einzelnen Page. Anders als <see cref="ParseIsCurrentlyViewed"/>
    /// ist die Root-Node bereits direkt die Page (kein XPath noetig).
    /// </summary>
    internal static OneNoteHierarchyInfo? ParseSelfHierarchyXml(string xml, string fallbackPageId)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("one", OneNoteXmlNamespace);

            var pageNode = doc.SelectSingleNode("//one:Page", ns);
            var sectionNode = doc.SelectSingleNode("//one:Section", ns);
            var notebookNode = doc.SelectSingleNode("//one:Notebook", ns);

            if (pageNode == null)
            {
                return new OneNoteHierarchyInfo(fallbackPageId, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, DateTime.MinValue);
            }

            var pageId = ReadAttribute(pageNode, "ID");
            if (string.IsNullOrEmpty(pageId)) pageId = fallbackPageId;

            var sectionId = ReadAttribute(sectionNode, "ID");
            var sectionTitle = ReadAttribute(sectionNode, "name");
            var notebookId = ReadAttribute(notebookNode, "ID");
            var notebookTitle = ReadAttribute(notebookNode, "name");
            var pageTitle = ReadAttribute(pageNode, "name");

            DateTime lastModified = DateTime.MinValue;
            var lastModifiedStr = ReadAttribute(pageNode, "lastModifiedTime");
            if (!string.IsNullOrEmpty(lastModifiedStr) &&
                DateTime.TryParse(lastModifiedStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var lm))
            {
                lastModified = lm;
            }

            return new OneNoteHierarchyInfo(
                PageId: pageId,
                PageTitle: pageTitle,
                SectionId: sectionId,
                SectionTitle: sectionTitle,
                NotebookId: notebookId,
                NotebookTitle: notebookTitle,
                LastModified: lastModified);
        }
        catch
        {
            return new OneNoteHierarchyInfo(fallbackPageId, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, DateTime.MinValue);
        }
    }

    // ============================================================================
    // Private Helpers
    // ============================================================================

    /// <summary>
    /// Liefert die OneNote-COM-Instanz mit Retry-Logik. Bei transienten
    /// COM-Fehlern (siehe <see cref="OneNoteComException.IsRetryable"/>)
    /// wird bis zu <see cref="MaxRetries"/> mal versucht, mit Backoff.
    /// Bei fatalen Fehlern wird sofort abgebrochen.
    /// </summary>
    private static object? GetActiveInstance()
    {
        object? instance = null;
        Exception? lastEx = null;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var type = Type.GetTypeFromProgID(OneNoteProgId);
                if (type == null) return null;

                OneNoteComNative.GetActiveObject(type.GUID, IntPtr.Zero, out instance);
                if (instance != null) return instance;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                var oneEx = new OneNoteComException(ex);
                if (!oneEx.IsRetryable)
                {
                    // Fatal — kein Retry (Schema-Error, RPC-Crash, ...)
                    return null;
                }
                Thread.Sleep(RetryBackoffMs);
            }
        }

        // MaxRetries ueberschritten — letzten Fehler silently schlucken.
        // Caller bekommen null und fallen auf OCR zurueck.
        _ = lastEx;
        return null;
    }

    private static object? TryInvokeGetProperty(Type type, object target, string name)
    {
        try
        {
            return type.InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadAttribute(XmlNode? node, string attributeName)
    {
        return node?.Attributes?[attributeName]?.Value ?? string.Empty;
    }
}
