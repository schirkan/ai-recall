using AiRecall.Cli.Logging;
using AiRecall.Conversion;
using AiRecall.Core.Configuration;
using Serilog;

namespace AiRecall.Cli.Commands;

/// <summary>
/// <c>recall convert</c> — Recovery-Subcommand (Spec 0007).
///
/// Scannt das Capture-Verzeichnis nach MD-Files mit
/// <c>conversion: pending</c> und fuegt sie in die Channel-Queue eines
/// neuen <see cref="ConversionWorker"/> ein. Wartet, bis alle verarbeitet
/// sind, und gibt Stats aus.
///
/// Use-Cases:
///   - Crash-Recovery: nach einem `recall record`-Crash bleiben
///     pending-MD-Files liegen, die hier nachkonvertiert werden.
///   - Manueller Batch-Run nach grossem Capture-Tag.
///   - CI/Test: Conversion ohne laufenden TriggerService testen.
/// </summary>
internal static class ConvertCommand
{
    public static int Run(string[] args)
    {
        string? configPath = null;
        string? captureRootOverride = null;
        int maxWaitSeconds = 60;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (i + 1 >= args.Length) { PrintUsage(); return 2; }
                    configPath = args[++i];
                    break;
                case "--path":
                    if (i + 1 >= args.Length) { PrintUsage(); return 2; }
                    captureRootOverride = args[++i];
                    break;
                case "--max-wait":
                    if (i + 1 >= args.Length) { PrintUsage(); return 2; }
                    if (!int.TryParse(args[++i], out maxWaitSeconds) || maxWaitSeconds < 1) { PrintUsage(); return 2; }
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        var config = ConfigLoader.Load(configPath);

        var captureRoot = captureRootOverride ?? config.Capture.RootPath;
        if (!Directory.Exists(captureRoot))
        {
            Console.Error.WriteLine($"Capture directory does not exist: {captureRoot}");
            return 1;
        }

        using var logger = SerilogSetup.Create(config.Logging);
        var pendingFiles = ScanPendingCaptures(captureRoot);
        logger.Information("Found {Count} pending captures in {Root}", pendingFiles.Count, captureRoot);

        if (pendingFiles.Count == 0)
        {
            Console.WriteLine("No pending captures found.");
            return 0;
        }

        // Konvertierung starten — OCR wird uebersprungen (NullOcrEngine),
        // weil Tesseract tessdata-Setup im CLI-Kontext unzuverlaessig ist.
        // Spec 0007 v0.4: Recall convert = Recovery, OCR ist optional.
        using var worker = new ConversionWorker(config, logger, ocrEngine: new NullOcrEngine());

        foreach (var mdPath in pendingFiles)
        {
            worker.EnqueueAsync(mdPath).AsTask().Wait(TimeSpan.FromSeconds(2));
        }

        // Auf Abschluss warten
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        while (DateTime.UtcNow < deadline && worker.PendingCount > 0)
        {
            Thread.Sleep(200);
        }

        worker.Stop();

        var stats = $"done={worker.CompletedCount} partial={worker.PartialCount} failed={worker.FailedCount} ocrSkipped={worker.OcrSkippedCount}";
        Console.WriteLine($"Conversion complete: {stats}");
        logger.Information("recall convert: {Stats}", stats);

        return worker.FailedCount > 0 ? 1 : 0;
    }

    private static List<string> ScanPendingCaptures(string captureRoot)
    {
        var result = new List<string>();
        foreach (var mdPath in Directory.EnumerateFiles(captureRoot, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(mdPath);
                if (content.Contains("conversion: \"pending\"", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(mdPath);
                }
            }
            catch
            {
                // Datei evtl. gesperrt oder unlesbar — ueberspringen
            }
        }
        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("recall convert — recover pending captures");
        Console.WriteLine();
        Console.WriteLine("Usage: recall convert [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --config <path>      Path to config JSON file.");
        Console.WriteLine("  --path <dir>         Capture root override (default: capture.rootPath from config).");
        Console.WriteLine("  --max-wait <sec>     Max seconds to wait for completion (default 60).");
        Console.WriteLine("  -h, --help           Show this help.");
    }
}