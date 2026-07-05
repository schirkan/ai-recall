using AiRecall.AppReader.Base;
using AiRecall.AppReader.Teams;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="TeamsAppReader"/> (Spec 0011, Cluster 4).
/// Public-Surface, Static-Helpers, BuildFullMarkdown-Helper (internal),
/// Enabled/Disabled-Verhalten.
///
/// <para>
/// Read()-Path-Tests sind limitiert, weil der echte Pfad CDP-WebSocket-Calls
/// benoetigt (= installiertes Teams). Hier testen wir die COM-/CDP-freien
/// Pfade: Title-Fallback, Skip-Pattern, Public-Surface, BuildFullMarkdown.
/// </para>
/// </summary>
public class TeamsAppReaderTests : IDisposable
{
    private readonly ILogger _logger;

    public TeamsAppReaderTests()
    {
        _logger = new LoggerConfiguration().WriteTo.Sink(new NullSink()).CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    private static AppConfig DefaultConfig() => new();

    private TeamsAppReader CreateReader() => new(_logger);

    private AppReaderContext CreateContext(TeamsConfig? teams = null)
    {
        var cfg = DefaultConfig();
        if (teams != null) cfg.AppReader.Teams = teams;
        return new AppReaderContext { Config = cfg, Logger = _logger };
    }

    private static WindowInfo MakeWindow(string processName = "ms-teams", string title = "Chat | Alice - Microsoft Teams")
        => new(IntPtr.Zero, title, 0, processName, true, new WindowRect(0, 0, 800, 600));

    // ============================================================================
    // Public Surface
    // ============================================================================

    [Fact]
    public void SupportedProcesses_ContainsMsTeams()
    {
        var reader = new TeamsAppReader();
        Assert.Contains("ms-teams", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_ContainsTeams()
    {
        var reader = new TeamsAppReader();
        Assert.False(string.IsNullOrWhiteSpace(reader.DisplayName));
        Assert.Contains("Teams", reader.DisplayName);
    }

    [Fact]
    public void SupportsBackgroundPolling_IsFalse()
    {
        var reader = new TeamsAppReader();
        Assert.False(reader.SupportsBackgroundPolling);
    }

    [Fact]
    public void CanRead_MatchessMsTeamsProcess_CaseInsensitive()
    {
        var reader = new TeamsAppReader();
        var win = MakeWindow("MS-TEAMS", "Chat | Alice - Microsoft Teams");
        Assert.True(reader.CanRead(win));
    }

    [Fact]
    public void CanRead_RejectsOtherProcess()
    {
        var reader = new TeamsAppReader();
        var win = MakeWindow("chrome", "Tab");
        Assert.False(reader.CanRead(win));
    }

    // ============================================================================
    // Static Helpers - ShortId
    // ============================================================================

    [Theory]
    [InlineData(null,   "0")]
    [InlineData("",     "0")]
    [InlineData("AB12CD34-1234-5678-90AB-CDEF12345678", "AB12CD34")]
    [InlineData("AB12 CD34 EF56",                       "AB12CD34")]
    [InlineData("AB12CD34",                             "AB12CD34")]
    [InlineData("AB12CD34EF56",                         "AB12CD34")]
    [InlineData("ABCDEF",                               "ABCDEF")]
    public void ShortId_ProducesExpectedResult(string? input, string expected)
    {
        Assert.Equal(expected, TeamsAppReader.ShortId(input));
    }

    [Fact]
    public void IsTeamsProcessRunning_NoThrow()
    {
        // Smoke-Test: Process.GetProcessesByName ohne Exception.
        var exception = Record.Exception(() => TeamsAppReader.IsTeamsProcessRunning());
        Assert.Null(exception);
    }

    // ============================================================================
    // Read - Disabled-Config-Path
    // ============================================================================

    [Fact]
    public void Read_WhenConfigDisabled_ReturnsNull()
    {
        var reader = CreateReader();
        var cfg = new TeamsConfig { Enabled = false };
        var ctx = CreateContext(cfg);

        var result = reader.Read(MakeWindow(), ctx);

        Assert.Null(result);
    }

    // ============================================================================
    // BuildFullMarkdown - Frontmatter-Validation
    // ============================================================================

    [Fact]
    public void BuildFullMarkdown_IncludesAllFrontmatterFields()
    {
        var reader = CreateReader();
        var hierarchy = new TeamsHierarchyInfo(
            "Chat | Alice",
            "1:1",
            "AB12CD34-1234-5678-90AB-CDEF12345678",
            IsMeeting: false);
        var cfg = new TeamsConfig();

        var md = reader.BuildFullMarkdown(
            hierarchy,
            bodyMd: "Hello World",
            source: "teams-title-fallback",
            senderList: new[] { "Alice", "Bob" },
            cfg: cfg);

        Assert.Contains("kind: \"teams-chat\"", md);
        Assert.Contains("chatId: \"AB12CD34-1234-5678-90AB-CDEF12345678\"", md);
        Assert.Contains("chatTitle: \"Chat | Alice\"", md);
        Assert.Contains("chatType: \"1:1\"", md);
        Assert.Contains("isMeeting: false", md);
        Assert.Contains("strategy: \"Auto\"", md);
        Assert.Contains("senderCount: 2", md);
        Assert.Contains("Alice, Bob", md);
        Assert.Contains("source: \"teams-title-fallback\"", md);
        Assert.Contains("reader: \"AiRecall.AppReader.Teams\"", md);
        Assert.Contains("Hello World", md);
    }

    [Fact]
    public void BuildFullMarkdown_MeetingChat_SetsIsMeetingTrue()
    {
        var reader = CreateReader();
        var hierarchy = new TeamsHierarchyInfo(
            "Meeting | Daily Standup",
            "meeting",
            "MTG-ABCDEF",
            IsMeeting: true);
        var cfg = new TeamsConfig();

        var md = reader.BuildFullMarkdown(hierarchy, "Audio only", "teams-uia", Array.Empty<string>(), cfg);

        Assert.Contains("isMeeting: true", md);
        Assert.Contains("chatType: \"meeting\"", md);
        Assert.Contains("Hinweis: Meeting-Chat", md);
    }

    [Fact]
    public void BuildFullMarkdown_EmptyBody_RendersHint()
    {
        var reader = CreateReader();
        var hierarchy = new TeamsHierarchyInfo("Chat | Alice", "1:1", "AB12CD34", false);
        var cfg = new TeamsConfig();

        var md = reader.BuildFullMarkdown(hierarchy, "", "teams-uia", Array.Empty<string>(), cfg);

        Assert.Contains("_(empty chat)_", md);
    }
}
