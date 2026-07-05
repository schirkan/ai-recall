using AiRecall.AppReader.Outlook;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OutlookTitleParser"/> (Spec 0004 Iter. 3).
/// Alle Outlook-Fenster-Titel-Varianten plus Fallback-Pfade.
/// </summary>
public class OutlookTitleParserTests
{
    [Fact]
    public void Parse_InboxAccountView()
    {
        var info = OutlookTitleParser.Parse("Inbox - alice@example.com - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.Equal("Inbox", info.FolderName);
        Assert.Equal("alice@example.com", info.Subject);
        Assert.False(info.HasUnreadCounter);
        Assert.False(info.IsReadOnly);
        Assert.False(info.HasUnsavedMarker);
    }

    [Fact]
    public void Parse_SentItemsFolder()
    {
        var info = OutlookTitleParser.Parse("Sent Items - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.Equal("Sent Items", info.FolderName);
    }

    [Fact]
    public void Parse_InspectorWithSubject()
    {
        var info = OutlookTitleParser.Parse("Angebot Q3 - bitte pruefen - Outlook");
        Assert.Equal(OutlookTitleKind.InspectorSubject, info.Kind);
        Assert.Null(info.FolderName);
        Assert.Equal("Angebot Q3 - bitte pruefen", info.Subject);
    }

    [Fact]
    public void Parse_InspectorReadOnly()
    {
        var info = OutlookTitleParser.Parse("Wichtiger Vertrag.pdf (Read-Only) - Outlook");
        Assert.Equal(OutlookTitleKind.InspectorSubject, info.Kind);
        Assert.True(info.IsReadOnly);
        Assert.Equal("Wichtiger Vertrag.pdf", info.Subject);
    }

    [Fact]
    public void Parse_UnreadCounter()
    {
        var info = OutlookTitleParser.Parse("Inbox - alice@example.com (5) - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.Equal("Inbox", info.FolderName);
        Assert.Equal("alice@example.com", info.Subject);
        Assert.True(info.HasUnreadCounter);
    }

    [Fact]
    public void Parse_UnsavedMarker()
    {
        var info = OutlookTitleParser.Parse("*Unbenannte Nachricht - Outlook");
        Assert.Equal(OutlookTitleKind.InspectorSubject, info.Kind);
        Assert.True(info.HasUnsavedMarker);
        Assert.Equal("Unbenannte Nachricht", info.Subject);
    }

    [Fact]
    public void Parse_DeletedItems_IsRecognized()
    {
        var info = OutlookTitleParser.Parse("Deleted Items - alice@example.com - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.Equal("Deleted Items", info.FolderName);
    }

    [Fact]
    public void Parse_JunkEMail_IsRecognized()
    {
        var info = OutlookTitleParser.Parse("Junk E-Mail - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.Equal("Junk E-Mail", info.FolderName);
    }

    [Fact]
    public void Parse_CaseInsensitiveFolder()
    {
        // Outlook lokalisiert Ordner-Namen — wir matchen case-insensitive
        var info = OutlookTitleParser.Parse("INBOX - alice@example.com - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.Equal("INBOX", info.FolderName);
    }

    [Fact]
    public void Parse_EmptyTitle_ReturnsUnknown()
    {
        var info = OutlookTitleParser.Parse("");
        Assert.Equal(OutlookTitleKind.Unknown, info.Kind);
        Assert.Null(info.FolderName);
        Assert.False(info.IsReadOnly);
    }

    [Fact]
    public void Parse_NullTitle_ReturnsUnknown()
    {
        var info = OutlookTitleParser.Parse(null);
        Assert.Equal(OutlookTitleKind.Unknown, info.Kind);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsUnknown()
    {
        var info = OutlookTitleParser.Parse("   ");
        Assert.Equal(OutlookTitleKind.Unknown, info.Kind);
    }

    [Fact]
    public void Parse_NoSuffix_FallsBackToSubject()
    {
        // Kein " - Outlook" Suffix — kein Folder-Name erkennbar
        var info = OutlookTitleParser.Parse("Just some random text");
        Assert.Equal(OutlookTitleKind.InspectorSubject, info.Kind);
        Assert.Equal("Just some random text", info.Subject);
    }

    [Fact]
    public void Parse_UnreadCounterWithTextNotNumeric_KeepsAsIs()
    {
        // "(Drafts)" wird nicht als Counter erkannt, weil nicht numerisch
        var info = OutlookTitleParser.Parse("Inbox - alice@example.com (Drafts) - Outlook");
        Assert.Equal(OutlookTitleKind.FolderView, info.Kind);
        Assert.False(info.HasUnreadCounter);
        // Subject enthaelt dann das "(Drafts)"
        Assert.Contains("(Drafts)", info.Subject);
    }

    [Fact]
    public void Parse_UnsavedAndReadOnly_Combined()
    {
        // "*" und "(Read-Only)" koexistieren
        var info = OutlookTitleParser.Parse("*Wichtig - Outlook");
        Assert.True(info.HasUnsavedMarker);
        Assert.False(info.IsReadOnly);

        var info2 = OutlookTitleParser.Parse("Doc.pdf (Read-Only) - Outlook");
        Assert.False(info2.HasUnsavedMarker);
        Assert.True(info2.IsReadOnly);
    }

    [Fact]
    public void Parse_ComplexSubjectWithDash_HandledCorrectly()
    {
        // Subject enthaelt selbst einen Bindestrich
        var info = OutlookTitleParser.Parse("Meeting-Notes-2026-07-05 - Outlook");
        Assert.Equal(OutlookTitleKind.InspectorSubject, info.Kind);
        Assert.Equal("Meeting-Notes-2026-07-05", info.Subject);
    }
}