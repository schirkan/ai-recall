using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;

namespace AiRecall.TrayApp;

/// <summary>
/// Verwaltet das NotifyIcon + ContextMenuStrip im System-Tray und verdrahtet
/// die Aktionen (Start/Stop, Logviewer, Settings, Quit) mit dem
/// <see cref="TriggerSupervisor"/>. Status-Subscriptions halten Menu und
/// Tooltip synchron (Spec 0006 Schritt 4).
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startRecordingItem;
    private readonly ToolStripMenuItem _stopRecordingItem;
    private readonly ToolStripMenuItem _showLogsItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _quitItem;
    private readonly TriggerSupervisor _supervisor;
    private readonly Func<AppConfig> _configProvider;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler? ShowLogviewerRequested;
    public event EventHandler? ShowSettingsRequested;

    /// <summary>Enables the "Live Logviewer" menu item (call after LogviewerWindow is ready).</summary>
    public void EnableLogviewer() => _showLogsItem.Enabled = true;

    /// <summary>Enables the "Settings" menu item (call after SettingsDialog is ready).</summary>
    public void EnableSettings() => _settingsItem.Enabled = true;

    public TrayIconController(TriggerSupervisor supervisor, Func<AppConfig> configProvider)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));

        _statusItem = new ToolStripMenuItem("🔴 Stopped")
        {
            Enabled = false   // Status-Item ist nicht klickbar
        };
        _startRecordingItem = new ToolStripMenuItem("▶ Start Recording")
        {
            ShortcutKeys = Keys.S
        };
        _stopRecordingItem = new ToolStripMenuItem("⏸ Stop Recording")
        {
            ShortcutKeys = Keys.T,
            Enabled = false
        };
        _showLogsItem = new ToolStripMenuItem("📋 Live Logviewer…")
        {
            ShortcutKeys = Keys.L,
            Enabled = false   // aktiv in Schritt 5 (Spec 0008) — via EnableLogviewer()
        };
        _settingsItem = new ToolStripMenuItem("⚙ Settings…")
        {
            ShortcutKeys = Keys.Oemcomma,
            Enabled = false   // aktiv in Schritt 6 (Spec 0009) — via EnableSettings()
        };
        _quitItem = new ToolStripMenuItem("🚪 Quit")
        {
            ShortcutKeys = Keys.Q
        };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _startRecordingItem,
            _stopRecordingItem,
            new ToolStripSeparator(),
            _showLogsItem,
            _settingsItem,
            new ToolStripSeparator(),
            _quitItem
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AiRecall",
            Visible = true,
            ContextMenuStrip = _menu
        };

        // Supervisor -> UI
        _supervisor.StateChanged += OnSupervisorStateChanged;
        ApplyState(TrayIconState.FromSupervisor(
            _supervisor.State,
            captureCount: 0,
            crashCount: _supervisor.CrashCount));

        // UI -> Supervisor
        _startRecordingItem.Click += (_, _) =>
        {
            Log.Information("Menu: Start Recording clicked");
            try
            {
                _supervisor.Start(_configProvider());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Start failed from menu");
                ShowError("Failed to start recording", ex);
            }
        };
        _stopRecordingItem.Click += (_, _) =>
        {
            Log.Information("Menu: Stop Recording clicked");
            _supervisor.Stop();
        };
        _showLogsItem.Click += (_, _) =>
        {
            Log.Information("Menu: Show Logviewer requested");
            ShowLogviewerRequested?.Invoke(this, EventArgs.Empty);
        };
        _settingsItem.Click += (_, _) =>
        {
            Log.Information("Menu: Show Settings requested");
            ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        };
        _quitItem.Click += (_, _) =>
        {
            Log.Information("Menu: Quit clicked");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        };
        _notifyIcon.DoubleClick += OnDoubleClick;

        Log.Information("TrayIconController wired to TriggerSupervisor (state={State})", _supervisor.State);
    }

    private void OnSupervisorStateChanged(object? sender, TriggerStateChangedEventArgs e)
    {
        var state = TrayIconState.FromSupervisor(
            e.NewState,
            captureCount: (int)(_supervisor.Service?.CaptureCount ?? 0),
            crashCount: _supervisor.CrashCount);
        ApplyState(state);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        Log.Information("Tray icon double-clicked (state={State})", _supervisor.State);
        if (_supervisor.State == TriggerState.Running)
        {
            _supervisor.Stop();
        }
        else if (_supervisor.State == TriggerState.Stopped || _supervisor.State == TriggerState.Crashed)
        {
            try
            {
                _supervisor.Start(_configProvider());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Toggle-Start failed");
                ShowError("Failed to start recording", ex);
            }
        }
    }

    private static void ShowError(string title, Exception ex)
    {
        // Fire-and-forget; User sieht's, kann's wegklicken, läuft auf UI-Thread
        // (Click-Handler laufen auf UI-Thread)
        MessageBox.Show(
            $"{title}\n\n{ex.GetType().Name}: {ex.Message}",
            "AiRecall",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ApplyState(TrayIconState state)
    {
        if (_disposed) return;
        _statusItem.Text = $"{state.IconGlyph} {state.StatusText}";
        _startRecordingItem.Enabled = state.StartEnabled;
        _stopRecordingItem.Enabled = state.StopEnabled;
        _notifyIcon.Text = TruncateForTooltip(state.TooltipText, 63);
        Log.Debug("TrayIcon state applied: {StatusText} (start={Start}, stop={Stop})",
            state.StatusText, state.StartEnabled, state.StopEnabled);
    }

    /// <summary>NotifyIcon.Text is limited to 63 chars on Windows; truncate gracefully.</summary>
    private static string TruncateForTooltip(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _supervisor.StateChanged -= OnSupervisorStateChanged;
        _notifyIcon.DoubleClick -= OnDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        Log.Information("TrayIconController disposed");
    }
}