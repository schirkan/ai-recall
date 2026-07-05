using AiRecall.AppReader.Outlook;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OutlookEntryStore"/> (Spec 0004 Iter. 3). Verwenden
/// temp-Verzeichnisse statt %APPDATA%, weil Outlook-Store im Test nicht
/// den User-State anfassen darf.
/// </summary>
public class OutlookEntryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public OutlookEntryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AiRecall.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "outlook-seen.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* ignore */ }
    }

    [Fact]
    public void IsSeen_NewEntry_ReturnsFalse()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        Assert.False(store.IsSeen("ENTRY0001"));
    }

    [Fact]
    public void MarkSeen_Persists_LoadReadsIt()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        store.MarkSeen("ENTRY0001");
        store.MarkSeen("ENTRY0002");

        Assert.Equal(2, store.Count);

        // Reload aus gleichem Pfad
        var store2 = new OutlookEntryStore(_statePath, _logger);
        store2.Load();
        Assert.Equal(2, store2.Count);
        Assert.True(store2.IsSeen("ENTRY0001"));
        Assert.True(store2.IsSeen("ENTRY0002"));
        Assert.False(store2.IsSeen("ENTRY0003"));
    }

    [Fact]
    public void MarkSeen_Idempotent_DoesNotDoublePersist()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        store.MarkSeen("ENTRY0001");
        store.MarkSeen("ENTRY0001");
        store.MarkSeen("ENTRY0001");

        Assert.Equal(1, store.Count);

        var store2 = new OutlookEntryStore(_statePath, _logger);
        store2.Load();
        Assert.Equal(1, store2.Count);
    }

    [Fact]
    public void MarkSeen_Multiple_OrderIndependent()
    {
        var s1 = new OutlookEntryStore(_statePath, _logger);
        s1.MarkSeen("AAA");
        s1.MarkSeen("BBB");
        s1.MarkSeen("CCC");

        var s2 = new OutlookEntryStore(_statePath, _logger);
        s2.Load();

        Assert.True(s2.IsSeen("AAA"));
        Assert.True(s2.IsSeen("BBB"));
        Assert.True(s2.IsSeen("CCC"));
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        store.Load();
        Assert.Equal(0, store.Count);
        Assert.Null(store.LastSweepAt);
    }

    [Fact]
    public void Load_EmptyFile_StartsEmpty()
    {
        File.WriteAllText(_statePath, "");
        var store = new OutlookEntryStore(_statePath, _logger);
        store.Load();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Load_CorruptedJson_StartsEmpty_WithNoThrow()
    {
        File.WriteAllText(_statePath, "{ this is not valid json !!!");
        var store = new OutlookEntryStore(_statePath, _logger);
        store.Load(); // darf nicht werfen
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void MarkSweepCompleted_PersistsTimestamp()
    {
        var when = new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.FromHours(2));
        var store = new OutlookEntryStore(_statePath, _logger);
        store.MarkSweepCompleted(when);

        Assert.Equal(when, store.LastSweepAt);

        var store2 = new OutlookEntryStore(_statePath, _logger);
        store2.Load();
        Assert.Equal(when, store2.LastSweepAt);
    }

    [Fact]
    public void IsSeen_EmptyOrNull_ReturnsFalse()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        store.MarkSeen("ENTRY0001");

        Assert.False(store.IsSeen(""));
        Assert.False(store.IsSeen(null!));
    }

    [Fact]
    public void MarkSeen_EmptyOrNull_NoOp()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        store.MarkSeen("");
        store.MarkSeen(null!);

        Assert.Equal(0, store.Count);

        // Datei darf nicht erzeugt worden sein (kein Save getriggert)
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Save_ExplicitFlush_WritesFile()
    {
        var store = new OutlookEntryStore(_statePath, _logger);
        store.MarkSeen("ENTRY0001");

        Assert.True(File.Exists(_statePath));
        var content = File.ReadAllText(_statePath);
        Assert.Contains("ENTRY0001", content);
        Assert.Contains("\"version\": 1", content);
    }

    [Fact]
    public void Load_IgnoresEmptyEntriesInJson()
    {
        // Direkt JSON mit leerem String drin schreiben
        File.WriteAllText(_statePath, """{"version":1,"entryIds":["VALID","","   "]}""");
        var store = new OutlookEntryStore(_statePath, _logger);
        store.Load();
        Assert.Equal(1, store.Count);
        Assert.True(store.IsSeen("VALID"));
    }

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OutlookEntryStore(null!, _logger));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OutlookEntryStore(_statePath, null!));
    }
}