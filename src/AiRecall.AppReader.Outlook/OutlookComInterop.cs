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
    /// Snapshot-Daten einer einzelnen Mail. Top-level type (siehe
    /// <c>MailSnapshotFromCom.cs</c>) — Datentransfer zwischen
    /// <see cref="OutlookComInterop"/>, <see cref="OutlookAppReader"/>
    /// und <see cref="OutlookAutoRuleDetector"/> (Heuristik).
    /// </summary>
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

    /// <summary>
    /// Liefert die juengsten Mails (max <paramref name="maxItems"/>) aus einem
    /// Outlook-Folder per Name (z. B. "Inbox", "Sent Items"). Late binding:
    /// <c>Application.Session.GetDefaultFolder(olFolderInbox).Items</c>
    /// bzw. <c>Application.Session.Folders(folderName).Items</c>.
    /// </summary>
    /// <param name="folderName">MAPI-Folder-Name (case-insensitive).</param>
    /// <param name="maxItems">Cap (typisch <see cref="AiRecall.Core.Configuration.OutlookConfig.MaxItemsPerSweep"/>).</param>
    public static IReadOnlyList<MailSnapshotFromCom> TryGetRecentMails(string folderName, int maxItems)
    {
        var result = new List<MailSnapshotFromCom>();
        object? app = null;
        object? session = null;
        object? folder = null;
        object? items = null;
        try
        {
            app = GetActiveOutlookInstance();
            if (app == null) return result;

            session = app.GetType().InvokeMember("Session", BindingFlags.GetProperty, null, app, null);
            if (session == null) return result;

            // Strategie A: Folders(folderName) (Lookup by Name)
            try
            {
                var folders = session.GetType().InvokeMember("Folders", BindingFlags.GetProperty, null, session, null);
                if (folders != null)
                {
                    try
                    {
                        folder = folders.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, folders, new object[] { folderName });
                    }
                    finally { Marshal.ReleaseComObject(folders); }
                }
            }
            catch { /* fallback zu Strategie B */ }

            // Strategie B: GetDefaultFolder via olDefaultFolders-Enum (Inbox=1, SentItems=5)
            if (folder == null)
            {
                int olFolderId = MapFolderNameToOutlookId(folderName);
                if (olFolderId > 0)
                {
                    folder = session.GetType().InvokeMember("GetDefaultFolder", BindingFlags.InvokeMethod, null, session, new object[] { olFolderId });
                }
            }

            if (folder == null) return result;

            items = folder.GetType().InvokeMember("Items", BindingFlags.GetProperty, null, folder, null);
            if (items == null) return result;

            // Sortiere nach ReceivedTime DESC
            try
            {
                items.GetType().InvokeMember("Sort", BindingFlags.InvokeMethod, null, items, new object[] { "[ReceivedTime]", true });
            }
            catch { /* Sort optional */ }

            var count = (int)(items.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, items, null) ?? 0);
            var take = Math.Min(count, maxItems);
            for (int i = 1; i <= take; i++)
            {
                object? item = null;
                try
                {
                    item = items.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, items, new object[] { i });
                    if (item == null) continue;
                    var snap = ReadMailItem(item);
                    if (snap != null && !string.IsNullOrEmpty(snap.EntryId)) result.Add(snap);
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
            if (items != null) Marshal.ReleaseComObject(items);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (session != null) Marshal.ReleaseComObject(session);
            if (app != null) Marshal.ReleaseComObject(app);
        }
    }

    /// <summary>
    /// Mappt haeufige Outlook-Folder-Namen auf die
    /// <c>OlDefaultFolders</c>-Enum-IDs. Outlook COM erwartet fuer
    /// <c>GetDefaultFolder</c> den Enum-Wert, nicht den Namen.
    /// </summary>
    private static int MapFolderNameToOutlookId(string folderName)
    {
        // OlDefaultFolders: Inbox=1, SentItems=5, Drafts=16, Outbox=4,
        // DeletedItems=3, Junk=23, Notes=12, Calendar=9, Contacts=10,
        // Tasks=13, Journal=11, Conflicts=19, SyncIssues=20, ToDo=28,
        // RssFeeds=25.
        return folderName.ToLowerInvariant() switch
        {
            "inbox" => 1,
            "sent items" => 5,
            "drafts" => 16,
            "outbox" => 4,
            "deleted items" => 3,
            "junk e-mail" => 23,
            "junk" => 23,
            "spam" => 23,
            _ => -1,
        };
    }
}