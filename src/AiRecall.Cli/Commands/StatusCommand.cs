using System.Text.Json;
using AiRecall.Core.Configuration;

namespace AiRecall.Cli.Commands;

/// <summary>
/// <c>recall status</c> — Diagnose-Übersicht über Config, Trigger-Einstellungen
/// und Tagesstatistik. Liest ausschließlich von Disk; kein laufender
/// TriggerService nötig.
///
/// Geplant für MVP2-IPC: die Tray-EXE wird periodisch eine Status-Datei
/// aktualisieren, die <c>recall status</c> anzeigt. Aktuell (MVP1) zeigen
/// wir einfach Config + heutige Capture-Counts.
/// </summary>
internal static class StatusCommand
{
    public static int Run(string[] args)
    {
        bool jsonOutput = false;
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    jsonOutput = true;
                    break;
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--config requires a path argument");
                        return 2;
                    }
                    configPath = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
            }
        }

        var config = ConfigLoader.Load(configPath);
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var todayDir = Path.Combine(Path.GetFullPath(config.Capture.RootPath), today);
        var totalToday = CountFiles(todayDir, "*.png");
        var byProcess = CountByProcess(todayDir);

        if (jsonOutput)
        {
            var payload = new
            {
                configPath = configPath ?? ConfigLoader.DefaultUserConfigPath(),
                capture = new
                {
                    rootPath = config.Capture.RootPath,
                    today,
                    todayCount = totalToday,
                    byProcess
                },
                trigger = new
                {
                    throttleMs = config.Trigger.ThrottleMs,
                    heartbeatIntervalSeconds = config.Trigger.HeartbeatIntervalSeconds,
                    winEvents = new
                    {
                        foreground = config.Trigger.WinEvents.Foreground,
                        focus = config.Trigger.WinEvents.Focus,
                        nameChange = config.Trigger.WinEvents.NameChange,
                        valueChange = config.Trigger.WinEvents.ValueChange,
                        scroll = config.Trigger.WinEvents.Scroll,
                        menuPopup = config.Trigger.WinEvents.MenuPopup
                    },
                    blacklist = new
                    {
                        processes = config.Trigger.Blacklist.Processes,
                        windowClasses = config.Trigger.Blacklist.WindowClasses
                    }
                },
                ocr = new
                {
                    engine = config.Ocr.Engine
                }
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("AI Recall — Status");
            Console.WriteLine();
            Console.WriteLine($"Config:          {configPath ?? ConfigLoader.DefaultUserConfigPath()}");
            Console.WriteLine($"Capture root:    {config.Capture.RootPath}");
            Console.WriteLine($"Today ({today}): {totalToday} captures");
            if (byProcess.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("By process:");
                foreach (var kv in byProcess.OrderByDescending(kv => kv.Value))
                {
                    Console.WriteLine($"  {kv.Value,4}  {kv.Key}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("Trigger config:");
            Console.WriteLine($"  WinEvents:  fg={YesNo(config.Trigger.WinEvents.Foreground)}, " +
                              $"focus={YesNo(config.Trigger.WinEvents.Focus)}, " +
                              $"name={YesNo(config.Trigger.WinEvents.NameChange)}, " +
                              $"value={YesNo(config.Trigger.WinEvents.ValueChange)}, " +
                              $"scroll={YesNo(config.Trigger.WinEvents.Scroll)}, " +
                              $"menu={YesNo(config.Trigger.WinEvents.MenuPopup)}");
            Console.WriteLine($"  Throttle:   {config.Trigger.ThrottleMs} ms per HWND");
            Console.WriteLine($"  Heartbeat:  {(config.Trigger.HeartbeatIntervalSeconds == 0 ? "off" : config.Trigger.HeartbeatIntervalSeconds + " s")}");
            if (config.Trigger.Blacklist.Processes.Count > 0)
            {
                Console.WriteLine($"  Blacklist processes: {string.Join(", ", config.Trigger.Blacklist.Processes)}");
            }
            if (config.Trigger.Blacklist.WindowClasses.Count > 0)
            {
                Console.WriteLine($"  Blacklist classes:   {string.Join(", ", config.Trigger.Blacklist.WindowClasses)}");
            }
            Console.WriteLine();
            Console.WriteLine($"OCR engine:     {config.Ocr.Engine}");
        }
        return 0;
    }

    private static int CountFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Length; }
        catch { return 0; }
    }

    private static Dictionary<string, int> CountByProcess(string dir)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return result;
        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(subDir);
                var count = Directory.GetFiles(subDir, "*.png").Length;
                if (count > 0) result[name] = count;
            }
        }
        catch { /* ignore */ }
        return result;
    }

    private static string YesNo(bool v) => v ? "on" : "off";

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: recall status [options]");
        Console.WriteLine();
        Console.WriteLine("Shows diagnostic information: config paths, today's capture counts,");
        Console.WriteLine("and the active trigger configuration. Reads from disk only; no");
        Console.WriteLine("running trigger service required.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json                 Output as JSON instead of human-readable text.");
        Console.WriteLine("  --config <path>        Override the config JSON path.");
        Console.WriteLine("  -h, --help             Show this help.");
    }
}