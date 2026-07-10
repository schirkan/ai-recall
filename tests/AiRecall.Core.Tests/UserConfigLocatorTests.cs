using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests;

/// <summary>
/// Tests für <see cref="UserConfigLocator"/> (Spec 0016 — First-Run-Settings-Dialog).
/// Verifiziert, dass <c>loadedFromUserFile</c> korrekt gesetzt wird.
///
/// <b>Test-Strategie:</b> Da <see cref="UserConfigLocator.DefaultUserConfigPath()"/>
/// die Windows Known Folder API nutzt (nicht <c>%APPDATA%</c>), sichern wir
/// die echte User-Config falls vorhanden, manipulieren sie im Test und spielen
/// sie im Dispose zurück.
/// </summary>
public class UserConfigLocatorTests : IDisposable
{
    private readonly string _configPath;
    private readonly string? _backupContent;
    private readonly bool _fileExistedInitially;

    public UserConfigLocatorTests()
    {
        _configPath = UserConfigLocator.GetUserConfigPath();
        var dir = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(_configPath))
        {
            _fileExistedInitially = true;
            _backupContent = File.ReadAllText(_configPath);
            File.Delete(_configPath);
        }
        else
        {
            _fileExistedInitially = false;
            _backupContent = null;
        }
    }

    public void Dispose()
    {
        // Immer aufräumen — Test-Verzeichnis ist heilig.
        if (File.Exists(_configPath))
        {
            try { File.Delete(_configPath); } catch { /* ignore */ }
        }

        if (_fileExistedInitially && _backupContent is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                File.WriteAllText(_configPath, _backupContent);
            }
            catch { /* ignore — best-effort restore */ }
        }
    }

    [Fact]
    public void LoadOrDefault_NoFile_ReturnsDefaults_LoadedFromUserFileFalse()
    {
        // Arrange: keine Datei (ctor hat sie gelöscht, falls vorhanden).
        Assert.False(File.Exists(_configPath));

        // Act
        var cfg = UserConfigLocator.LoadOrDefault(out var loadedFromUserFile);

        // Assert
        Assert.False(loadedFromUserFile);
        Assert.NotNull(cfg);
        Assert.True(cfg.App.FirstRun);
    }

    [Fact]
    public void LoadOrDefault_ValidFile_ReturnsConfig_LoadedFromUserFileTrue()
    {
        // Arrange
        File.WriteAllText(_configPath, "{\"app\":{\"firstRun\":false}}");

        // Act
        var cfg = UserConfigLocator.LoadOrDefault(out var loadedFromUserFile);

        // Assert
        Assert.True(loadedFromUserFile);
        Assert.NotNull(cfg);
        Assert.False(cfg.App.FirstRun);
    }

    [Fact]
    public void LoadOrDefault_MalformedFile_ReturnsDefaults_LoadedFromUserFileFalse()
    {
        // Arrange — kaputte JSON, sodass ConfigLoader.Load() wirft.
        File.WriteAllText(_configPath, "{ this is not valid json");

        // Act
        var cfg = UserConfigLocator.LoadOrDefault(out var loadedFromUserFile);

        // Assert — Malformed = treat as first run (Spec 0016 §Verworfen).
        Assert.False(loadedFromUserFile);
        Assert.NotNull(cfg);
        Assert.True(cfg.App.FirstRun);
    }

    [Fact]
    public void LoadOrDefault_OldOverloadWithoutOut_StillWorks()
    {
        // Rückwärtskompatibilität: alte Aufrufer ohne out-Parameter müssen weiterhin
        // funktionieren und dieselbe Config liefern.
        var cfgOld = UserConfigLocator.LoadOrDefault();
        var cfgNew = UserConfigLocator.LoadOrDefault(out var loadedFromUserFile);

        Assert.Equal(cfgOld.App.FirstRun, cfgNew.App.FirstRun);
        Assert.False(loadedFromUserFile);
    }

    [Fact]
    public void AppSettings_FirstRun_DefaultsToTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.FirstRun);
    }

    [Fact]
    public void AppConfig_AppProperty_DefaultsToNonNull()
    {
        var cfg = new AppConfig();
        Assert.NotNull(cfg.App);
        Assert.True(cfg.App.FirstRun);
    }
}