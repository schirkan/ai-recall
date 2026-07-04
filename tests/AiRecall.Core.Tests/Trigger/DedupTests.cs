using AiRecall.Core.Util;

namespace AiRecall.Core.Tests.Trigger;

public class DedupTests
{
    [Fact]
    public void NewProcess_NotDuplicate()
    {
        var d = new Dedup(stateFile: null);
        Assert.False(d.IsDuplicate("chrome", "abc", DateTimeOffset.Now));
    }

    [Fact]
    public void SameHash_AfterMark_IsDuplicate()
    {
        var d = new Dedup(stateFile: null);
        var now = DateTimeOffset.Now;
        d.Mark("chrome", "abc123", now);
        Assert.True(d.IsDuplicate("chrome", "abc123", now.AddSeconds(1)));
    }

    [Fact]
    public void DifferentHash_NotDuplicate()
    {
        var d = new Dedup(stateFile: null);
        var now = DateTimeOffset.Now;
        d.Mark("chrome", "abc", now);
        Assert.False(d.IsDuplicate("chrome", "xyz", now.AddSeconds(1)));
    }

    [Fact]
    public void DifferentProcesses_TrackedSeparately()
    {
        var d = new Dedup(stateFile: null);
        var now = DateTimeOffset.Now;
        d.Mark("chrome", "abc", now);
        Assert.False(d.IsDuplicate("notepad", "abc", now));
    }

    [Fact]
    public void ProcessKey_IsCaseInsensitive()
    {
        var d = new Dedup(stateFile: null);
        var now = DateTimeOffset.Now;
        d.Mark("Chrome", "abc", now);
        Assert.True(d.IsDuplicate("CHROME", "abc", now));
    }

    [Fact]
    public void StatePersists_AcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai-recall-dedup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var now = DateTimeOffset.Now;
            var d1 = new Dedup(path);
            d1.Mark("chrome", "hash1", now);
            d1.Save();

            var d2 = new Dedup(path);
            Assert.True(d2.IsDuplicate("chrome", "hash1", now.AddSeconds(1)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CorruptStateFile_IgnoredReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai-recall-dedup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{this is not valid json");
            var d = new Dedup(path);
            Assert.False(d.IsDuplicate("chrome", "abc", DateTimeOffset.Now));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var d = new Dedup(stateFile: null);
        var now = DateTimeOffset.Now;
        d.Mark("chrome", "abc", now);
        d.Reset();
        Assert.False(d.IsDuplicate("chrome", "abc", now));
    }
}