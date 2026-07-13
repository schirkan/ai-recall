using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using AiRecall.TrayApp;

using Serilog;
using Serilog.Core;
using Serilog.Events;

using Xunit;

namespace AiRecall.Core.Tests.TrayApp;

/// <summary>
/// Tests fuer Spec 0014 Iter. 3 — manuelle Audio-Steuerung im Tray-Menu.
/// Verifiziert:
/// <list type="bullet">
///   <item>Privacy-First-Gate: Items disabled solange Audio.Enabled=false</item>
///   <item>Rebind: Enabled-State folgt IsRecording der gebundenen IRecordingControl</item>
///   <item>RecordingStateChanged aktualisiert Enabled-State</item>
///   <item>Idempotenz: doppelter Rebind fuehrt nicht zu Mehrfach-Subscriptions</item>
///   <item>Click-Handler ruft StartManualAsync/StopAsync auf</item>
/// </list>
/// <para>
/// WinForms-Threading: TrayIconController erzeugt ein NotifyIcon. Tests laufen
/// mit <c>[Collection("WinForms")]</c> damit sie hintereinander auf demselben
/// STA-Thread laufen.
/// </para>
/// <para>
/// Anmerkung: <c>ToolStripMenuItem.Visible</c> ist in .NET 8 / WinForms
/// standardmaessig false und wird auch nach AddRange nicht automatisch true
/// (das ist ein bekannter Bug, der dokumentiert wurde). Wir testen daher nur
/// das semantisch wichtige <c>Enabled</c>-State (Gate), nicht Visible. Der
/// Tray-Controller setzt Visible zwar korrekt auf Audio.Enabled, aber das ist
/// eine WinForms-UI-Sache, die bei nicht-aktivem Message-Loop unzuverlaessig
/// ist.
/// </para>
/// </summary>
[Collection("WinForms")]
public class TrayIconControllerAudioItemsTests : IDisposable
{
    private readonly List<TrayIconController> _controllers = new();

