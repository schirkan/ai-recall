using System.Text.Json;

namespace AiRecall.Core.Util;

/// <summary>
/// Hash-basierte Dedup (Spec 0002 TR-5). Pro Prozess wird der Hash des
/// letzten Captures behalten; identische Hashes führen zu Skip.
/// State wird in <c>%APPDATA%/AiRecall/dedup-state.json</c> persistiert
/// (Plain-JSON, gitignored).
/// </summary>
public sealed class Dedup
{
    private readonly string? _stateFile;
    private readonly Dictionary<string, DedupEntry> _state = new(StringComparer.OrdinalIgnoreCase);

    public Dedup(string? stateFile = null)
    {
        _stateFile = stateFile;
        Load();
    }

    /// <summary>
    /// True, wenn der Hash identisch zum letzten Eintrag für den Prozess ist.
    /// In dem Fall wird der Timestamp aktualisiert (Activity-Tracking) und
    /// kein neuer Capture geschrieben.
    /// </summary>
    public bool IsDuplicate(string processKey, string hash, DateTimeOffset now)
    {
        if (_state.TryGetValue(processKey, out var entry) && entry.Hash == hash)
        {
            entry.LastSeen = now;
            _state[processKey] = entry;
            return true;
        }
        return false;
    }

    /// <summary>Markiert diesen Hash als letzten für den Prozess.</summary>
    public void Mark(string processKey, string hash, DateTimeOffset now)
    {
        _state[processKey] = new DedupEntry(hash, now);
        Save();
    }

    /// <summary>Setzt den State zurück (für Tests).</summary>
    public void Reset() => _state.Clear();

    /// <summary>Persistiert den State auf Disk (idempotent, keine IO wenn keine Datei konfiguriert).</summary>
    public void Save()
    {
        if (_stateFile is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_stateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_state.ToDictionary(kv => kv.Key, kv => new PersistedEntry(kv.Value.Hash, kv.Value.LastSeen)));
            File.WriteAllText(_stateFile, json);
        }
        catch
        {
            // Best effort — wenn die Disk voll ist, läuft das Programm trotzdem weiter.
        }
    }

    private void Load()
    {
        if (_stateFile is null || !File.Exists(_stateFile)) return;
        try
        {
            var json = File.ReadAllText(_stateFile);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PersistedEntry>>(json);
            if (loaded is null) return;
            foreach (var kv in loaded)
            {
                _state[kv.Key] = new DedupEntry(kv.Value.Hash, kv.Value.LastSeen);
            }
        }
        catch
        {
            // Korrupte Datei → leerer State, weiter geht's.
        }
    }

    private record struct DedupEntry(string Hash, DateTimeOffset LastSeen);
    private sealed record PersistedEntry(string Hash, DateTimeOffset LastSeen);
}