using System.Threading.Channels;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Trigger;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AiRecall.Core.Tests.Trigger;

public class TriggerWorkerTests
{
    private static AppConfig MakeConfig() => new AppConfig();

    private static Serilog.ILogger MakeLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Verbose)
            .WriteTo.Sink(new NullSink())
            .CreateLogger();

    private static WindowInfo FakeBrowser => new(new IntPtr(0x24fff), "Example tab", 42,
        "chrome", true, new WindowRect(0, 0, 100, 100));

    [Theory]
    [InlineData(TriggerKind.Periodic, "app")]
    [InlineData(TriggerKind.Periodic, "title")]
    [InlineData(TriggerKind.Foreground, "app")]
    [InlineData(TriggerKind.Foreground, "title")]
    public void ProcessWindow_IgnoredAppOrTitle_DoesNotTakeScreenshot(TriggerKind kind, string filter)
    {
        var config = MakeConfig();
        if (filter == "app") config.ScreenRecorder.IgnoreApps.Add("Chrome");
        else config.ScreenRecorder.IgnoreWindowTitles.Add("example TAB");
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var logger = MakeLogger();
        using var worker = new TriggerWorker(channel.Reader, config, logger, AppReaderRegistry.Empty(logger));
        var window = FakeBrowser;
        var screenshots = 0;

        worker.ProcessWindow(new(window.Handle, kind, DateTimeOffset.Now), window, null,
            _ => { screenshots++; return [1]; }, CancellationToken.None);

        Assert.Equal(0, screenshots);
        Assert.Equal(1, worker.BlacklistCount);
    }

    [Fact]
    public void ProcessWindow_IgnoredBrowserUrl_DoesNotPersistAnyCapture()
    {
        var config = MakeConfig();
        config.Capture.RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        config.ScreenRecorder.IgnoreUrls.Add("private.example");
        var logger = MakeLogger();
        var registry = AppReaderRegistry.FromReaders([new BrowserStub(_ =>
            new("", "Generic title", "url", "Browser stub", "1", new Dictionary<string, string>
            {
                ["url"] = "https://private.example/"
            }))], logger);
        using var worker = new TriggerWorker(Channel.CreateUnbounded<TriggerEvent>().Reader, config, logger, registry);
        var window = FakeBrowser;

        worker.ProcessWindow(new(window.Handle, TriggerKind.Periodic, DateTimeOffset.Now), window, null,
            _ => Guid.NewGuid().ToByteArray(), CancellationToken.None);

        Assert.False(Directory.Exists(config.Capture.RootPath));
        Assert.Equal(1, worker.BlacklistCount);
        Assert.Equal(0, worker.CaptureCount);
    }

    [Fact]
    public void ProcessWindow_CancelledWhileReaderIsRunning_DoesNotPersistAnyCapture()
    {
        var config = MakeConfig();
        config.Capture.RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource();
        var logger = MakeLogger();
        var observedToken = CancellationToken.None;
        var registry = AppReaderRegistry.FromReaders([new BrowserStub(context =>
        {
            observedToken = context.CancellationToken;
            cts.Cancel();
            return null;
        })], logger);
        using var worker = new TriggerWorker(Channel.CreateUnbounded<TriggerEvent>().Reader, config, logger, registry);
        var window = FakeBrowser;

        Assert.Throws<OperationCanceledException>(() => worker.ProcessWindow(
            new(window.Handle, TriggerKind.Foreground, DateTimeOffset.Now), window, null,
            _ => Guid.NewGuid().ToByteArray(), cts.Token));
        Assert.Equal(cts.Token, observedToken);
        Assert.False(Directory.Exists(config.Capture.RootPath));
        Assert.Equal(0, worker.CaptureCount);
    }

    private sealed class BrowserStub(Func<AppReaderContext, AppReaderResult?> read) : IAppReader
    {
        public IReadOnlyCollection<string> SupportedProcesses => ["chrome"];
        public string DisplayName => "Browser stub";
        public bool CanRead(WindowInfo window) => window.ProcessName == "chrome";
        public AppReaderResult? Read(WindowInfo window, AppReaderContext context) => read(context);
    }

    private sealed class NullSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent) { /* drop */ }
    }

    // -----------------------------------------------------------------------
    // Lifecycle / Smoke
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_StoresConfig()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        Assert.Equal(0, worker.CaptureCount);
        Assert.Equal(0, worker.SkippedCount);
        Assert.Equal(0, worker.ThrottleCount);
        Assert.Equal(0, worker.DuplicateCount);
        Assert.Equal(0, worker.BlacklistCount);
        Assert.Equal(0, worker.SelfCaptureCount);
        Assert.Equal(0, worker.ErrorCount);
    }

    [Fact]
    public void StartStop_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        worker.Start();
        Thread.Sleep(50);
        worker.Stop();
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        worker.Start();
        worker.Start(); // darf nicht werfen
        Thread.Sleep(50);
        worker.Stop();
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        worker.Stop();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        worker.Dispose();
        worker.Dispose();
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => worker.Start());
    }

    // -----------------------------------------------------------------------
    // ProcessEvent: HWND-Filter (Win32-Aufrufe ohne echtes Desktop)
    // -----------------------------------------------------------------------

    [Fact]
    public void ProcessEvent_HwndZero_SkippedCountIncrements()
    {
        // GetAncestor(IntPtr.Zero, GA_ROOT) = 0 → Schritt 1 greift → skip
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());

        var ev = new TriggerEvent(IntPtr.Zero, TriggerKind.Foreground, DateTimeOffset.Now);
        worker.ProcessEvent(ev);

        Assert.Equal(0, worker.CaptureCount);
        Assert.Equal(1, worker.SkippedCount);
        Assert.Equal(0, worker.SelfCaptureCount);
    }

    [Fact]
    public void ProcessEvent_InvalidHwnd_SkippedOrSelfCapture()
    {
        // HWND 0x1234 ist wahrscheinlich kein gültiges Top-Level-Fenster.
        // GetAncestor wird 0 oder das HWND selbst liefern, WindowInfoLookup
        // gibt null zurück → SkippedCount++. Wenn Zufall PID == eigene PID,
        // SelfCaptureCount++. Beide Pfade sind akzeptabel — wir testen nur,
        // dass KEIN Capture entsteht.
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());

        var ev = new TriggerEvent(new IntPtr(0x1234), TriggerKind.Foreground, DateTimeOffset.Now);
        worker.ProcessEvent(ev);

        Assert.Equal(0, worker.CaptureCount);
        Assert.True(worker.SkippedCount + worker.SelfCaptureCount >= 1,
            "Erwartet: mindestens 1 in SkippedCount ODER SelfCaptureCount (kein Capture)");
    }

    // -----------------------------------------------------------------------
    // MatchesAny (Blacklist-Helper, pure Function)
    // -----------------------------------------------------------------------

    [Fact]
    public void MatchesAny_EmptyPatterns_ReturnsFalse()
    {
        Assert.False(TriggerWorker.MatchesAny("notepad", Array.Empty<string>()));
        Assert.False(TriggerWorker.MatchesAny("notepad", new List<string>()));
    }

    [Fact]
    public void MatchesAny_EmptyValue_ReturnsFalse()
    {
        Assert.False(TriggerWorker.MatchesAny("", new[] { "notepad" }));
        Assert.False(TriggerWorker.MatchesAny(null!, new[] { "notepad" }));
    }

    [Fact]
    public void MatchesAny_SubstringMatch()
    {
        Assert.True(TriggerWorker.MatchesAny("notepad-class", new[] { "notepad" }));
        Assert.True(TriggerWorker.MatchesAny("MyNotepadWindow", new[] { "notepad" }));
        Assert.False(TriggerWorker.MatchesAny("chrome", new[] { "notepad" }));
    }

    [Fact]
    public void MatchesAny_CaseInsensitive()
    {
        Assert.True(TriggerWorker.MatchesAny("Notepad", new[] { "notepad" }));
        Assert.True(TriggerWorker.MatchesAny("NOTEPAD", new[] { "notepad" }));
        Assert.True(TriggerWorker.MatchesAny("notepad", new[] { "NOTEPAD" }));
    }

    [Fact]
    public void MatchesAny_SkipsEmptyPatterns()
    {
        // Leere Patterns in der Liste sollen ignoriert werden
        Assert.True(TriggerWorker.MatchesAny("notepad", new[] { "", "  ", "notepad" }));
        Assert.False(TriggerWorker.MatchesAny("chrome", new[] { "", "  " }));
    }

    [Fact]
    public void MatchesAny_FirstMatchWins()
    {
        var patterns = new[] { "first", "second" };
        Assert.True(TriggerWorker.MatchesAny("first-window", patterns));
        Assert.True(TriggerWorker.MatchesAny("second-window", patterns));
        Assert.False(TriggerWorker.MatchesAny("third-window", patterns));
    }

    // -----------------------------------------------------------------------
    // Channel-Integration: Event → ProcessEvent-Pfad
    // -----------------------------------------------------------------------

    [Fact]
    public void Channel_HwndZeroEvent_ProcessedAndSkipped()
    {
        // Schreibt einen HWND=0-TriggerEvent in den Channel; Worker liest
        // und verarbeitet ihn. Verifiziert, dass die Channel-Loop läuft
        // und Events korrekt durch ProcessEvent gehen.
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var worker = new TriggerWorker(channel.Reader, MakeConfig(), MakeLogger());
        worker.Start();

        channel.Writer.TryWrite(new TriggerEvent(IntPtr.Zero, TriggerKind.Foreground, DateTimeOffset.Now));

        // Auf Worker-Loop warten
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (worker.SkippedCount == 0 && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(1, worker.SkippedCount);
        worker.Stop();
    }

    // -----------------------------------------------------------------------
    // Bug-Bash 2026-07-05: Title-Blacklist für TrayApp-Fenster
    // (SettingsDialog, LogviewerWindow) — die Defaults müssen verhindern,
    // dass der Trigger die eigene UI capturt.
    // -----------------------------------------------------------------------

    [Fact]
    public void TriggerBlacklist_Default_WindowTitles_ContainsTrayAppPrefix()
    {
        var config = new AppConfig();
        Assert.Contains("AiRecall - ", config.Trigger.Blacklist.WindowTitles);
    }

    [Fact]
    public void TriggerBlacklist_Default_WindowTitles_MatchesTrayAppWindows()
    {
        // Substring-Match (case-insensitive) wie Processes / WindowClasses.
        // Beide TrayApp-Fenster müssen von der Default-Blacklist erfasst werden.
        var config = new AppConfig();
        Assert.True(TriggerWorker.MatchesAny("AiRecall - Settings", config.Trigger.Blacklist.WindowTitles));
        Assert.True(TriggerWorker.MatchesAny("AiRecall - Live Logviewer", config.Trigger.Blacklist.WindowTitles));
    }

    [Fact]
    public void TriggerBlacklist_Default_WindowTitles_DoesNotMatchForeignWindows()
    {
        // False-Positive-Check: ein normaler WindowTitle darf nicht versehentlich
        // auf den "AiRecall - "-Prefix matchen.
        var config = new AppConfig();
        Assert.False(TriggerWorker.MatchesAny("Microsoft Word - Document1.docx", config.Trigger.Blacklist.WindowTitles));
        Assert.False(TriggerWorker.MatchesAny("Chrome", config.Trigger.Blacklist.WindowTitles));
        Assert.False(TriggerWorker.MatchesAny("", config.Trigger.Blacklist.WindowTitles));
    }
}