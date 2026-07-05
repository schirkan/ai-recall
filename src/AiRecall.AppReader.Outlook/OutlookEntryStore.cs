using System.Text.Json;
using System.Text.Json.Serialization;
using AiRecall.Core.Configuration;
using Serilog;

namespace AiRecall.AppReader.Outlook;

/// <summary>
/// Persistenter EntryID-Dedup-State fuer den Outlook-Mail-Log (Spec 0004
/// Iter. 3 §„EntryStore-Format").
///
/// <para>
/// Outlook-MailItems haben eine eindeutige <c>EntryID</c> (~70 Zeichen),
/// die ueber die MAPI-Session stabil ist. Wir merken uns alle bereits
/// persistierten (oder explizit geskippten) EntryIDs in einer HashSet, so
/// dass jeder Sweep ohne Duplikat-Persistierung laeuft.
/// </para>
///
/// <para>
/// State wird in <c>%APPDATA%/AiRecall/outlook-seen.json</c> gespeichert
/// (Pfad siehe <see cref="OutlookConfig.DefaultSeenStatePath"/>), Plain-JSON
/// (System.Text.Json), gitignored.
/// </para>
///
/// <para>
/// Save ist <b>atomar</b> via <see cref="File.Replace(string, string, string?)"/>:
/// erst nach <c>%APPDATA%/AiRecall/outlook-seen.json.tmp</c> schreiben,
/// dann atomar ersetzen. Verhindert halbe Files bei Crash mitten im Write.
/// </para>
///
/// <para>
/// Threadsafe: alle oeffentlichen Methoden nehmen ein internes
/// <see cref="lock"/>. Iter. 4 serialisiert zusaetzlich ueber den
/// AppReaderPollService, aber Defensive-Lock macht die Klasse auch ohne
/// externen Schutz sicher.
/// </para>
/// </summary>
public sealed class OutlookEntryStore
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSweepAt;
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Konstruktor mit explizitem Pfad (fuer Tests: temp-Verzeichnis).
    /// Fuer Production: <see cref="CreateDefault(ILogger)"/>.
    /// </summary>
    public OutlookEntryStore(string filePath, ILogger logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Factory: Konstruiert einen Store mit dem Default-Pfad
    /// (<c>%APPDATA%/AiRecall/outlook-seen.json</c>) und laedt existierenden
    /// State. Existiert die Datei nicht oder ist sie korrupt, startet der
    /// Store mit leerem Set (siehe <see cref="Load"/>).
    /// </summary>
    public static OutlookEntryStore CreateDefault(ILogger logger)
    {
        var fullPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            OutlookConfig.DefaultSeenStatePath());
        var store = new OutlookEntryStore(fullPath, logger);
        store.Load();
        return store;
    }

    /// <summary>Anzahl gemerkter EntryIDs (fuer Diagnostics/Tests).</summary>
    public int Count
    {
        get { lock (_gate) return _seen.Count; }
    }

    /// <summary>Pfad zur State-Datei (read-only, fuer Diagnostics).</summary>
    public string FilePath => _filePath;

    /// <summary>Zeitpunkt des letzten Sweeps (null wenn noch nie gesweept).</summary>
    public DateTimeOffset? LastSweepAt
    {
        get { lock (_gate) return _lastSweepAt; }
    }

    /// <summary>
    /// Prueft, ob die EntryID bereits gemerkt wurde. O(1).
    /// </summary>
    public bool IsSeen(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate) return _seen.Contains(entryId);
    }

    /// <summary>
    /// Markiert die EntryID als gesehen und persistiert sofort (atomar).
    /// Idempotent: doppeltes MarkSeen derselben ID ist ein No-Op was den
    /// Set betrifft, loest aber trotzdem Save aus (kostet ~5 ms).
    /// </summary>
    public void MarkSeen(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return;
        lock (_gate)
        {
            if (!_seen.Add(entryId))
            {
                return; // bereits drin, kein Save noetig
            }
            _dirty = true;
            Save_NoLock();
        }
    }

    /// <summary>
    /// Markiert den Zeitpunkt des letzten Sweeps (fuer Diagnostics; hat
    /// keine Filter-Funktion — EntryID-Dedup ist robuster).
    /// </summary>
    public void MarkSweepCompleted(DateTimeOffset when)
    {
        lock (_gate)
        {
            _lastSweepAt = when;
            _dirty = true;
            Save_NoLock();
        }
    }

    /// <summary>
    /// Laedt den State aus der Datei. Existiert die Datei nicht oder ist
    /// sie korrupt, startet der Store mit leerem Set (kein Throw, nur
    /// Logger-Warning). Idempotent: kann mehrfach aufgerufen werden.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            _seen = new HashSet<string>(StringComparer.Ordinal);
            _lastSweepAt = null;
            _dirty = false;

            if (!File.Exists(_filePath))
            {
                _logger.Information("OutlookEntryStore: no existing state at {Path}, starting empty", _filePath);
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.Warning("OutlookEntryStore: empty state file at {Path}, starting empty", _filePath);
                    return;
                }

                var dto = JsonSerializer.Deserialize<EntryStoreDto>(json, JsonOpts);
                if (dto?.EntryIds is not null)
                {
                    foreach (var id in dto.EntryIds)
                    {
                        if (!string.IsNullOrWhiteSpace(id)) _seen.Add(id);
                    }
                }
                _lastSweepAt = dto?.LastSweepAt;
                _logger.Information("OutlookEntryStore: loaded {Count} entry IDs from {Path}", _seen.Count, _filePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OutlookEntryStore: corrupted state file at {Path}, starting empty", _filePath);
                _seen = new HashSet<string>(StringComparer.Ordinal);
                _lastSweepAt = null;
            }
        }
    }

    /// <summary>
    /// Schreibt den aktuellen State auf Disk (atomar via File.Replace).
    /// Normalerweise nicht noetig (Save passiert automatisch bei
    /// MarkSeen/MarkSweepCompleted), aber nuetzlich fuer Tests und fuer
    /// explizite Flush-Calls vor App-Shutdown.
    /// </summary>
    public void Save()
    {
        lock (_gate) Save_NoLock();
    }

    /// <summary>
    /// Interner Save ohne Lock — Aufrufer muss <see cref="_gate"/> halten.
    /// Schreibt nach <c>.tmp</c> und ersetzt dann atomar via File.Replace.
    /// </summary>
    private void Save_NoLock()
    {
        if (!_dirty) return;

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dto = new EntryStoreDto
            {
                Version = 1,
                LastSweepAt = _lastSweepAt,
                EntryIds = _seen.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);

            var tmpPath = _filePath + ".tmp";
            File.WriteAllText(tmpPath, json);

            // File.Replace ist atomar auf NTFS; falls Zieldatei nicht existiert,
            // faellt zurueck auf File.Move.
            if (File.Exists(_filePath))
            {
                File.Replace(tmpPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmpPath, _filePath);
            }

            _dirty = false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OutlookEntryStore: failed to save state to {Path}", _filePath);
            // Tmp-File aufraeumen falls vorhanden
            try
            {
                var tmpPath = _filePath + ".tmp";
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>DTO fuer JSON-Serialisierung. Public fuer Tests.</summary>
    public sealed class EntryStoreDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("lastSweepAt")]
        public DateTimeOffset? LastSweepAt { get; set; }

        [JsonPropertyName("entryIds")]
        public List<string> EntryIds { get; set; } = new();
    }
}