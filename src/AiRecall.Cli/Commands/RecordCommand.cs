using AiRecall.Cli.Logging;
using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;

namespace AiRecall.Cli.Commands;

/// <summary>
/// <c>recall record</c> — kontinuierliche Capture-Loop mit Trigger-Pipeline
/// (Spec 0005). Drückt Ctrl+C zum Beenden.
///
/// CLI ist nur ein temporärer Einstiegspunkt für MVP1 — MVP2 bringt eine
/// Tray-Icon-EXE, die <see cref="ITriggerService"/> direkt nutzt (siehe
/// Spec 0005 §Zukunft: MVP2 — Tray-Icon-EXE).
/// </summary>
internal static class RecordCommand
{
    public enum TriggerMode
    {
        /// <summary>Nur WinEventHook (default, MVP1-Produktion).</summary>
        Events,
        /// <summary>Nur Heartbeat-Polling (für Tests / Headless-Server ohne Message-Loop).</summary>
        Polling,
        /// <summary>WinEventHook + Heartbeat (sicherste Variante, höchster CPU-Verbrauch).</summary>
        Both
    }

    public static int Run(string[] args)
    {
        bool noOcr = false;
        bool headless = false;
        TriggerMode mode = TriggerMode.Events;
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-ocr":
                    noOcr = true;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--trigger-mode":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--trigger-mode requires an argument (events|polling|both)");
                        return 2;
                    }
                    var modeStr = args[++i].ToLowerInvariant();
                    if (!Enum.TryParse<TriggerMode>(modeStr, ignoreCase: true, out mode))
                    {
                        Console.Error.WriteLine($"Invalid --trigger-mode: {args[i]} (expected: events|polling|both)");
                        return 2;
                    }
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
        if (noOcr)
        {
            // Überschreibe OCR-Engine in der In-Memory-Config (Persistenz nicht nötig).
            config.Ocr.Engine = "";
        }

        // Trigger-Mode auf die In-Memory-Config anwenden.
        bool enableHook = mode is TriggerMode.Events or TriggerMode.Both;
        bool enableHeartbeat = mode is TriggerMode.Polling or TriggerMode.Both;

        Log.Logger = SerilogSetup.Create(config.Logging);
        var logger = Log.Logger;

        try
        {
            logger.Information(
                "recall record: starting (headless={Headless}, trigger-mode={Mode}, Ctrl+C to stop)",
                headless, mode);
            using var service = new TriggerService(config, logger,
                enableWinEventHook: enableHook,
                enableHeartbeat: enableHeartbeat);
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                logger.Information("Shutdown signal received");
            };

            service.Start();

            // Stats-Reporter: alle 30s einen Tick loggen.
            var lastStats = DateTimeOffset.Now;
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(500);
                if (!headless && (DateTimeOffset.Now - lastStats).TotalSeconds >= 30)
                {
                    logger.Information(
                        "Stats: captures={Captures}, skipped={Skipped}, throttled={Throttled}, dedup={Dedup}, blacklist={BL}, self={Self}, errors={Err}",
                        service.CaptureCount, service.SkippedCount, service.ThrottleCount,
                        service.DuplicateCount, service.BlacklistCount, service.SelfCaptureCount, service.ErrorCount);
                    lastStats = DateTimeOffset.Now;
                }
            }

            service.Stop();

            if (!headless)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"Stopped. Captures: {service.CaptureCount}, Throttled: {service.ThrottleCount}, " +
                    $"Dedup: {service.DuplicateCount}, Blacklist: {service.BlacklistCount}, Errors: {service.ErrorCount}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "recall record failed");
            if (!headless)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: recall record [options]");
        Console.WriteLine();
        Console.WriteLine("Continuous capture mode with trigger pipeline (Spec 0005).");
        Console.WriteLine("Uses SetWinEventHook for foreground/focus/name/value/scroll events,");
        Console.WriteLine("with optional Heartbeat polling as fallback. Throttle and per-HWND");
        Console.WriteLine("dedup are applied before the screenshot. Press Ctrl+C to stop.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --no-ocr                 Disable Tesseract OCR (faster, no text in MD).");
        Console.WriteLine("  --headless               Silent mode: only Serilog output, no console stats.");
        Console.WriteLine("                           (Intended for MVP2 tray-EXE and CI use.)");
        Console.WriteLine("  --trigger-mode <m>       Which trigger sources to enable:");
        Console.WriteLine("                             events   — only WinEventHook (default).");
        Console.WriteLine("                             polling  — only Heartbeat polling.");
        Console.WriteLine("                             both     — WinEventHook + Heartbeat.");
        Console.WriteLine("  --config <path>          Override the config JSON path.");
        Console.WriteLine("  -h, --help               Show this help.");
    }
}