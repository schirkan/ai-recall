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
    // Bug-Bash 2026-07-06 I-15: Capture-Counter aktualisiert sich waehrend
    // 'Running' nicht mehr von selbst, weil StateChanged nur bei
    // Zustandsuebergaengen feuert. StatusRefreshTimer pollt 1/s den Counter
    // und ruft ApplyState, solange der Supervisor laeuft.
    private readonly System.Windows.Forms.Timer _statusRefreshTimer;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler? ShowLogviewerRequested;
    public event EventHandler? ShowSettingsRequested;

    /// <summary>
    /// Zeigt eine Balloon-Tip am Tray-Icon an. Wird vom TrayAppContext fuer
    /// nicht-blockierende User-Hinweise verwendet (z. B. OCR-Init-Fehler).
    /// </summary>
    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning, int timeoutMs = 8000)
    {
        if (_disposed) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(timeoutMs);
    }

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
            ShortcutKeys = Keys.Control | Keys.S
        };
        _stopRecordingItem = new ToolStripMenuItem("⏸ Stop Recording")
        {
            ShortcutKeys = Keys.Control | Keys.T,
            Enabled = false
        };
        _showLogsItem = new ToolStripMenuItem("📋 Live Logviewer…")
        {
            ShortcutKeys = Keys.Control | Keys.L,
            Enabled = false   // aktiv in Schritt 5 (Spec 0008) — via EnableLogviewer()
        };
        _settingsItem = new ToolStripMenuItem("⚙ Settings…")
        {
            ShortcutKeys = Keys.Control | Keys.Oemcomma,
            Enabled = false   // aktiv in Schritt 6 (Spec 0009) — via EnableSettings()
        };
        _quitItem = new ToolStripMenuItem("🚪 Quit")
        {
            ShortcutKeys = Keys.Control | Keys.Q
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

        // Bug-Bash 2026-07-06 I-15: 1s-Tick zum Capture-Counter-Refresh.
        // Wird in OnSupervisorStateChanged gesteuert (Start/Stop) statt im
        // Timer selbst, damit der Refresh exakt an die Lifecycle-Phasen
        // gekoppelt ist.
        _statusRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _statusRefreshTimer.Tick += (_, _) => RefreshStatus();
        _statusRefreshTimer.Start();

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
        // Beim Stop die letzte Counter-Synchronisation noch einmal forcieren,
        // damit der Status nicht "Running — N captures" einfriert, nachdem
        // der Timer gleich pausiert.
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_disposed) return;
        var state = TrayIconState.FromSupervisor(
            _supervisor.State,
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
        _statusRefreshTimer.Stop();
        _statusRefreshTimer.Dispose();
        _supervisor.StateChanged -= OnSupervisorStateChanged;
        _notifyIcon.DoubleClick -= OnDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        Log.Information("TrayIconController disposed");
    }
}