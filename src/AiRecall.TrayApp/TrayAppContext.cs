using AiRecall.Core.Configuration;
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
    private AppConfig _config;

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

    protected override void ExitThreadCore()
    {
        _trayIcon.Dispose();
        _supervisor.Dispose();
        LogSink.Dispose();
        Log.CloseAndFlush();
        base.ExitThreadCore();
    }
}