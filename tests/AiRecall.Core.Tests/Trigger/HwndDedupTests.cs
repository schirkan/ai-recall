using AiRecall.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public class HwndDedupTests : IDisposable
{
    private readonly string _tempDir;

    public HwndDedupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ai-recall-hwnd-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void NewHwnd_NotDuplicate()
    {
        var d = new HwndDedup(stateFile: null);
        Assert.False(d.IsDuplicate(new IntPtr(0x1234), "abc", DateTimeOffset.Now));
    }

    [Fact]
    public void SameHash_AfterMark_IsDuplicate()
    {
        var d = new HwndDedup(stateFile: null);
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0xABCD);
        d.Mark(hwnd, "abc123", now);
        Assert.True(d.IsDuplicate(hwnd, "abc123", now.AddSeconds(1)));
    }

    [Fact]
    public void DifferentHash_NotDuplicate()
    {
        var d = new HwndDedup(stateFile: null);
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0xABCD);
        d.Mark(hwnd, "abc", now);
        Assert.False(d.IsDuplicate(hwnd, "xyz", now.AddSeconds(1)));
    }

    [Fact]
    public void DifferentHwnds_TrackedSeparately()
    {
        // Spec 0005 Akzeptanzkriterium: zwei HWNDs derselben App deduplizieren unabhängig.
        var d = new HwndDedup(stateFile: null);
        var now = DateTimeOffset.Now;
        var hwndA = new IntPtr(0xAAAA);
        var hwndB = new IntPtr(0xBBBB);
        d.Mark(hwndA, "abc", now);
        Assert.False(d.IsDuplicate(hwndB, "abc", now));
    }

    [Fact]
    public void SameValue_DifferentHwndInstances_Distinct()
    {
        // IntPtr ist Wert-Typ — IntPtr(0x1234) und IntPtr(0x1234) sollten
        // denselben Key ergeben. Hier gegenprüfen, dass das Dictionary
        // sie als identisch behandelt (Backward-Compat-Test).
        var d = new HwndDedup(stateFile: null);
        var now = DateTimeOffset.Now;
        var key1 = new IntPtr(0xCAFE);
        var key2 = new IntPtr(0xCAFE);
        d.Mark(key1, "abc", now);
        Assert.True(d.IsDuplicate(key2, "abc", now));
    }

    [Fact]
    public void StatePersists_AcrossInstances()
    {
        var path = Path.Combine(_tempDir, "hwnd-dedup.json");
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0xDEAD);
        var d1 = new HwndDedup(path);
        d1.Mark(hwnd, "hash1", now);
        d1.Save();

        var d2 = new HwndDedup(path);
        Assert.True(d2.IsDuplicate(hwnd, "hash1", now.AddSeconds(1)));
    }

    [Fact]
    public void CorruptStateFile_IgnoredReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "hwnd-dedup.json");
        File.WriteAllText(path, "{this is not valid json");
        var d = new HwndDedup(path);
        Assert.False(d.IsDuplicate(new IntPtr(0x1234), "abc", DateTimeOffset.Now));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var d = new HwndDedup(stateFile: null);
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0x1234);
        d.Mark(hwnd, "abc", now);
        d.Reset();
        Assert.False(d.IsDuplicate(hwnd, "abc", now));
    }

    [Fact]
    public void TrackedHwndCount_ReflectsState()
    {
        var d = new HwndDedup(stateFile: null);
        Assert.Equal(0, d.TrackedHwndCount);
        d.Mark(new IntPtr(0x1), "a", DateTimeOffset.Now);
        d.Mark(new IntPtr(0x2), "b", DateTimeOffset.Now);
        Assert.Equal(2, d.TrackedHwndCount);
    }

    [Theory]
    [InlineData(0x0, "0x0")]
    [InlineData(0x1234, "0x1234")]
    [InlineData(0xDEADBEEF, "0xDEADBEEF")]
    [InlineData(unchecked((int)0xFFFFFFFF), "0xFFFFFFFFFFFFFFFF")] // ToInt64() => 0xFFFFFFFFFFFFFFFF (high bit set)
    public void FormatHwnd_Roundtrips(long value, string expected)
    {
        var hwnd = new IntPtr(value);
        var formatted = HwndDedup.FormatHwnd(hwnd);
        Assert.Equal(expected, formatted);
        Assert.True(HwndDedup.TryParseHwnd(formatted, out var parsed));
        Assert.Equal(hwnd, parsed);
    }

    [Theory]
    [InlineData("0xDEAD")]
    [InlineData("DEAD")]
    [InlineData("0xdead")]
    [InlineData("dead")]
    public void TryParseHwnd_AcceptsHexFormats(string input)
    {
        Assert.True(HwndDedup.TryParseHwnd(input, out var hwnd));
        Assert.Equal(0xDEADL, hwnd.ToInt64());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0xZZZZ")]
    public void TryParseHwnd_RejectsInvalidInputs(string input)
    {
        Assert.False(HwndDedup.TryParseHwnd(input, out _));
    }
}