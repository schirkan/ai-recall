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
        var path = GetUserConfigPath();
        if (!File.Exists(path))
        {
            logger?.Invoke($"User config not found at {path}, using defaults");
            return new AppConfig();
        }

        try
        {
            return ConfigLoader.Load(path);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"User config at {path} is malformed: {ex.Message}, using defaults");
            return new AppConfig();
        }
    }
}