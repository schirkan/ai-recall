using AiRecall.Core.Configuration;

namespace AiRecall.Trigger;

/// <summary>
/// Resolves the on-disk location of the user's AiRecall config file and
/// loads it with a fallback to <see cref="AppConfig"/> defaults (Spec 0009
/// §Persistenz §Load). Thin wrapper around <see cref="ConfigLoader"/> —
/// kept separate so Trigger-app code doesn't depend on config-file details.
/// </summary>
public static class UserConfigLocator
{
    /// <summary>Path to the user-specific config file (typically <c>%APPDATA%/AiRecall/config.json</c>).</summary>
    public static string GetUserConfigPath() => ConfigLoader.DefaultUserConfigPath();

    /// <summary>
    /// Loads the user config from <see cref="GetUserConfigPath"/>, or returns
    /// a fresh <see cref="AppConfig"/> if the file does not exist or is malformed.
    /// <paramref name="logger"/> is called for diagnostic messages (e.g. Serilog).
    /// </summary>
    public static AppConfig LoadOrDefault(Action<string>? logger = null)
    {
        var cfg = LoadOrDefault(out _, logger);
        return cfg;
    }

    /// <summary>
    /// Wie <see cref="LoadOrDefault(Action{string}?)"/>, meldet aber zusätzlich
    /// über <paramref name="loadedFromUserFile"/>, ob eine echte User-Config
    /// geladen wurde (true) oder ob mit Defaults gestartet wurde, weil die Datei
    /// nicht existiert oder malformed ist (false) — Spec 0016 (First-Run-Dialog).
    /// </summary>
    /// <param name="loadedFromUserFile">true, wenn die Datei existierte und erfolgreich geladen wurde.</param>
    /// <param name="logger">Optionaler Diagnostic-Callback (z. B. Serilog).</param>
    public static AppConfig LoadOrDefault(out bool loadedFromUserFile, Action<string>? logger = null)
    {
        var path = GetUserConfigPath();
        if (!File.Exists(path))
        {
            logger?.Invoke($"User config not found at {path}, using defaults");
            loadedFromUserFile = false;
            return new AppConfig();
        }

        try
        {
            var cfg = ConfigLoader.Load(path);
            loadedFromUserFile = true;
            return cfg;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"User config at {path} is malformed: {ex.Message}, using defaults");
            // Malformed = treat as first run: User soll die kaputte Datei via
            // Settings-Dialog reparieren können (Spec 0016 §Verworfen: malformed
            // wie "noch nie gespeichert" behandeln).
            loadedFromUserFile = false;
            return new AppConfig();
        }
    }
}