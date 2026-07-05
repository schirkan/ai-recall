using System.Reflection;
using System.Runtime.InteropServices;

namespace AiRecall.AppReader.Outlook;

/// <summary>
/// COM-Interop-Wrapper fuer Outlook (Spec 0004 Iter. 3).
///
/// <para>
/// Strategie: <b>late binding</b> via ProgID <c>Outlook.Application</c>
/// + <c>Type.InvokeMember</c>. Keine PIAs / NuGet-Pakete noetig;
/// Outlook muss nur auf dem Zielsystem installiert sein.
/// </para>
///
/// <para>
/// COM-Verbindung zur laufenden Instanz ueber P/Invoke auf
/// <c>oleaut32.dll!GetActiveObject</c> — in .NET 8 SDK 8.0.422 ist
/// <c>Marshal.GetActiveObject</c> nicht (mehr) direkt verfuegbar
/// (gleiche Begruendung wie <c>OfficeComInterop</c> in Documents).
/// </para>
///
/// <para>
/// Bei Fehlern (Outlook nicht installiert, keine Instanz aktiv,
/// COM-Exception) wird <c>null</c> zurueckgegeben — Caller fallen
/// auf Title-Parsing zurueck. <b>Niemals crashen</b>.
/// </para>
///
/// <para>
/// Outlook COM-Objekte muessen explizit mit <c>Marshal.ReleaseComObject</c>
/// freigegeben werden, sonst bleiben RCW-Handles haengen bis GC laeuft.
/// Alle hier exponierten Methoden sind dafuer verantwortlich.
/// </para>
///
/// <para>
/// COM-spezifische Tests sind als <c>[Trait("Integration", "Outlook")]</c>
/// markiert und laufen nur, wenn Outlook installiert ist (analog zu
/// Office-Reader-Tests in Documents).
/// </para>
/// </summary>
internal static class OutlookComInterop
{
    /// <summary>Outlook COM-ProgID (Office-Variante).</summary>
    private const string OutlookProgId = "Outlook.Application";

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.Interface)] out object ppunk);

    /// <summary>
    /// Liefert die laufende Outlook-COM-Instanz oder <c>null</c>, wenn
    /// nicht vorhanden / nicht erreichbar. Cache des Type-Objekts
    /// (Outlook.Application) beim ersten Aufruf.
    /// </summary>
    private static object? GetActiveOutlookInstance()
    {
        try
        {
            var type = Type.GetTypeFromProgID(OutlookProgId);
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
    /// Snapshot-Daten einer einzelnen Mail. Public fuer Tests und als
    /// Datentransfer zwischen <see cref="OutlookComInterop"/> und
    /// <see cref="OutlookAutoRuleDetector"/> (Heuristik).
    /// </summary>
    public sealed record MailSnapshotFromCom(
        string EntryId,
        string Subject,
        string From,
        string FolderName,
        bool UnRead,
        DateTimeOffset ReceivedTime,
        DateTimeOffset LastModificationTime,
        string Body,
        string HtmlBody);

    /// <summary>
    /// Liefert den aktiven Inspector oder null. Wenn ein Mail-Inspector
    /// offen ist (d. h. User liest/schreibt eine Mail), wird dessen
    /// CurrentItem als MailItem zurueckgegeben — sonst null.
    /// </summary>
    public static MailSnapshotFromCom? TryGetActiveInspectorMail()
    {
        object? app = null;
        object? inspector = null;
        object? mailItem = null;
        try
        {
            app = GetActiveOutlookInstance();
            if (app == null) return null;

            // Application.ActiveInspector() (Methode, nicht Property)
            inspector = app.GetType().InvokeMember(
                "ActiveInspector",
                BindingFlags.GetProperty | BindingFlags.InvokeMethod, // COM-Methoden sind GetProperty-kompatibel
                null,
                app,
                null);
            if (inspector == null) return null;

            // Inspector.CurrentItem
            mailItem = inspector.GetType().InvokeMember(
                "CurrentItem",
                BindingFlags.GetProperty,
                null,
                inspector,
                null);
            if (mailItem == null) return null;

            return ReadMailItem(mailItem);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (mailItem != null) Marshal.ReleaseComObject(mailItem);
            if (inspector != null) Marshal.ReleaseComObject(inspector);
            if (app != null) Marshal.ReleaseComObject(app);
        }
    }

    /// <summary>
    /// Liefert die im Explorer (Hauptfenster) selektierten Mails.
    /// Pro Folder max <paramref name="maxItems"/> Mails.
    /// Liefert leere Liste wenn kein Explorer aktiv oder keine
    /// Selektion. Jede Mail als <see cref="MailSnapshotFromCom"/>.
    /// </summary>
    public static IReadOnlyList<MailSnapshotFromCom> TryGetExplorerSelection(int maxItems)
    {
        var result = new List<MailSnapshotFromCom>();
        object? app = null;
        object? explorer = null;
        object? selection = null;
        try
        {
            app = GetActiveOutlookInstance();
            if (app == null) return result;

            explorer = app.GetType().InvokeMember(
                "ActiveExplorer",
                BindingFlags.GetProperty,
                null,
                app,
                null);
            if (explorer == null) return result;

            selection = explorer.GetType().InvokeMember(
                "Selection",
                BindingFlags.GetProperty,
                null,
                explorer,
                null);
            if (selection == null) return result;

            var count = (int)selection.GetType().InvokeMember(
                "Count",
                BindingFlags.GetProperty,
                null,
                selection,
                null);
            var take = Math.Min(count, maxItems);
            for (int i = 1; i <= take; i++)
            {
                object? item = null;
                try
                {
                    item = selection.GetType().InvokeMember(
                        "Item",
                        BindingFlags.GetProperty,
                        null,
                        selection,
                        new object[] { i });
                    if (item == null) continue;
                    var snap = ReadMailItem(item);
                    if (snap != null) result.Add(snap);
                }
                finally
                {
                    if (item != null) Marshal.ReleaseComObject(item);
                }
            }
            return result;
        }
        catch
        {
            return result;
        }
        finally
        {
            if (selection != null) Marshal.ReleaseComObject(selection);
            if (explorer != null) Marshal.ReleaseComObject(explorer);
            if (app != null) Marshal.ReleaseComObject(app);
        }
    }

    /// <summary>
    /// Versucht, den Mail-Snapshot aus einem COM-Objekt zu lesen.
    /// Erwartet ein Outlook-MailItem (Inspector.CurrentItem oder
    /// Selection.Item). Liefert null bei Casting-Fehler.
    /// </summary>
    private static MailSnapshotFromCom? ReadMailItem(object mailItem)
    {
        if (mailItem == null) return null;
        try
        {
            var type = mailItem.GetType();

            // EntryID (Pflicht — null bei neuen, noch nicht gesendeten Mails)
            var entryId = type.InvokeMember("EntryID", BindingFlags.GetProperty, null, mailItem, null) as string ?? string.Empty;

            var subject = type.InvokeMember("Subject", BindingFlags.GetProperty, null, mailItem, null) as string ?? string.Empty;

            // SenderEmailAddress (Outlook-Adresse); Fallback auf Sender.Name
            var fromEmail = type.InvokeMember("SenderEmailAddress", BindingFlags.GetProperty, null, mailItem, null) as string;
            if (string.IsNullOrEmpty(fromEmail))
            {
                fromEmail = type.InvokeMember("SenderName", BindingFlags.GetProperty, null, mailItem, null) as string;
            }
            fromEmail ??= string.Empty;

            // UnRead (Property)
            var unRead = false;
            try
            {
                unRead = (bool)(type.InvokeMember("UnRead", BindingFlags.GetProperty, null, mailItem, null) ?? false);
            }
            catch { /* defensiv */ }

            // ReceivedTime
            DateTimeOffset received = default;
            try
            {
                var dt = type.InvokeMember("ReceivedTime", BindingFlags.GetProperty, null, mailItem, null);
                if (dt is DateTime dt2) received = new DateTimeOffset(dt2, TimeZoneInfo.Local.GetUtcOffset(dt2));
            }
            catch { /* defensiv */ }

            // LastModificationTime
            DateTimeOffset lastMod = default;
            try
            {
                var dt = type.InvokeMember("LastModificationTime", BindingFlags.GetProperty, null, mailItem, null);
                if (dt is DateTime dt2) lastMod = new DateTimeOffset(dt2, TimeZoneInfo.Local.GetUtcOffset(dt2));
            }
            catch { /* defensiv */ }

            // Body (Plain-Text)
            var body = type.InvokeMember("Body", BindingFlags.GetProperty, null, mailItem, null) as string ?? string.Empty;

            // HTMLBody
            var htmlBody = type.InvokeMember("HTMLBody", BindingFlags.GetProperty, null, mailItem, null) as string ?? string.Empty;

            // Folder-Name (Parent.Folder.Name — wir bleiben in MailItem.Parent)
            var folderName = string.Empty;
            try
            {
                var parent = type.InvokeMember("Parent", BindingFlags.GetProperty, null, mailItem, null);
                if (parent != null)
                {
                    try
                    {
                        var folderObj = parent.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, parent, null);
                        if (folderObj != null)
                        {
                            try
                            {
                                folderName = folderObj.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, folderObj, null) as string ?? string.Empty;
                            }
                            finally { Marshal.ReleaseComObject(folderObj); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(parent); }
                }
            }
            catch { /* defensiv */ }

            return new MailSnapshotFromCom(
                EntryId: entryId,
                Subject: subject,
                From: fromEmail,
                FolderName: folderName,
                UnRead: unRead,
                ReceivedTime: received,
                LastModificationTime: lastMod,
                Body: body,
                HtmlBody: htmlBody);
        }
        catch
        {
            return null;
        }
    }
}