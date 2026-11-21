using System.Text.Json;
using System.Text.Json.Nodes;
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
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = ConfigLoader.DocumentOptions.CommentHandling,
        AllowTrailingCommas = ConfigLoader.DocumentOptions.AllowTrailingCommas,
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
    /// Saves all settings, retaining unrecognized properties in an existing JSON object.
    /// A malformed file can be repaired from Settings; the original is still backed up.
    /// </summary>
    public static void SaveAtomic(AppConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(path);

        JsonObject root;
        try { root = ReadExisting(path); }
        catch (JsonException) { root = new JsonObject(); }

        Overlay(root, (JsonObject)JsonSerializer.SerializeToNode(config, Options)!);
        WriteAtomic(root, path);
    }

    /// <summary>Updates only the tessdata prompt preference, even before Settings have been saved.</summary>
    public static void SaveTessdataAutoDownloadAtomic(string path, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var root = ReadExisting(path);
        Overlay(root, new JsonObject
        {
            ["ocr"] = new JsonObject { ["autoDownloadTessdata"] = enabled }
        });
        WriteAtomic(root, path);
    }

    private static JsonObject ReadExisting(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        return JsonNode.Parse(File.ReadAllText(path), documentOptions: ConfigLoader.DocumentOptions) as JsonObject
            ?? throw new JsonException("Expected a configuration JSON object");
    }

    private static void Overlay(JsonObject target, JsonObject changes)
    {
        foreach (var (name, value) in changes)
        {
            // Like the case-insensitive loader, apply updates to the last matching property.
            // Do not use a case-insensitive DOM: distinct unknown keys must survive unchanged.
            var key = target.LastOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
            if (value is JsonObject patch && target[key] is JsonObject existing)
                Overlay(existing, patch);
            else
                target[key] = value?.DeepClone();
        }
    }

    private static void WriteAtomic(JsonObject root, string path)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 1. Backup existing file (best-effort: Bug-Bash 2026-07-05 I-7).
        // Wenn der Backup-Pfad gelockt ist (z. B. weil ein anderer Prozess
        // oder die vorherige Schreibaktion ihn noch offen haelt), soll der
        // Save trotzdem funktionieren. Backup-Verlust ist akzeptabler als
        // ein kompletter Save-Fehler fuer ein Personal-Tool.
        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch
            {
                // bewusst geschluckt — siehe Kommentar oben
            }
        }

        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmpPath, root.ToJsonString(Options));
            if (File.Exists(path)) File.Replace(tmpPath, path, destinationBackupFileName: null);
            else File.Move(tmpPath, path);
        }
        finally
        {
            try { File.Delete(tmpPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}