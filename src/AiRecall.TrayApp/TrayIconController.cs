using Serilog;

namespace AiRecall.TrayApp;

/// <summary>
/// Verwaltet das NotifyIcon + ContextMenuStrip im System-Tray.
/// Schritt 1-Scope (Spec 0006): nur Skeleton mit Platzhalter-Menu-Items.
/// Start/Stop Recording ist deaktiviert bis ProcessSupervisor (Schritt 2) und
/// MmfSink (Schritt 3) implementiert sind. Logviewer und Settings werden in
/// Schritt 5/6 (Specs 0008/0009) hinzugefügt.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startRecordingItem;
    private readonly ToolStripMenuItem _stopRecordingItem;
    private readonly ToolStripMenuItem _showLogsItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _quitItem;
    private bool _disposed;

    public event EventHandler? ExitRequested;

    public TrayIconController()
    {
        _startRecordingItem = new ToolStripMenuItem("▶ Start Recording")
        {
            ShortcutKeys = Keys.S,
            Enabled = false   // aktiv in Schritt 2 wenn ProcessSupervisor steht
        };
        _stopRecordingItem = new ToolStripMenuItem("⏸ Stop Recording")
        {
            ShortcutKeys = Keys.T,
            Enabled = false
        };
        _showLogsItem = new ToolStripMenuItem("📋 Live Logviewer…")
        {
            ShortcutKeys = Keys.L,
            Enabled = false   // aktiv in Schritt 5 (Spec 0008)
        };
        _settingsItem = new ToolStripMenuItem("⚙ Settings…")
        {
            ShortcutKeys = Keys.Oemcomma,
            Enabled = false   // aktiv in Schritt 6 (Spec 0009)
        };
        _quitItem = new ToolStripMenuItem("🚪 Quit")
        {
            ShortcutKeys = Keys.Q
        };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _startRecordingItem,
            _stopRecordingItem,
            new ToolStripSeparator(),
            _showLogsItem,
            _settingsItem,
            new ToolStripSeparator(),
            _quitItem
        });

        // TODO (Schritt 1+): tray-icon.ico aus Resources laden. Erstmal SystemIcons.Application.
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AiRecall — Skeleton (Schritt 1)",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += OnDoubleClick;
        _quitItem.Click += (_, _) =>
        {
            Log.Information("Quit menu item clicked");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        };

        Log.Information("TrayIconController initialized (menu has {Count} items)", _menu.Items.Count);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        Log.Information("Tray icon double-clicked");
        // Toggle Start/Stop folgt mit ProcessSupervisor in Schritt 2
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick -= OnDoubleClick;
        _notifyIcon.Dispose();
        _menu.Dispose();
        Log.Information("TrayIconController disposed");
    }
}