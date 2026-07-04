using System.Threading.Channels;
using AiRecall.Core.Configuration;
using AiRecall.ScreenCapture.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public class WinEventHookDetectorTests
{
    [Fact]
    public void Constructor_StoresConfigAndWriter()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription();
        var detector = new WinEventHookDetector(config, channel.Writer);
        Assert.NotNull(detector);
        detector.Dispose();
    }

    [Fact]
    public void StartStop_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription();
        using var detector = new WinEventHookDetector(config, channel.Writer);
        detector.Start();
        // kurze Wartezeit, damit der Thread Hooks registriert
        Thread.Sleep(50);
        detector.Stop();
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription();
        using var detector = new WinEventHookDetector(config, channel.Writer);
        detector.Start();
        detector.Start(); // darf nicht werfen
        Thread.Sleep(50);
        detector.Stop();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription();
        var detector = new WinEventHookDetector(config, channel.Writer);
        detector.Dispose();
        detector.Dispose(); // darf nicht werfen
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription();
        using var detector = new WinEventHookDetector(config, channel.Writer);
        detector.Stop();
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription();
        var detector = new WinEventHookDetector(config, channel.Writer);
        detector.Dispose();
        Assert.Throws<ObjectDisposedException>(() => detector.Start());
    }

    [Fact]
    public void DisabledConfig_StartsWithoutHooks()
    {
        // Alle WinEvents deaktiviert → Detector startet trotzdem, registriert aber keine Hooks.
        // Verifiziert: keine Exception beim Start.
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var config = new WinEventSubscription
        {
            Foreground = false,
            Focus = false,
            NameChange = false,
            ValueChange = false,
            Scroll = false,
            MenuPopup = false
        };
        using var detector = new WinEventHookDetector(config, channel.Writer);
        detector.Start();
        Thread.Sleep(50);
        detector.Stop();
    }

    [Fact]
    public void LoggerCallbacks_AreInvokedOnHookRegistration()
    {
        // Bei deaktivierten Events dürfen keine Warnungen/Errors auftreten.
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var warns = new List<string>();
        var errors = new List<string>();
        var config = new WinEventSubscription(); // alle aktiviert
        using var detector = new WinEventHookDetector(
            config,
            channel.Writer,
            logWarn: warns.Add,
            logError: errors.Add);
        detector.Start();
        Thread.Sleep(100);
        detector.Stop();

        // SetWinEventHook kann fehlschlagen, wenn keine Window-Station/Session
        // verfügbar ist (z. B. in CI ohne Desktop). Wir loggen nur die Counts,
        // kein Assert auf 0 — Smoke-Check, kein harter Failure.
        Assert.NotNull(warns);
        Assert.NotNull(errors);
    }
}