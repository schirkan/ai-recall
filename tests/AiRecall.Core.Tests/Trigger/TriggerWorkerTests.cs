using System.Threading.Channels;
using AiRecall.Core.Configuration;
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
}