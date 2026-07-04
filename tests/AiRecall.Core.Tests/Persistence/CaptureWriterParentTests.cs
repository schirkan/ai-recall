using System.Text;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;

namespace AiRecall.Core.Tests.Persistence;

/// <summary>
/// Tests fuer CaptureWriter.Write mit parentWindow (Spec 0005
/// §Modale Dialoge, Option (a) — Frontmatter-Metadaten).
/// </summary>
public class CaptureWriterParentTests : IDisposable
{
    private readonly string _tempRoot;

    public CaptureWriterParentTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ai-recall-parent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private static WindowInfo MakeWindow(IntPtr hwnd, string title, string process, int pid) =>
        new WindowInfo(hwnd, title, pid, process, true, new WindowRect(0, 0, 100, 100));

    [Fact]
    public void Write_WithoutParentWindow_OmitsParentFields()
    {
        var window = MakeWindow(new IntPtr(0x123), "Modal Dialog", "Outlook", 100);
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var item = CaptureWriter.Write(window, bytes, "content", "hash", _tempRoot);

        var md = File.ReadAllText(item.MarkdownPath);
        Assert.DoesNotContain("parentHwnd:", md);
        Assert.DoesNotContain("parentTitle:", md);
        Assert.DoesNotContain("parentProcess:", md);
    }

    [Fact]
    public void Write_WithParentWindow_AddsParentFields()
    {
        var window   = MakeWindow(new IntPtr(0xABC), "New Message", "Outlook", 100);
        var parent   = MakeWindow(new IntPtr(0xDEF), "Outlook", "Outlook", 100);
        var bytes    = new byte[] { 0xCA, 0xFE };

        var item = CaptureWriter.Write(window, bytes, "Hello", "hash123", _tempRoot, parentWindow: parent);

        var md = File.ReadAllText(item.MarkdownPath);
        Assert.Contains("parentHwnd: 0xDEF", md);
        Assert.Contains("parentTitle: \"Outlook\"", md);
        Assert.Contains("parentProcess: \"Outlook\"", md);
    }

    [Fact]
    public void Write_WithParentWindow_AddsBodyLine()
    {
        var window   = MakeWindow(new IntPtr(0xABC), "Dialog Title", "MyApp", 200);
        var parent   = MakeWindow(new IntPtr(0xDEF), "Main App Window", "MyApp", 200);
        var bytes    = new byte[] { 0x01 };

        var item = CaptureWriter.Write(window, bytes, "x", "h", _tempRoot, parentWindow: parent);

        var md = File.ReadAllText(item.MarkdownPath);
        Assert.Contains("**Parent window:** `MyApp` — Main App Window", md);
    }

    [Fact]
    public void Write_WithNullParent_OmitsBodyLine()
    {
        var window = MakeWindow(new IntPtr(0x123), "T", "P", 1);
        var item = CaptureWriter.Write(window, new byte[] { 0 }, "x", "h", _tempRoot, parentWindow: null);
        var md = File.ReadAllText(item.MarkdownPath);
        Assert.DoesNotContain("Parent window:", md);
    }

    [Fact]
    public void Write_WithDifferentParentProcess_RecordsCorrectly()
    {
        // Sonderfall: Dialog aus einem Helper-Prozess, Parent ist das Hauptapp-Fenster
        var window   = MakeWindow(new IntPtr(0x111), "Print Dialog", "HelperProc", 300);
        var parent   = MakeWindow(new IntPtr(0x222), "Main App", "MainApp", 100);
        var bytes    = new byte[] { 0x02 };

        var item = CaptureWriter.Write(window, bytes, "x", "h", _tempRoot, parentWindow: parent);

        var md = File.ReadAllText(item.MarkdownPath);
        Assert.Contains("parentProcess: \"MainApp\"", md);
        Assert.Contains("parentTitle: \"Main App\"", md);
    }
}