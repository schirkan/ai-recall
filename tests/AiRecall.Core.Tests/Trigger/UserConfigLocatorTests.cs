using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class UserConfigLocatorTests
{
    [Fact]
    public void GetUserConfigPath_ReturnsNonEmptyPath()
    {
        var path = UserConfigLocator.GetUserConfigPath();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.EndsWith("config.json", path);
    }

    [Fact]
    public void LoadOrDefault_NoArg_ReturnsDefaultConfig()
    {
        var config = UserConfigLocator.LoadOrDefault();
        Assert.NotNull(config);
    }

    [Fact]
    public void LoadOrDefault_LoggerCallback_DoesNotThrow()
    {
        var messages = new List<string>();
        var config = UserConfigLocator.LoadOrDefault(messages.Add);
        Assert.NotNull(config);
    }
}