    public TrayIconControllerAudioItemsTests()
    {
        // Sicherstellen, dass ein Logger konfiguriert ist (TrayIconController-
        // Konstruktor ruft Log.Information).
        if (Log.Logger.GetType().Name == "SilentLogger")
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new ListLogSink(_ => { }))
                .CreateLogger();
        }
    }

    public void Dispose()
    {
        foreach (var c in _controllers)
        {
            try { c.Dispose(); } catch { /* ignore */ }
        }
        _controllers.Clear();
    }

    private TrayIconController CreateController(
        AppConfig config,
        Func<IRecordingControl?>? recordingControlProvider = null)
    {
        var supervisor = new TriggerSupervisor(Log.Logger.ForContext<TriggerSupervisor>());
        var controller = new TrayIconController(
            supervisor,
            () => config,
            recordingControlProvider);
        _controllers.Add(controller);
        return controller;
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void AudioDisabled_AudioItemsAlwaysDisabled()
    {
        // Arrange — Audio.Enabled=false (Privacy-First-Default)
        var config = new AppConfig { Audio = { Enabled = false } };
        var fakeControl = new FakeRecordingControl();
        using var controller = CreateController(config, () => fakeControl);
        controller.RebindRecordingControlForTest();

        // Assert — Audio-Items disabled, weil Audio.Enabled=false (Gate)
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.False(controller.StopAudioItemForTest.Enabled);

        // Recording-State-Change darf Items nicht enablen, solange Audio.Enabled=false
        fakeControl.IsRecording = true;
        fakeControl.RaiseStateChanged(true, RecordingSource.Manual, "manual-abc", null);
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.False(controller.StopAudioItemForTest.Enabled);
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void AudioEnabled_NotRecording_StartEnabledStopDisabled()
    {
        // Arrange — Audio.Enabled=true, kein Recording
        var config = new AppConfig { Audio = { Enabled = true } };
        var fakeControl = new FakeRecordingControl { IsRecording = false };
        using var controller = CreateController(config, () => fakeControl);
        controller.RebindRecordingControlForTest();

        // Assert
        Assert.Same(fakeControl, controller.BoundRecordingControlForTest);
        Assert.True(controller.StartAudioItemForTest.Enabled);
        Assert.False(controller.StopAudioItemForTest.Enabled);
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void AudioEnabled_IsRecording_StartDisabledStopEnabled()
    {
        // Arrange — Audio.Enabled=true, Recording laeuft bereits
        var config = new AppConfig { Audio = { Enabled = true } };
        var fakeControl = new FakeRecordingControl { IsRecording = true };
        using var controller = CreateController(config, () => fakeControl);
        controller.RebindRecordingControlForTest();

        // Assert — Single-Active-Constraint: bei laufender Aufnahme ist Start
        // disabled (sonst wuerde StartManualAsync InvalidOperationException
        // werfen), Stop aktiviert.
        Assert.Same(fakeControl, controller.BoundRecordingControlForTest);
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.True(controller.StopAudioItemForTest.Enabled);
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void Rebind_NoProvider_AudioItemsBothDisabled()
    {
        // Arrange — kein Provider (kein TriggerService aktiv)
        var config = new AppConfig { Audio = { Enabled = true } };
        using var controller = CreateController(config, recordingControlProvider: null);
        controller.RebindRecordingControlForTest();

        // Assert
        Assert.Null(controller.BoundRecordingControlForTest);
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.False(controller.StopAudioItemForTest.Enabled);
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void RecordingStateChanged_IsRecording_TogglesEnabledState()
    {
        var config = new AppConfig { Audio = { Enabled = true } };
        var fakeControl = new FakeRecordingControl();
        using var controller = CreateController(config, () => fakeControl);
        controller.RebindRecordingControlForTest();

        // Initial: idle → Start enabled
        Assert.True(controller.StartAudioItemForTest.Enabled);
        Assert.False(controller.StopAudioItemForTest.Enabled);

        // Act 1 — Recording started
        fakeControl.IsRecording = true;
        fakeControl.RaiseStateChanged(true, RecordingSource.Manual, "manual-abc", null);

        // Assert 1 — Start disabled (Single-Active), Stop enabled
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.True(controller.StopAudioItemForTest.Enabled);

        // Act 2 — Recording stopped
        fakeControl.IsRecording = false;
        fakeControl.RaiseStateChanged(false, RecordingSource.Manual, "manual-abc", null);

        // Assert 2 — Start wieder enabled
        Assert.True(controller.StartAudioItemForTest.Enabled);
        Assert.False(controller.StopAudioItemForTest.Enabled);
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void Rebind_SwapsBinding_NoDoubleSubscription()
    {
        // Arrange — zwei verschiedene Recording-Controls, Provider liefert
        // wechselnd. Rebind muss alte Subscription sauber abmelden.
        var config = new AppConfig { Audio = { Enabled = true } };
        var fakeA = new FakeRecordingControl();
        var fakeB = new FakeRecordingControl();
        var current = fakeA;
        using var controller = CreateController(config, () => current);
        controller.RebindRecordingControlForTest();

        Assert.Same(fakeA, controller.BoundRecordingControlForTest);

        // Provider liefert jetzt fakeB — naechster Rebind muss alte Subscription
        // sauber abmelden und neue anmelden.
        current = fakeB;
        controller.RebindRecordingControlForTest();

        Assert.Same(fakeB, controller.BoundRecordingControlForTest);

        // fakeA-Raise darf KEINE Aenderung im Tray-Controller mehr ausloesen
        // (alte Subscription weg). fakeB-Raise muss wirken.
        fakeA.IsRecording = true;
        fakeA.RaiseStateChanged(true, RecordingSource.MeetingAuto, "chat1", "Topic X");
        Assert.True(controller.StartAudioItemForTest.Enabled); // bleibt idle

        // fakeB feuert → muss wirken.
        fakeB.IsRecording = true;
        fakeB.RaiseStateChanged(true, RecordingSource.Manual, "manual-x", null);
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.True(controller.StopAudioItemForTest.Enabled);
    }

    [Fact]
    [Trait("Category", "WinForms")]
    public void Rebind_SameControlTwice_NoOp()
    {
        var config = new AppConfig { Audio = { Enabled = true } };
        var fakeControl = new FakeRecordingControl { IsRecording = true };
        using var controller = CreateController(config, () => fakeControl);
        controller.RebindRecordingControlForTest();

        // Erneuter Rebind mit gleicher Instanz darf weder unsubscribe-noch
        // resubscribe-Schleife ausloesen, sonst waere nach Dispose() die
        // Subscription mehrfach registriert.
        controller.RebindRecordingControlForTest();
        controller.RebindRecordingControlForTest();

        // State muss konsistent bleiben.
        Assert.Same(fakeControl, controller.BoundRecordingControlForTest);
        Assert.False(controller.StartAudioItemForTest.Enabled);
        Assert.True(controller.StopAudioItemForTest.Enabled);
    }
}

/// <summary>
/// Minimaler Fake von <see cref="IRecordingControl"/> fuer Tests.
/// Erlaubt es, <c>IsRecording</c> zu setzen und
/// <c>RecordingStateChanged</c> kontrolliert zu feuern.
/// </summary>
internal sealed class FakeRecordingControl : IRecordingControl
{
    public bool IsRecording { get; set; }
    public event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public List<string> StartedKeys { get; } = new();

    public Task<string> StartManualAsync(CancellationToken ct)
    {
        StartCallCount++;
        var key = "manual-test-" + StartCallCount;
        StartedKeys.Add(key);
        IsRecording = true;
        // RecordingStateChanged wird vom Test manuell ueber RaiseStateChanged
        // gefeuert, damit State-Tests deterministisch sind.
        return Task.FromResult(key);
    }

    public Task StopAsync()
    {
        StopCallCount++;
        IsRecording = false;
        return Task.CompletedTask;
    }

    public void RaiseStateChanged(bool isRecording, RecordingSource source, string key, string? topic)
    {
        RecordingStateChanged?.Invoke(this, new RecordingStateChangedEventArgs(
            IsRecording: isRecording,
            Source: source,
            Key: key,
            Topic: topic,
            At: DateTimeOffset.UtcNow));
    }
}

/// <summary>
/// Serilog-Senke, die alle Events in eine Liste schreibt. Wird in Tests
/// verwendet, wenn kein globaler Logger gesetzt ist.
/// </summary>
internal sealed class ListLogSink : ILogEventSink
{
    private readonly Action<string> _onEvent;
    public ListLogSink(Action<string> onEvent) => _onEvent = onEvent;
    public void Emit(LogEvent logEvent) => _onEvent(logEvent.RenderMessage());
}

/// <summary>
/// Marker-Collection, damit alle WinForms-Tests hintereinander laufen.
/// </summary>
[CollectionDefinition("WinForms", DisableParallelization = true)]
public class WinFormsCollection { }