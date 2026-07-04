using Serilog;

namespace AiRecall.TrayApp;

/// <summary>
/// ApplicationContext-Wrapper für die Tray-EXE. Lebt solange wie die Tray-Anwendung läuft;
/// hält den <see cref="TrayIconController"/> und disposet ihn sauber beim Beenden.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly TrayIconController _trayIcon;

    public TrayAppContext(System.Threading.Mutex singleInstanceMutex)
    {
        // Serilog global einrichten. Logs werden in logs/trayapp-yyyy-MM-dd.log rotiert.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                "logs/trayapp-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _trayIcon = new TrayIconController();
        _trayIcon.ExitRequested += (_, _) =>
        {
            Log.Information("Exit requested from tray menu");
            ExitThread();
        };

        Log.Information("AiRecall TrayApp started (single-instance mutex held)");
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Dispose();
        Log.CloseAndFlush();
        base.ExitThreadCore();
    }
}