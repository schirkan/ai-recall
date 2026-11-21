using System.Text.Json;
using AiRecall.Core.Configuration;
using AiRecall.Core.Tessdata;
using AiRecall.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public sealed class ConfigPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-config-tests-{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public void SettingsSave_PreservesUnknownRootAndNestedFieldsInCommentedConfig()
    {
        const string original = """
            {
              // ConfigLoader accepts comments and trailing commas.
              "OCR": {"Engine": "tesseract", "futureOcr": [null, {"flag": true,},],},
              "appReader": {"browser": {"cdp": {"futureMode": "synthetic",},},},
              "Future": {"items": [1, null,],},
              "future": false,
            }
            """;
        WriteOriginal(original);
        var config = ConfigLoader.Load(ConfigPath);
        config.Ocr.Engine = "test-engine";
        config.AppReader.Browser.Cdp.Enabled = true;

        ConfigSerializer.SaveAtomic(config, ConfigPath);

        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var root = saved.RootElement;
        Assert.True(root.GetProperty("OCR").GetProperty("futureOcr")[1].GetProperty("flag").GetBoolean());
        Assert.False(root.TryGetProperty("ocr", out _));
        Assert.Equal("synthetic", root.GetProperty("appReader").GetProperty("browser").GetProperty("cdp").GetProperty("futureMode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("Future").GetProperty("items")[1].ValueKind);
        Assert.False(root.GetProperty("future").GetBoolean());
        var reloaded = ConfigLoader.Load(ConfigPath);
        Assert.Equal("test-engine", reloaded.Ocr.Engine);
        Assert.True(reloaded.AppReader.Browser.Cdp.Enabled);
        Assert.Equal(original, File.ReadAllText(ConfigPath + ".bak"));
    }

    [Fact]
    public void TessdataOptOut_CreatesMinimalConfigAndSuppressesPromptWithoutPriorSettings()
    {
        ConfigSerializer.SaveTessdataAutoDownloadAtomic(ConfigPath, enabled: false);

        var config = ConfigLoader.Load(ConfigPath);
        Assert.False(config.Ocr.AutoDownloadTessdata);
        Assert.Equal(new AppConfig().Ocr.Engine, config.Ocr.Engine);
        Assert.Empty(new TessdataManager().FindMissingLanguages(config));
        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var ocr = Assert.Single(saved.RootElement.EnumerateObject());
        Assert.Equal("ocr", ocr.Name);
        Assert.Equal("autoDownloadTessdata", Assert.Single(ocr.Value.EnumerateObject()).Name);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void TessdataOptOut_OnlyChangesOptOutFlagInCommentedConfig()
    {
        const string original = """
            {
              // A partial config using case-insensitive names.
              "Ocr": {"AutoDownloadTessdata": true, "languages": ["fra",], "future": true,},
              "unrelated": ["keep",],
            }
            """;
        WriteOriginal(original);

        ConfigSerializer.SaveTessdataAutoDownloadAtomic(ConfigPath, enabled: false);

        var config = ConfigLoader.Load(ConfigPath);
        Assert.False(config.Ocr.AutoDownloadTessdata);
        Assert.Equal("fra", Assert.Single(config.Ocr.Languages));
        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.True(saved.RootElement.GetProperty("Ocr").GetProperty("future").GetBoolean());
        Assert.Equal("keep", saved.RootElement.GetProperty("unrelated")[0].GetString());
        Assert.Equal(2, saved.RootElement.EnumerateObject().Count());
        Assert.Equal(original, File.ReadAllText(ConfigPath + ".bak"));
    }

    [Fact]
    public void MalformedConfig_TessdataOptOutLeavesOriginalAndSettingsCanRepairWithBackup()
    {
        const string malformed = "{broken:";
        WriteOriginal(malformed);
        Assert.ThrowsAny<JsonException>(() => ConfigSerializer.SaveTessdataAutoDownloadAtomic(ConfigPath, enabled: false));
        Assert.Equal(malformed, File.ReadAllText(ConfigPath));

        ConfigSerializer.SaveAtomic(new AppConfig(), ConfigPath);

        Assert.Equal(malformed, File.ReadAllText(ConfigPath + ".bak"));
        Assert.True(ConfigLoader.Load(ConfigPath).Ocr.AutoDownloadTessdata);
    }

    private void WriteOriginal(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
