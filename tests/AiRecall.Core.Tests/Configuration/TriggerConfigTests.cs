using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tests.Configuration;

public class TriggerConfigTests : IDisposable
{
    private readonly string _tempDir;

    public TriggerConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ai-recall-trigger-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Defaults_AreLoaded()
    {
        var config = new AppConfig();
        Assert.NotNull(config.Trigger);
        Assert.True(config.Trigger.Enabled);
        Assert.Equal(500, config.Trigger.ThrottleMs);
        Assert.Equal(2, config.Trigger.ThrottlePerAppSeconds);
        Assert.Equal(30, config.Trigger.HeartbeatIntervalSeconds);
        Assert.NotNull(config.Trigger.WinEvents);
        Assert.NotNull(config.Trigger.Blacklist);
    }

    [Fact]
    public void WinEvents_AllTrueByDefault()
    {
        var ev = new WinEventSubscription();
        Assert.True(ev.Foreground);
        Assert.True(ev.Focus);
        Assert.True(ev.NameChange);
        Assert.True(ev.ValueChange);
        Assert.True(ev.Scroll);
        Assert.True(ev.MenuPopup);
    }

    [Fact]
    public void Blacklist_HasTooltipAndNotifyDefaults()
    {
        var b = new TriggerBlacklist();
        Assert.Contains("tooltips_class32", b.WindowClasses);
        Assert.Contains("NotifyIconOverflowWindow", b.WindowClasses);
        Assert.Empty(b.Processes);
    }

    [Fact]
    public void LoadFromJson_AppliesOverrides()
    {
        var path = WriteConfig("""
        {
          "trigger": {
            "enabled": false,
            "throttleMs": 750,
            "throttlePerAppSeconds": 5,
            "heartbeatIntervalSeconds": 60,
            "winEvents": {
              "foreground": true,
              "focus": false,
              "nameChange": false,
              "valueChange": true,
              "scroll": false,
              "menuPopup": true
            },
            "blacklist": {
              "windowClasses": ["myclass"],
              "processes": ["evilproc", "another"]
            }
          }
        }
        """);
        var config = ConfigLoader.Load(path);

        Assert.False(config.Trigger.Enabled);
        Assert.Equal(750, config.Trigger.ThrottleMs);
        Assert.Equal(5, config.Trigger.ThrottlePerAppSeconds);
        Assert.Equal(60, config.Trigger.HeartbeatIntervalSeconds);

        Assert.True(config.Trigger.WinEvents.Foreground);
        Assert.False(config.Trigger.WinEvents.Focus);
        Assert.False(config.Trigger.WinEvents.NameChange);
        Assert.True(config.Trigger.WinEvents.ValueChange);
        Assert.False(config.Trigger.WinEvents.Scroll);
        Assert.True(config.Trigger.WinEvents.MenuPopup);

        Assert.Single(config.Trigger.Blacklist.WindowClasses);
        Assert.Equal("myclass", config.Trigger.Blacklist.WindowClasses[0]);
        Assert.Equal(2, config.Trigger.Blacklist.Processes.Count);
        Assert.Contains("evilproc", config.Trigger.Blacklist.Processes);
        Assert.Contains("another", config.Trigger.Blacklist.Processes);
    }

    [Fact]
    public void LoadFromJson_PartialTrigger_OtherDefaultsIntact()
    {
        var path = WriteConfig("""
        {
          "trigger": {
            "throttleMs": 1234
          }
        }
        """);
        var config = ConfigLoader.Load(path);

        Assert.Equal(1234, config.Trigger.ThrottleMs);
        // Andere Defaults bleiben erhalten
        Assert.True(config.Trigger.Enabled);
        Assert.Equal(2, config.Trigger.ThrottlePerAppSeconds);
        Assert.Equal(30, config.Trigger.HeartbeatIntervalSeconds);
        Assert.True(config.Trigger.WinEvents.Foreground);
        Assert.True(config.Trigger.WinEvents.Focus);
        Assert.True(config.Trigger.WinEvents.NameChange);
        Assert.True(config.Trigger.WinEvents.ValueChange);
        Assert.True(config.Trigger.WinEvents.Scroll);
        Assert.True(config.Trigger.WinEvents.MenuPopup);
        Assert.Contains("tooltips_class32", config.Trigger.Blacklist.WindowClasses);
    }

    [Fact]
    public void LoadFromJson_EmptyTrigger_UsesDefaults()
    {
        var path = WriteConfig("""
        {
          "trigger": {}
        }
        """);
        var config = ConfigLoader.Load(path);

        Assert.True(config.Trigger.Enabled);
        Assert.Equal(500, config.Trigger.ThrottleMs);
        Assert.Equal(30, config.Trigger.HeartbeatIntervalSeconds);
        Assert.True(config.Trigger.WinEvents.Foreground);
        Assert.Contains("tooltips_class32", config.Trigger.Blacklist.WindowClasses);
    }

    [Fact]
    public void LoadFromJson_NoTrigger_UsesDefaults()
    {
        // Komplett ohne "trigger"-Sektion → Default-Config greift
        var path = WriteConfig("""
        {
          "capture": { "rootPath": "x" }
        }
        """);
        var config = ConfigLoader.Load(path);

        Assert.Equal("x", config.Capture.RootPath);
        Assert.Equal(500, config.Trigger.ThrottleMs);
        Assert.True(config.Trigger.WinEvents.Foreground);
    }

    [Fact]
    public void LoadFromJson_HeartbeatZero_Allowed()
    {
        // 0 ist ein gültiger Wert, der den Heartbeat deaktiviert
        var path = WriteConfig("""
        {
          "trigger": {
            "heartbeatIntervalSeconds": 0
          }
        }
        """);
        var config = ConfigLoader.Load(path);
        Assert.Equal(0, config.Trigger.HeartbeatIntervalSeconds);
    }

    [Fact]
    public void LoadFromJson_EmptyBlacklists_Allowed()
    {
        var path = WriteConfig("""
        {
          "trigger": {
            "blacklist": {
              "windowClasses": [],
              "processes": []
            }
          }
        }
        """);
        var config = ConfigLoader.Load(path);
        Assert.Empty(config.Trigger.Blacklist.WindowClasses);
        Assert.Empty(config.Trigger.Blacklist.Processes);
    }
}