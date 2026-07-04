using System.Text.Json;
using System.Text.Json.Serialization;
using AiRecall.Core.Configuration;

namespace AiRecall.Trigger;

/// <summary>
/// Pure-logic JSON serializer for <see cref="AppConfig"/> (Spec 0009 §Persistenz §Save).
/// Atomic write: temp file + rename. Camel-case output to match existing
/// <c>default-config.json</c> convention.
///
/// Lives outside the WinForms UI so it can be unit-tested without UI.
/// </summary>
public static class ConfigSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Serializes an <see cref="AppConfig"/> to JSON (camelCase, indented).</summary>
    public static string Serialize(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, Options);
    }

    /// <summary>Deserializes JSON to an <see cref="AppConfig"/>; throws on malformed JSON.</summary>
    public static AppConfig Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<AppConfig>(json, Options)
            ?? throw new InvalidOperationException("Deserialization returned null");
    }

    /// <summary>
    /// Writes <paramref name="config"/> atomically to <paramref name="path"/>:
    /// 1. backup existing file to <c>{path}.bak</c>
    /// 2. write to <c>{path}.tmp</c>
    /// 3. move tmp to path (overwrite)
    /// </summary>
    public static void SaveAtomic(AppConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 1. Backup
        if (File.Exists(path))
        {
            var backupPath = path + ".bak";
            File.Copy(path, backupPath, overwrite: true);
        }

        // 2. Write tmp
        var tmpPath = path + ".tmp";
        var json = Serialize(config);
        File.WriteAllText(tmpPath, json);

        // 3. Atomic move
        if (File.Exists(path)) File.Replace(tmpPath, path, destinationBackupFileName: null);
        else File.Move(tmpPath, path);
    }
}