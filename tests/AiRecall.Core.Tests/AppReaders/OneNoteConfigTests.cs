using System.Text.Json;
using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OneNoteConfig"/> (Spec 0010, Cluster 1).
/// Defaults, JSON-Deserialisierung und Robustheit gegen fehlende Sektion.
/// </summary>
public class OneNoteConfigTests
{
    [Fact]
    public void Defaults_AreSetAsSpecified()
    {
        var cfg = new OneNoteConfig();

        Assert.True(cfg.Enabled,                "Default Enabled must be true");
        Assert.Equal(256,    cfg.MaxContentKB);
        Assert.False(cfg.IncludeImages,         "IncludeImages default off wegen Base64-Inflation");
        Assert.True(cfg.IncludeTags);
        Assert.Equal("PageAndSection", cfg.HierarchyDepth);
        Assert.Equal("WindowsApi",    cfg.ActivePageStrategy);
        Assert.Equal(0,               cfg.PollIntervalSeconds);
        Assert.NotNull(cfg.SkipNotebookPatterns);
        Assert.Empty(cfg.SkipNotebookPatterns);
    }

    [Fact]
    public void Deserialize_FullJson_ProducesExpectedValues()
    {
        var json = """
        {
          "enabled": false,
          "maxContentKB": 128,
          "includeImages": true,
          "includeTags": false,
          "hierarchyDepth": "PageAndSectionAndNotebook",
          "activePageStrategy": "HierarchyXml",
          "pollIntervalSeconds": 30,
          "skipNotebookPatterns": ["*.deleted", "Archive 2024"]
        }
        """;

        var cfg = JsonSerializer.Deserialize<OneNoteConfig>(json);

        Assert.NotNull(cfg);
        Assert.False(cfg!.Enabled);
        Assert.Equal(128, cfg.MaxContentKB);
        Assert.True(cfg.IncludeImages);
        Assert.False(cfg.IncludeTags);
        Assert.Equal("PageAndSectionAndNotebook", cfg.HierarchyDepth);
        Assert.Equal("HierarchyXml",               cfg.ActivePageStrategy);
        Assert.Equal(30, cfg.PollIntervalSeconds);
        Assert.Equal(new[] { "*.deleted", "Archive 2024" }, cfg.SkipNotebookPatterns);
    }

    [Fact]
    public void Deserialize_EmptyObject_UsesDefaults()
    {
        var cfg = JsonSerializer.Deserialize<OneNoteConfig>("{}")!;

        Assert.True(cfg.Enabled);
        Assert.Equal(256,    cfg.MaxContentKB);
        Assert.False(cfg.IncludeImages);
        Assert.True(cfg.IncludeTags);
        Assert.Equal("PageAndSection", cfg.HierarchyDepth);
        Assert.Equal("WindowsApi",    cfg.ActivePageStrategy);
        Assert.Equal(0,               cfg.PollIntervalSeconds);
    }

    [Fact]
    public void AppReaderConfig_ContainsOneNoteSubConfig()
    {
        var cfg = new AppReaderConfig();
        Assert.NotNull(cfg.OneNote);
        Assert.IsType<OneNoteConfig>(cfg.OneNote);
        // Same defaults as direct ctor
        Assert.True(cfg.OneNote.Enabled);
        Assert.Equal("WindowsApi", cfg.OneNote.ActivePageStrategy);
    }

    [Fact]
    public void DefaultConfig_HasOnenoteSection()
    {
        // Default-Config in src/AiRecall.Cli/default-config.json muss die
        // AppReaderConfig.OneNote-Sektion enthalten (Spec 0010 §Konfiguration).
        // Walk-Up-Methode sucht AiRecall.sln von BaseDirectory aus aufwaerts —
        // robust gegen unterschiedliche Working-Directories in CI vs. lokal.
        var dir = AppContext.BaseDirectory;
        string? repoRoot = null;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AiRecall.sln"))) { repoRoot = dir; break; }
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        Assert.False(string.IsNullOrEmpty(repoRoot), $"AiRecall.sln not found above {AppContext.BaseDirectory}");

        var jsonPath = Path.Combine(repoRoot!, "src", "AiRecall.Cli", "default-config.json");
        Assert.True(File.Exists(jsonPath), $"default-config.json not found at {jsonPath}");

        var json = File.ReadAllText(jsonPath);
        var doc = JsonDocument.Parse(json);

        var onenoteNode = doc.RootElement
            .GetProperty("appReader")
            .GetProperty("onenote");

        Assert.True(onenoteNode.GetProperty("enabled").GetBoolean());
        Assert.Equal(256,    onenoteNode.GetProperty("maxContentKB").GetInt32());
        Assert.Equal("PageAndSection", onenoteNode.GetProperty("hierarchyDepth").GetString());
        Assert.Equal("WindowsApi",     onenoteNode.GetProperty("activePageStrategy").GetString());
    }
}
