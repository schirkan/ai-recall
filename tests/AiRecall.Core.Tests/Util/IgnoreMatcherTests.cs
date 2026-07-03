using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Util;

namespace AiRecall.Core.Tests.Util;

public class IgnoreMatcherTests
{
    private static WindowInfo Win(string process, string title) =>
        new(IntPtr.Zero, title, 1234, process, true, new WindowRect(0, 0, 100, 100));

    [Fact]
    public void EmptyConfig_NeverIgnores()
    {
        var config = new ScreenRecorderConfig();
        Assert.False(IgnoreMatcher.IsIgnored(Win("chrome", "Google"), "https://example.com", config));
    }

    [Fact]
    public void MatchesProcessName_CaseInsensitive()
    {
        var config = new ScreenRecorderConfig { IgnoreApps = { "1Password" } };
        Assert.True(IgnoreMatcher.IsIgnored(Win("1password", "Vault"), null, config));
        Assert.True(IgnoreMatcher.IsIgnored(Win("1PASSWORD", "Vault"), null, config));
    }

    [Fact]
    public void MatchesProcessName_Substring()
    {
        var config = new ScreenRecorderConfig { IgnoreApps = { "keepass" } };
        Assert.True(IgnoreMatcher.IsIgnored(Win("KeePassXC", "Database"), null, config));
    }

    [Fact]
    public void MatchesWindowTitle()
    {
        var config = new ScreenRecorderConfig { IgnoreWindowTitles = { "Anmelden" } };
        Assert.True(IgnoreMatcher.IsIgnored(Win("chrome", "Bei Google anmelden"), null, config));
        Assert.False(IgnoreMatcher.IsIgnored(Win("chrome", "Startseite"), null, config));
    }

    [Fact]
    public void MatchesUrl()
    {
        var config = new ScreenRecorderConfig { IgnoreUrls = { "banking" } };
        Assert.True(IgnoreMatcher.IsIgnored(Win("chrome", "Bank"), "https://mybanking.example.com", config));
        Assert.False(IgnoreMatcher.IsIgnored(Win("chrome", "Bank"), "https://example.com", config));
    }

    [Fact]
    public void NullOrEmptyContext_DoesNotMatchUrls()
    {
        var config = new ScreenRecorderConfig { IgnoreUrls = { "banking" } };
        Assert.False(IgnoreMatcher.IsIgnored(Win("chrome", "x"), null, config));
        Assert.False(IgnoreMatcher.IsIgnored(Win("chrome", "x"), string.Empty, config));
    }

    [Fact]
    public void WhitespacePattern_IsSkipped()
    {
        var config = new ScreenRecorderConfig { IgnoreApps = { "", "   ", "chrome" } };
        Assert.True(IgnoreMatcher.IsIgnored(Win("Chrome", "x"), null, config));
    }

    [Fact]
    public void MultipleSources_AnyMatch_TriggersIgnore()
    {
        var config = new ScreenRecorderConfig
        {
            IgnoreApps = { "nope" },
            IgnoreWindowTitles = { "secret" },
            IgnoreUrls = { "private" }
        };
        Assert.True(IgnoreMatcher.IsIgnored(Win("anything", "secret"), null, config));
        Assert.True(IgnoreMatcher.IsIgnored(Win("anything", "x"), "https://private.example", config));
        Assert.False(IgnoreMatcher.IsIgnored(Win("chrome", "Google"), "https://google.com", config));
    }
}
