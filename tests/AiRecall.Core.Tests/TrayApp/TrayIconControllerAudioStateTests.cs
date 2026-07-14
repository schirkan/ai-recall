using AiRecall.TrayApp;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.TrayApp;

/// <summary>
/// Spec 0014 Iter. 2 — Tray-Icon-Prioritaet (Audio > Capture > Idle).
///
/// Diese Tests pruefen die Pure-Function-Variante
/// <c>TrayIconController.ResolveTrayIconKey(TriggerState, bool)</c>, die
/// ohne NotifyIcon-Instanziierung auskommt. Damit kein WinForms-Handle-
/// Leak in Tests (siehe MEMORY.md: Probe-Tests NotifyIcon-Leak) und
/// kein STA-Threading noetig.
///
/// Die echte UI-Aktualisierung laeuft ueber <c>UpdateTrayIcon()</c>, das
/// von <c>OnRecordingStateChanged</c> und <c>OnSupervisorStateChanged</c>
/// aufgerufen wird. Diese Tests validieren die reine Key-Berechnung; die
/// Wiring-Tests (Subscription + Aufruf) sind in
/// <c>TrayIconControllerAudioItemsTests</c> abgedeckt.
/// </summary>
public class TrayIconControllerAudioStateTests
{
    // --- Idle (kein Audio, kein Capture) ---

    [Fact]
    public void Key_NoAudio_NoCapture_Stopped_ReturnsIdleIcon()
    {
        var key = TrayIconController.ResolveTrayIconKey(TriggerState.Stopped, isAudioRecording: false);
        Assert.Equal("tray-idle.ico", key);
    }

    [Fact]
    public void Key_NoAudio_NoCapture_Crashed_ReturnsIdleIcon()
    {
        // Crashed ist bewusst Idle, weil keine Aufnahme laeuft (Spec 0014 Iter. 2).
        var key = TrayIconController.ResolveTrayIconKey(TriggerState.Crashed, isAudioRecording: false);
        Assert.Equal("tray-idle.ico", key);
    }

    // --- Capture laeuft, kein Audio ---

    [Fact]
    public void Key_NoAudio_CaptureRunning_ReturnsCaptureIcon()
    {
        var key = TrayIconController.ResolveTrayIconKey(TriggerState.Running, isAudioRecording: false);
        Assert.Equal("tray-recording.ico", key);
    }

    // --- Audio nimmt Prioritaet ein (Spec 0014 Iter. 2 Hauptzweck) ---

    [Fact]
    public void Key_AudioRecording_CaptureStopped_ReturnsAudioIcon()
    {
        var key = TrayIconController.ResolveTrayIconKey(TriggerState.Stopped, isAudioRecording: true);
        Assert.Equal("tray-audio-recording.ico", key);
    }

    [Fact]
    public void Key_AudioRecording_CaptureRunning_AudioWins()
    {
        // Kern-Invariante: Audio hat IMMER Vorrang vor Capture, auch wenn
        // Supervisor.State==Running. Theoretisch kann das vorkommen, wenn
        // MeetingTrigger parallel eine Auto-Aufnahme startet waehrend
        // manuell aufgenommen wird — dann zeigt das Icon "Audio laeuft",
        // weil der User das selbst ausgeloest hat.
        var key = TrayIconController.ResolveTrayIconKey(TriggerState.Running, isAudioRecording: true);
        Assert.Equal("tray-audio-recording.ico", key);
    }

    // --- Bonus: Capture ueberdeckt Audio nicht, wenn Audio aus ist ---

    [Fact]
    public void Key_NoAudio_CaptureStarting_ReturnsIdleIcon()
    {
        // Starting ist ein transientes State — Capture laeuft noch nicht,
        // also kein Recording-Icon. Audio hat hier sowieso keine Chance
        // (IsRecording=false), also Idle.
        var key = TrayIconController.ResolveTrayIconKey(TriggerState.Starting, isAudioRecording: false);
        Assert.Equal("tray-idle.ico", key);
    }
}