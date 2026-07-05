using AiRecall.Core.Configuration;
using AiRecall.TrayApp.Windows;
using AiRecall.Trigger;
using Serilog;

namespace AiRecall.TrayApp;

/// <summary>
/// ApplicationContext-Wrapper für die Tray-EXE. Lebt solange wie die Tray-Anwendung läuft;
/// hält den <see cref="TriggerSupervisor"/> und <see cref="TrayIconController"/>.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly TriggerSupervisor _supervisor;
    private readonly TrayIconController _trayIcon;
    private readonly LogviewerSession _logSession;
    private LogviewerWindow? _logviewer;
    private SettingsDialog? _settingsDialog;
    private AppConfig _config;
    // Bug-Bash 2026-07-05 I-10: Idempotent-Dispose-Flag. Ohne dieses Flag
    // wuerde ein wiederholter ExitThreadCore-Aufruf _trayIcon und _supervisor
    // doppelt disposen (die haben zwar eigene _disposed-Checks, aber Defense
    // in Depth und symmetrisches Verhalten zu allen anderen IDisposable-
    // Komponenten im TrayAppContext).
    private bool _disposed;

    /// <summary>
    /// In-memory ring buffer for live log streaming. Subscribed by
    /// <c>LogviewerWindow</c> in Schritt 5. Public so that window code can
    /// read it without going through service-locator gymnastics.
    /// </summary>
    public InMemoryLogSink LogSink { get; }

    public TrayAppContext(System.Threading.Mutex singleInstanceMutex)
    {
        _config = UserConfigLocator.LoadOrDefault(msg => Log.Warning("Config: {Msg}", msg));

        // InMemoryLogSink zuerst erzeugen, dann in Serilog-Logger einhängen.
        // Schritt 5 (LogviewerWindow) subscribed auf sink.EventEmitted.
        LogSink = new InMemoryLogSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(LogSink)
            .WriteTo.File(
                "logs/trayapp-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Loaded config from {Path}", UserConfigLocator.GetUserConfigPath());

        _supervisor = new TriggerSupervisor(Log.Logger.ForContext<TriggerSupervisor>());
        _supervisor.StateChanged += (_, e) =>
            Log.Information("Supervisor state: {Old} -> {New}", e.OldState, e.NewState);

        _trayIcon = new TrayIconController(_supervisor, () => _config);
        _trayIcon.ExitRequested += (_, _) =>
        {
            Log.Information("Exit requested from tray menu");
            ExitThread();
        };
        _trayIcon.ShowLogviewerRequested += (_, _) => ShowLogviewer();
        _trayIcon.ShowSettingsRequested += (_, _) => ShowSettings();

        _logSession = new LogviewerSession(LogSink);
        // ShowLogviewerItem aktivieren (Spec 0008 Schritt 5)
        _trayIcon.EnableLogviewer();
        // Settings-Item aktivieren (Spec 0009 Schritt 6)
        _trayIcon.EnableSettings();

        Log.Information("AiRecall TrayApp ready (config={Path})", UserConfigLocator.GetUserConfigPath());
    }

    /// <summary>
    /// Updates the in-memory config and triggers a hot-reload via
    /// <see cref="TriggerSupervisor.Restart"/>. Called by SettingsDialog on save
    /// (Spec 0009 §Hot-Reload).
    /// </summary>
    public void ApplyConfig(AppConfig newConfig)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        _config = newConfig;
        Log.Information("Hot-reloading supervisor with new config");
        _supervisor.Restart(newConfig);
    }

    /// <summary>The live in-memory config (used by SettingsDialog to populate fields).</summary>
    public AppConfig CurrentConfig => _config;

    private void ShowLogviewer()
    {
        if (_logviewer is null || _logviewer.IsDisposed)
        {
            _logviewer = new LogviewerWindow(_logSession);
            _logviewer.FormClosed += (_, _) => _logviewer = null;
        }
        _logviewer.Show();
        _logviewer.BringToFront();
    }

    private void ShowSettings()
    {
        if (_settingsDialog is null || _settingsDialog.IsDisposed)
        {
            _settingsDialog = new SettingsDialog(_config, onSave: newConfig =>
            {
                _config = newConfig;
                Log.Information("Settings saved, hot-reloading supervisor");
                _supervisor.Restart(newConfig);
            });
            _settingsDialog.FormClosed += (_, _) => _settingsDialog = null;
        }
        _settingsDialog.Show();
        _settingsDialog.BringToFront();
    }

    protected override void ExitThreadCore()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _trayIcon.Dispose();
            _supervisor.Dispose();
        }
        finally
        {
            // LogSink + Logger IMMER disposen, auch wenn oben was wirft
            // (Tesseract tessdata-File-Handles, Serilog-Buffer, etc.)
            try { LogSink.Dispose(); } catch (Exception ex) { Serilog.Log.Warning(ex, "LogSink dispose failed"); }
            Serilog.Log.CloseAndFlush();
        }
        base.ExitThreadCore();
    }
}