using AiRecall.AppReader.Teams;
using AiRecall.Core.Models;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="TeamsUiaReader"/> (Spec 0011, Cluster 2).
/// Title-Parser, Process-Check, Sender-Heuristik.
/// </summary>
public class TeamsUiaReaderTests
{
    // ============================================================================
    // ParseWindowTitle - Format-Varianten
    // ============================================================================

    [Theory]
    [InlineData("Chat | Alice - Microsoft Teams",                "chat",   "Chat | Alice",  false)]
    [InlineData("Channel | #general - Microsoft Teams",          "channel","Channel | #general", false)]
    [InlineData("Group Chat | Project Alpha - Microsoft Teams",  "group",  "Group Chat | Project Alpha", false)]
    [InlineData("Meeting | Daily Standup - Microsoft Teams",     "meeting","Meeting | Daily Standup", true)]
    [InlineData("Chat | Bob - Microsoft Teams",                  "chat",   "Chat | Bob", false)]
    [InlineData("Channel | #design-review - Microsoft Teams",    "channel","Channel | #design-review", false)]
    public void ParseWindowTitle_ParsesCommonFormats(string title, string kindStr, string formatted, bool isMeeting)
    {
        var info = TeamsUiaReader.ParseWindowTitle(title);
        var expectedKind = kindStr switch
        {
            "chat"    => TeamsChatKind.OneOnOne,
            "channel" => TeamsChatKind.Channel,
            "group"   => TeamsChatKind.Group,
            "meeting" => TeamsChatKind.Meeting,
            _         => TeamsChatKind.Unknown,
        };

        Assert.Equal(expectedKind, info.Kind);
        Assert.Equal(formatted,    info.FormattedTitle);
        Assert.Equal(isMeeting,    info.IsMeeting);
    }

    [Theory]
    [InlineData("",                                                "(unknown)",                false)]
    [InlineData(null!,                                             "(unknown)",                false)]
    [InlineData("Some Random Title",                               "Some Random Title",        false)]
    [InlineData("Chat with Bob Without Separator",                 "Chat with Bob Without Separator", false)]
    public void ParseWindowTitle_FallbackReturnsUnknownOrRawTitle(string? title, string formatted, bool isMeeting)
    {
        var info = TeamsUiaReader.ParseWindowTitle(title);
        Assert.Equal(formatted, info.FormattedTitle);
        Assert.Equal(isMeeting, info.IsMeeting);
        Assert.Equal(TeamsChatKind.Unknown, info.Kind);
    }

    [Fact]
    public void ParseWindowTitle_NoMicrosoftTeamsSuffix_StillParses()
    {
        // Manche Custom-Skins haben das Suffix nicht — Parser sollte trotzdem funktionieren.
        var info = TeamsUiaReader.ParseWindowTitle("Chat | Carol");
        Assert.Equal(TeamsChatKind.OneOnOne, info.Kind);
        Assert.Equal("Chat | Carol", info.FormattedTitle);
    }

    // ============================================================================
    // ChatTypeLabel
    // ============================================================================

    [Theory]
    [InlineData(TeamsChatKind.OneOnOne, "1:1")]
    [InlineData(TeamsChatKind.Channel,  "channel")]
    [InlineData(TeamsChatKind.Group,    "group")]
    [InlineData(TeamsChatKind.Meeting,  "meeting")]
    [InlineData(TeamsChatKind.Unknown,  "unknown")]
    public void ChatTypeLabel_ReturnsExpectedString(TeamsChatKind kind, string expected)
    {
        var info = new TeamsTitleInfo("X | Y", kind, false);
        Assert.Equal(expected, info.ChatTypeLabel);
    }

    // ============================================================================
    // IsTeamsChatWindow - Process-Check
    // ============================================================================

    [Fact]
    public void IsTeamsChatWindow_ZeroHandle_ReturnsFalse()
    {
        Assert.False(TeamsUiaReader.IsTeamsChatWindow(IntPtr.Zero));
    }

    // ============================================================================
    // ComputeChatId - deterministisch
    // ============================================================================

    [Fact]
    public void ComputeChatId_DeterministicForSameInputs()
    {
        var id1 = TeamsHierarchyInfo.ComputeChatId("Chat | Alice", "1:1", new[] { "Alice", "Bob" });
        var id2 = TeamsHierarchyInfo.ComputeChatId("Chat | Alice", "1:1", new[] { "Bob", "Alice" });  // Sender-Reihenfolge egal
        Assert.Equal(id1, id2);
        Assert.Matches(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id1);
    }

    [Fact]
    public void ComputeChatId_DifferentForDifferentTitles()
    {
        var id1 = TeamsHierarchyInfo.ComputeChatId("Chat | Alice", "1:1", new[] { "Alice" });
        var id2 = TeamsHierarchyInfo.ComputeChatId("Chat | Bob",   "1:1", new[] { "Bob" });
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeChatId_DifferentForDifferentChatTypes()
    {
        var id1 = TeamsHierarchyInfo.ComputeChatId("Chat | Alice", "1:1", new[] { "Alice" });
        var id2 = TeamsHierarchyInfo.ComputeChatId("Chat | Alice", "group", new[] { "Alice" });
        Assert.NotEqual(id1, id2);
    }

    [Theory]
    [InlineData(null,   "0")]
    [InlineData("",     "0")]
    [InlineData("AB12CD34-1234-5678-90AB-CDEF12345678", "AB12CD34")]
    [InlineData("ABC",  "ABC")]
    [InlineData("ABCDEFGHIJKLMNOP", "ABCDEFGH")]
    public void ChatIdShort_TruncatesCorrectly(string? input, string expected)
    {
        var info = new TeamsHierarchyInfo("Title", "1:1", input!, false);
        Assert.Equal(expected, info.ChatIdShort);
    }

    // ============================================================================
    // TeamsMessage - SenderShort
    // ============================================================================

    [Fact]
    public void TeamsMessage_SenderShort_TruncatesLongNames()
    {
        var msg = new TeamsMessage(
            "Christopher Anderson-Smith the Third",
            DateTimeOffset.UtcNow,
            "Hello",
            false);
        Assert.Equal("Christopher Ande…", msg.SenderShort);
    }

    [Fact]
    public void TeamsMessage_SenderShort_KeepsShortNames()
    {
        var msg = new TeamsMessage("Alice", DateTimeOffset.UtcNow, "Hi", false);
        Assert.Equal("Alice", msg.SenderShort);
    }

    // ============================================================================
    // TeamsContent - ChatTitleHint
    // ============================================================================

    [Fact]
    public void TeamsContent_ChatTitleHint_DefaultNull()
    {
        var content = new TeamsContent("body", new[] { "Alice" }, "teams-uia");
        Assert.Null(content.ChatTitleHint);
    }

    [Fact]
    public void TeamsContent_ChatTitleHint_InitSetter()
    {
        var content = new TeamsContent("body", new[] { "Alice" }, "teams-cdp")
        { ChatTitleHint = "Chat | Alice" };
        Assert.Equal("Chat | Alice", content.ChatTitleHint);
    }
}
