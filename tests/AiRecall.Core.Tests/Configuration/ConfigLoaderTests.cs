using System.Text.Json;
using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tests.Configuration;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ai-recall-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Load_ExplicitMissing_ReturnsDefaults()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.json");
        var config = ConfigLoader.Load(missing);
        Assert.NotNull(config);
        Assert.Equal("capture", config.Capture.RootPath);
        Assert.Equal("tesseract", config.Ocr.Engine);
        Assert.Equal("deu", config.Ocr.Languages[0]);
    }

    [Fact]
    public void Load_AppliesJsonValues()
    {
        File.WriteAllText(_configPath, """
        {
          "capture": { "rootPath": "my-captures" },
          "ocr": { "engine": "tesseract", "languages": ["eng"], "tessDataPath": "td" },
          "screenRecorder": { "throttleMs": 500, "ignoreApps": ["x"] }
        }
        """);
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal("my-captures", config.Capture.RootPath);
        Assert.Equal(500, config.ScreenRecorder.ThrottleMs);
        Assert.Single(config.ScreenRecorder.IgnoreApps);
        Assert.Equal("x", config.ScreenRecorder.IgnoreApps[0]);
        Assert.Single(config.Ocr.Languages);
        Assert.Equal("eng", config.Ocr.Languages[0]);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        File.WriteAllText(_configPath, """
        {
          // comment
          "capture": { "rootPath": "c", }, // trailing comma
        }
        """);
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal("c", config.Capture.RootPath);
    }

    [Fact]
    public void Load_CaseInsensitivePropertyNames()
    {
        File.WriteAllText(_configPath, """
        { "CAPTURE": { "ROOTPATH": "x" } }
        """);
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal("x", config.Capture.RootPath);
    }

    [Fact]
    public void Load_EmptyJson_ReturnsDefaults()
    {
        File.WriteAllText(_configPath, "{}");
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal("capture", config.Capture.RootPath);
        Assert.Equal("png", config.Capture.ScreenshotFormat);
    }

    [Fact]
    public void DefaultUserConfigPath_IsUnderAppData()
    {
        var path = ConfigLoader.DefaultUserConfigPath();
        Assert.Contains("AiRecall", path);
        Assert.EndsWith("config.json", path);
    }
}
