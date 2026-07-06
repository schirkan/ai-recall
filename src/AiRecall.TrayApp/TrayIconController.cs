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
    private readonly MenuImageCache _menuImages = new();
    // Welcher Tray-Icon-Key aktuell am _notifyIcon haengt. Idempotente
    // Updates: wir setzen das Icon nur bei State-Wechsel neu (sonst
    // flackert der Shell-Cache, und FromHandle kann zu HFON-Leak
    // fuehren).
    private string? _currentTrayIconKey;
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

        _statusItem = new ToolStripMenuItem("Stopped")
        {
            Enabled = false,   // Status-Item ist nicht klickbar
            Image = _menuImages.GetOrAdd("status-stopped", () => _menuImages.GetOrAddEmbeddedIcon("status-stopped.ico").ToBitmap())
        };
        _startRecordingItem = new ToolStripMenuItem("Start Recording")
        {
            ShortcutKeys = Keys.Control | Keys.S,
            Image = _menuImages.GetOrAdd("start", () => _menuImages.GetOrAddEmbeddedIcon("start.ico").ToBitmap())
        };
        _stopRecordingItem = new ToolStripMenuItem("Stop Recording")
        {
            ShortcutKeys = Keys.Control | Keys.T,
            Enabled = false,
            Image = _menuImages.GetOrAdd("stop", () => _menuImages.GetOrAddEmbeddedIcon("stop.ico").ToBitmap())
        };
        _showLogsItem = new ToolStripMenuItem("Live Logviewer…")
        {
            ShortcutKeys = Keys.Control | Keys.L,
            Enabled = false,   // aktiv in Schritt 5 (Spec 0008) — via EnableLogviewer()
            Image = _menuImages.GetOrAdd("logs", () => _menuImages.GetOrAddEmbeddedIcon("logs.ico").ToBitmap())
        };
        _settingsItem = new ToolStripMenuItem("Settings…")
        {
            ShortcutKeys = Keys.Control | Keys.Oemcomma,
            Enabled = false,   // aktiv in Schritt 6 (Spec 0009) — via EnableSettings()
            Image = _menuImages.GetOrAdd("settings", () => _menuImages.GetOrAddEmbeddedIcon("settings.ico").ToBitmap())
        };
        _quitItem = new ToolStripMenuItem("Quit")
        {
            ShortcutKeys = Keys.Control | Keys.Q,
            // Bug-Bash 2026-07-06 I-UE: Quit-Icon wird jetzt aus "x"-Emoji
            // gerendert statt aus embedded quit.ico. EmojiIconFactory nutzt
            // den gleichen COLR/CPAL-Pfad wie bei anderen Menu-Icons — falls
            // der User spaeter weitere Menu-Icons auf Emoji umstellt, bleibt
            // der Code konsistent. Grosse=SmallIconSize skaliert sauber
            // mit HiDPI (16/24/32).
            Image = _menuImages.GetOrAdd(
                "quit-emoji",
                () => EmojiIconFactory.RenderBitmap("❌", SystemInformation.SmallIconSize.Height))
        };

        _menu = new ContextMenuStrip();
        // Kein ImageScalingSize-Override: WinForms waehlt den Default-Slot
        // aus SystemInformation.SmallIconSize (16x16 @ 100% DPI, 24x24
        // @ 150%, 32x32 @ 200%). Das matched die RenderBitmap(size:16)-
        // Bitmaps bei Standard-DPI 1:1 und skaliert proportional bei HiDPI.
        // Fruehere Override-Versuche haben das Layout verschlechtert.
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

        // Tray-Icon = Capture-Indikator (Multi-Resolution .ico aus Embedded
        // Resource). Running -> 👁️ (Eye), sonst -> ⚫ (Black Circle). Die
        // .ico-Dateien werden vom EmojiIconGen-Tool generiert und ueber
        // AiRecall.TrayApp.csproj als EmbeddedResource eingebunden — kein
        // GDI+-Runtime-Rendering, daher zuverlaessig fuer NotifyIcon.
        _notifyIcon = new NotifyIcon
        {
            Icon = ResolveTrayIcon(_supervisor.State),
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
        _statusItem.Text = state.StatusText;
        _statusItem.Image = _menuImages.GetOrAdd(
            $"status-{state.IconGlyph}",
            () => _menuImages.GetOrAddEmbeddedIcon(StateGlyphToResource(state.IconGlyph)).ToBitmap());
        _startRecordingItem.Enabled = state.StartEnabled;
        _stopRecordingItem.Enabled = state.StopEnabled;
        UpdateTrayIcon();
        _notifyIcon.Text = TruncateForTooltip(state.TooltipText, 63);
        Log.Debug("TrayIcon state applied: {StatusText} (start={Start}, stop={Stop})",
            state.StatusText, state.StartEnabled, state.StopEnabled);
    }

    /// <summary>NotifyIcon.Text is limited to 63 chars on Windows; truncate gracefully.</summary>
    private static string TruncateForTooltip(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";

    /// <summary>
    /// Liefert das Tray-<see cref="Icon"/> fuer den aktuellen
    /// <see cref="TriggerState"/> aus dem Embedded-Resource-Cache.
    /// Running -> tray-recording.ico (👁️), alles andere -> tray-idle.ico
    /// (⚫). Bei Crashed bewusst Idle, weil die Aufnahme nicht laeuft.
    /// </summary>
    private Icon ResolveTrayIcon(TriggerState state)
    {
        var key = state == TriggerState.Running ? "tray-recording.ico" : "tray-idle.ico";
        return _menuImages.GetOrAddEmbeddedIcon(key);
    }

    /// <summary>
    /// Aktualisiert das Tray-Icon idempotent. Wir setzen das Icon nur
    /// bei State-Wechsel neu, weil NotifyIcon sonst den HFON-Cache
    /// staendig invalidiert und das Shell-Paint stoert.
    /// </summary>
    private void UpdateTrayIcon()
    {
        var key = _supervisor.State == TriggerState.Running ? "tray-recording.ico" : "tray-idle.ico";
        if (_currentTrayIconKey == key) return;
        _notifyIcon.Icon = ResolveTrayIcon(_supervisor.State);
        _currentTrayIconKey = key;
    }

    /// <summary>
    /// Mappt das <see cref="TrayIconState.IconGlyph"/> auf den
    /// Embedded-Resource-Namen der .ico-Datei. Die Status-Glyphen
    /// (🔴/🟡/🟢/⚠) sind fix in TrayIconState codiert — hier nur die
    /// Uebersetzung auf den Asset-Namen.
    /// </summary>
    private static string StateGlyphToResource(string glyph) => glyph switch
    {
        "🔴" => "status-stopped.ico",
        "🟡" => "status-starting.ico",
        "🟢" => "status-running.ico",
        "⚠" => "status-crashed.ico",
        _ => "status-stopped.ico"
    };

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
        // Items vor Menu-Dispose von ihren Images trennen, sonst doppelte
        // Freigabe (Menu.Dispose laeuft ueber die Images).
        _statusItem.Image = null;
        _startRecordingItem.Image = null;
        _stopRecordingItem.Image = null;
        _showLogsItem.Image = null;
        _settingsItem.Image = null;
        _quitItem.Image = null;
        _menu.Dispose();
        _menuImages.Dispose();
        Log.Information("TrayIconController disposed");
    }
}
