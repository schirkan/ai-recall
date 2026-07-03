using AiRecall.AppReader.Base;

namespace AiRecall.Core.Tests.AppReaders;

public class AppReaderResultTests
{
    [Fact]
    public void Constructor_StoresAllProperties()
    {
        var extra = new Dictionary<string, string> { ["EntryID"] = "abc" };
        var r = new AppReaderResult("md", "url", "url", "reader", "1.0", extra);
        Assert.Equal("md", r.ContentMarkdown);
        Assert.Equal("url", r.ContextLabel);
        Assert.Equal("url", r.ContextKind);
        Assert.Equal("reader", r.ReaderName);
        Assert.Equal("1.0", r.ReaderVersion);
        Assert.Same(extra, r.Extra);
    }

    [Fact]
    public void Extra_DefaultsToNull()
    {
        var r = new AppReaderResult("md", null, null, "r", "1.0");
        Assert.Null(r.Extra);
    }
}