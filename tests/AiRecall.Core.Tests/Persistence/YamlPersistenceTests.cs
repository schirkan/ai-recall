using System.Text.Json;
using AiRecall.Conversion;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using AiRecall.Transcription;

namespace AiRecall.Core.Tests.Persistence;

public sealed class YamlPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-yaml-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, "\"\"")]
    [InlineData("", "\"\"")]
    [InlineData("A\r\n---\nB\rC\t\"D\"\\E\u0000\u2028F", "\"A\\r\\n---\\nB\\rC\\t\\\"D\\\"\\\\E\\u0000\\u2028F\"")]
    public void QuotedScalar_UsesSingleLineAndRoundTripsText(string? value, string expected)
    {
        var actual = YamlScalar.Quote(value);
        Assert.Equal(expected, actual);
        // These YAML escape sequences use the same syntax as double-quoted JSON.
        Assert.Equal(value ?? "", JsonSerializer.Deserialize<string>(actual));
    }

    [Fact]
    public void PendingCapture_RoundTripsMultilineFrontmatterAndWindowsPath()
    {
        var window = new WindowInfo((IntPtr)123, "Sample\r\n---\nstatus: injected", 42, "sample", true,
            new WindowRect(0, 0, 100, 100));
        var capture = CaptureWriter.WritePending(window, new byte[] { 0x01 }, "hash", _directory,
            filePath: @"C:\temp\notes.txt", uiaContent: "First\r\n---\nconversion: injected\rLast");

        var markdown = File.ReadAllText(capture.MarkdownPath);
        var fields = ConversionWorker.ParseFrontmatter(markdown.ReplaceLineEndings("\n"));
        Assert.Equal("pending", fields["conversion"]);
        Assert.Equal(@"C:\temp\notes.txt", fields["filePath"]);
        Assert.Equal("First\r\n---\nconversion: injected\rLast", fields["uiaContent"]);
        Assert.Contains("title: \"Sample\\r\\n---\\nstatus: injected\"", markdown);
        Assert.False(fields.ContainsKey("status"));

        CaptureWriter.UpdateConversionStatus(capture.MarkdownPath, "error", conversionError: "First\n---\rLast");
        var updated = File.ReadAllText(capture.MarkdownPath);
        Assert.Contains("conversionError: \"First\\n---\\rLast\"", updated);
        Assert.Contains("## Content", updated);
    }

    [Fact]
    public async Task TranscriptionError_EscapesMultilineValueWithoutChangingMarkdownBody()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "meta.md");
        const string body = "transcript_error: this is body text\r\n";
        File.WriteAllText(path, "---\r\ntranscript_status: pending\r\n---\r\n" + body);

        await new MetadataUpdater().MarkFailedAsync(path, "First: \"diagnostic\"\r\n---\nLast", CancellationToken.None);

        var markdown = File.ReadAllText(path);
        Assert.Equal("---\r\ntranscript_status: failed\r\ntranscript_error: \"First: \\\"diagnostic\\\"\\r\\n---\\nLast\"\r\n---\r\n" + body,
            markdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
