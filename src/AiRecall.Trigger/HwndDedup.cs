using System.Globalization;
using System.Text.Json;

namespace AiRecall.Trigger;

/// <summary>
/// HWND-basierte Hash-Dedup (Spec 0005 §Pipeline-Schritt 9, Diskussion 2026-07-04
/// Punkt 4: Hash pro Fenster, nicht pro App).
///
/// Im Gegensatz zur pro-App-Variante wird hier der HWND als Hex-String
/// serialisiert, weil <see cref="IntPtr"/> nicht direkt JSON-serialisierbar
/// ist. State wird in <c>%APPDATA%/AiRecall/hwnd-dedup-state.json</c>
/// persistiert.
///
/// Hinweis HWND-Recycling: HWNDs können nach Window-Destroy recycelt werden
/// (selten, aber möglich). In dem Fall würde der neue HWND-Inhaber den
/// alten Hash "erben". Für MVP1 akzeptabel — würde höchstens zu einem
/// übersprungenen Capture führen, nicht zu Datenverlust. Cleanup-Strategie
/// (z. B. periodisches Pruning inaktiver HWNDs) ist Out of Scope für MVP1.
/// </summary>
public sealed class HwndDedup
{
    private readonly string? _stateFile;
    private readonly Dictionary<IntPtr, DedupEntry> _state = new();

    public HwndDedup(string? stateFile = null)
    {
        _stateFile = stateFile;
        Load();
    }

    /// <summary>
    /// True, wenn der Hash identisch zum letzten Eintrag für das HWND ist.
    /// In dem Fall wird der Timestamp aktualisiert (Activity-Tracking) und
    /// kein neuer Capture geschrieben.
    /// </summary>
    public bool IsDuplicate(IntPtr hwnd, string hash, DateTimeOffset now)
    {
        if (_state.TryGetValue(hwnd, out var entry) && entry.Hash == hash)
        {
            entry.LastSeen = now;
            _state[hwnd] = entry;
            return true;
        }
        return false;
    }

    /// <summary>Markiert diesen Hash als letzten für das HWND.</summary>
    public void Mark(IntPtr hwnd, string hash, DateTimeOffset now)
    {
        _state[hwnd] = new DedupEntry(hash, now);
        Save();
    }

    /// <summary>Setzt den State zurück (für Tests).</summary>
    public void Reset() => _state.Clear();

    /// <summary>Anzahl der getrackten HWNDs (für Diagnostik/Tests).</summary>
    public int TrackedHwndCount => _state.Count;

    /// <summary>Persistiert den State auf Disk. Idempotent, kein IO wenn keine Datei konfiguriert.</summary>
    public void Save()
    {
        if (_stateFile is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_stateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                _state.ToDictionary(
                    kv => FormatHwnd(kv.Key),
                    kv => new PersistedEntry(kv.Value.Hash, kv.Value.LastSeen)));
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
                if (TryParseHwnd(kv.Key, out var hwnd))
                {
                    _state[hwnd] = new DedupEntry(kv.Value.Hash, kv.Value.LastSeen);
                }
            }
        }
        catch
        {
            // Korrupte Datei → leerer State, weiter geht's.
        }
    }

    /// <summary>Format: <c>0xDEADBEEF</c> (uppercase hex).</summary>
    public static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

    public static bool TryParseHwnd(string s, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrEmpty(s)) return false;
        var trimmed = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? s[2..]
            : s;
        if (long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            hwnd = new IntPtr(value);
            return true;
        }
        return false;
    }

    private record struct DedupEntry(string Hash, DateTimeOffset LastSeen);
    private sealed record PersistedEntry(string Hash, DateTimeOffset LastSeen);
}