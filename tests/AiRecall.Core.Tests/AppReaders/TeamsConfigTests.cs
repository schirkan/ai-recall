using System.Text.Json;
using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="TeamsConfig"/> (Spec 0011, Cluster 1).
/// Defaults, JSON-Deserialisierung und Robustheit gegen fehlende Sektion.
/// </summary>
public class TeamsConfigTests
{
    [Fact]
    public void Defaults_AreSetAsSpecified()
    {
        var cfg = new TeamsConfig();

        Assert.True(cfg.Enabled,                "Default Enabled must be true");
        Assert.Equal(512,    cfg.MaxContentKB);
        Assert.True(cfg.UseCdpIfAvailable,      "CDP opt-in default on");
        Assert.Equal("http://localhost:9222", cfg.CdpEndpoint);
        Assert.Equal(1500,   cfg.CdpTimeoutMs);
        Assert.Equal("Auto", cfg.PreferredStrategy);
        Assert.Equal(0,      cfg.PollIntervalSeconds);
        Assert.NotNull(cfg.SkipChatPatterns);
        Assert.Empty(cfg.SkipChatPatterns);
        Assert.NotNull(cfg.IncludeSenderPatterns);
        Assert.Empty(cfg.IncludeSenderPatterns);
    }

    [Fact]
    public void Deserialize_FullJson_ProducesExpectedValues()
    {
        var json = """
        {
          "enabled": false,
          "maxContentKB": 256,
          "useCdpIfAvailable": false,
          "cdpEndpoint": "http://localhost:9333",
          "cdpTimeoutMs": 2500,
          "preferredStrategy": "Uia",
          "pollIntervalSeconds": 60,
          "skipChatPatterns": ["Meeting", "Status Bot"],
          "includeSenderPatterns": ["Alice", "Bob"]
        }
        """;

        var cfg = JsonSerializer.Deserialize<TeamsConfig>(json);

        Assert.NotNull(cfg);
        Assert.False(cfg!.Enabled);
        Assert.Equal(256,                       cfg.MaxContentKB);
        Assert.False(cfg.UseCdpIfAvailable);
        Assert.Equal("http://localhost:9333",   cfg.CdpEndpoint);
        Assert.Equal(2500,                      cfg.CdpTimeoutMs);
        Assert.Equal("Uia",                     cfg.PreferredStrategy);
        Assert.Equal(60,                        cfg.PollIntervalSeconds);
        Assert.Equal(new[] { "Meeting", "Status Bot" }, cfg.SkipChatPatterns);
        Assert.Equal(new[] { "Alice", "Bob" },           cfg.IncludeSenderPatterns);
    }

    [Fact]
    public void Deserialize_EmptyObject_UsesDefaults()
    {
        var cfg = JsonSerializer.Deserialize<TeamsConfig>("{}")!;

        Assert.True(cfg.Enabled);
        Assert.Equal(512,    cfg.MaxContentKB);
        Assert.True(cfg.UseCdpIfAvailable);
        Assert.Equal("Auto", cfg.PreferredStrategy);
    }

    [Fact]
    public void AppReaderConfig_ContainsTeamsSubConfig()
    {
        var cfg = new AppReaderConfig();
        Assert.NotNull(cfg.Teams);
        Assert.IsType<TeamsConfig>(cfg.Teams);
        Assert.True(cfg.Teams.Enabled);
        Assert.Equal("Auto", cfg.Teams.PreferredStrategy);
    }

    [Fact]
    public void DefaultConfig_HasTeamsSection()
    {
        // Default-Config in src/AiRecall.Cli/default-config.json muss die
        // AppReaderConfig.Teams-Sektion enthalten (Spec 0011 Cluster 1).
        var dir = AppContext.BaseDirectory;
        string? repoRoot = null;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AiRecall.sln"))) { repoRoot = dir; break; }
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        Assert.False(string.IsNullOrEmpty(repoRoot));

        var jsonPath = Path.Combine(repoRoot!, "src", "AiRecall.Cli", "default-config.json");
        Assert.True(File.Exists(jsonPath), $"default-config.json not found at {jsonPath}");

        var json = File.ReadAllText(jsonPath);
        var doc = JsonDocument.Parse(json);

        var teamsNode = doc.RootElement
            .GetProperty("appReader")
            .GetProperty("teams");

        Assert.True(teamsNode.GetProperty("enabled").GetBoolean());
        Assert.Equal(512,                       teamsNode.GetProperty("maxContentKB").GetInt32());
        Assert.True(teamsNode.GetProperty("useCdpIfAvailable").GetBoolean());
        Assert.Equal("http://localhost:9222",   teamsNode.GetProperty("cdpEndpoint").GetString());
        Assert.Equal("Auto",                    teamsNode.GetProperty("preferredStrategy").GetString());
    }
}
