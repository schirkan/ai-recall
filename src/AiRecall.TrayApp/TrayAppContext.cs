using AiRecall.Core.Configuration;
using AiRecall.Core.Tessdata;
using AiRecall.TrayApp.Tessdata;
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

        // Spec 0012: First-Run-Dialog, wenn Tesseract konfiguriert ist und
        // tessdata für die konfigurierten Sprachen fehlt. Ersetzt das passive
        // Balloon aus Bug-Bash I-14.
        MaybeOfferTessdataDownload();
    }

    private void MaybeOfferTessdataDownload()
    {
        var manager = new TessdataManager();
        var missing = manager.FindMissingLanguages(_config);
        if (missing.Count == 0) return;

        Log.Warning("Missing tessdata for {Count} language(s): {Langs}",
            missing.Count, string.Join(", ", missing.Select(m => m.Code)));

        // Beim ersten Start kann das TrayApp-Hauptfenster noch nicht da sein,
        // NotifyIcon ist aber sichtbar — deshalb Owner=null (Balloon-Auslöser
        // übernimmt die Aufmerksamkeit). Spec 0012 §Auslöser.
        using var dialog = new TessdataFirstRunDialog(
            missing,
            manager,
            onPersistNeverAskAgain: disable =>
            {
                _config.Ocr.AutoDownloadTessdata = disable;
                TryPersistOcrConfig();
            });

        var result = dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case TessdataFirstRunDialog.DialogChoice.DownloadNow:
                Log.Information("User chose to download tessdata now (missing was {Count})", missing.Count);
                break;
            case TessdataFirstRunDialog.DialogChoice.Later:
                _trayIcon.ShowBalloon(
                    title: "AiRecall — OCR deaktiviert",
                    text: "tessdata-Dateien fehlen weiterhin. Captures laufen ohne OCR.",
                    icon: ToolTipIcon.Warning,
                    timeoutMs: 8000);
                break;
            case TessdataFirstRunDialog.DialogChoice.NeverAskAgain:
                Log.Information("User opted out of tessdata auto-download prompt");
                break;
        }
    }

    /// <summary>
    /// Persistiert die aktuelle OCR-Konfiguration (mit ggf. geändertem
    /// <c>autoDownloadTessdata</c>-Flag) zurück in
    /// <c>%APPDATA%/AiRecall/config.json</c>. Atomar via bestehender
    /// Settings-Persist-Logik; siehe Spec 0009 §Persistenz.
    /// </summary>
    private void TryPersistOcrConfig()
    {
        try
        {
            var path = UserConfigLocator.GetUserConfigPath();
            if (!File.Exists(path))
            {
                Log.Information("No user config at {Path} yet; nothing to persist (defaults already cover autoDownloadTessdata=false)", path);
                return;
            }
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = new System.Text.Json.Nodes.JsonObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                root[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            }
            var ocr = root["ocr"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            ocr["autoDownloadTessdata"] = _config.Ocr.AutoDownloadTessdata;
            root["ocr"] = ocr;
            var bak = path + ".bak";
            if (File.Exists(path)) File.Copy(path, bak, overwrite: true);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(tmp, path, overwrite: true);
            Log.Information("Persisted ocr.autoDownloadTessdata={Flag}", _config.Ocr.AutoDownloadTessdata);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist ocr.autoDownloadTessdata");
        }
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