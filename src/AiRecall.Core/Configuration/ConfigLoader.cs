using System.Text.Json;

namespace AiRecall.Core.Configuration;

/// <summary>
/// Loads <see cref="AppConfig"/> from a JSON file.
/// Resolution order:
///   1. explicit <c>path</c> argument
///   2. <c>%APPDATA%/AiRecall/config.json</c> on Windows
///   3. <c>default-config.json</c> next to the executable
///   4. <see cref="AppConfig"/> with all-default values
/// </summary>
public static class ConfigLoader
{
    public const string DefaultConfigFileName = "default-config.json";
    public const string AppDataSubdirectory = "AiRecall";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load(string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath);
        candidates.Add(DefaultUserConfigPath());
        candidates.Add(Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName));

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return LoadFromFile(path);
            }
        }

        return new AppConfig();
    }

    public static string DefaultUserConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppDataSubdirectory,
        "config.json");

    private static AppConfig LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        return config ?? new AppConfig();
    }
}
